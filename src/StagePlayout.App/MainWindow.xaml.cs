using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StagePlayout.App.Services;
using StagePlayout.App.Video;
using StagePlayout.Core.Models;
using StagePlayout.Core.Services;

namespace StagePlayout.App;

public partial class MainWindow : Window
{
    private readonly Playlist _playlist = new();
    private readonly MediaPool _mediaPool = new();
    private readonly CompanionControl _companion = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Engine de programa: FFDecoders próprios alimentam o compositor GPU
    // (2 slots com crossfade real). Preload = decoder aberto em pausa no 1.º frame.
    private readonly FFDecoder?[] _slotDec = new FFDecoder?[2];
    private readonly HashSet<int> _closingSlots = new();
    private int _liveSlot = -1;
    private FFDecoder? _standbyDec;
    private Cue? _standbyCue;
    private bool _compWired;
    private readonly HashSet<int> _closingLayerSlots = new();

    // Geração de transições: invalida callbacks de fades antigos (evita stops/fora de época)
    private int _transitionGen;



    // Layers flutuantes L1/L2 (índice 1 e 2) — slots 2 e 3 do compositor
    private class LayerState
    {
        public FFDecoder? Decoder;
        public string? File;
        public bool Visible;
        public bool Muted = true; // layers começam mudas (logos/lower-thirds)
        public double X, Y, W, H;
    }
    private bool _masterMuted;
    private readonly LayerState[] _layers =
    {
        null!, // índice 0 não usado
        new LayerState { X = 0.68, Y = 0.66, W = 0.28, H = 0.28 }, // L1: canto inf. direito
        new LayerState { X = 0.68, Y = 0.04, W = 0.28, H = 0.28 }, // L2: canto sup. direito
    };

    // Atalhos configuráveis (shortcuts.json na pasta do exe)
    private readonly ShortcutConfig _shortcuts = ShortcutConfig.Load();
    private Key _keyGo, _keyNext, _keyPrev, _keyStop, _keyPause;

    private OutputWindow? _output;
    private string _outputInfo = "—";

    private System.ComponentModel.ICollectionView? _cuesView;

    public MainWindow()
    {
        InitializeComponent();

        // view com filtro: filhos de grupos colapsados ficam escondidos
        _cuesView = System.Windows.Data.CollectionViewSource.GetDefaultView(_playlist.Cues);
        _cuesView.Filter = o => o is Cue c && (!c.IsChild || IsParentExpanded(c));
        CueList.ItemsSource = _cuesView;

        _playlist.CurrentChanged += (_, _) => OnCurrentChanged();

        PreloadNext();

        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        HookCompanion();
        _companion.Start();

        _keyGo = ParseKey(_shortcuts.Go, Key.Space);
        _keyNext = ParseKey(_shortcuts.Next, Key.Right);
        _keyPrev = ParseKey(_shortcuts.Previous, Key.Left);
        _keyStop = ParseKey(_shortcuts.Stop, Key.S);
        _keyPause = ParseKey(_shortcuts.Pause, Key.P);

        Loaded += (_, _) => RefreshGeomEditor();

        UpdateStatus();
    }

    private static Key ParseKey(string? name, Key fallback)
        => Enum.TryParse<Key>(name ?? "", true, out var key) ? key : fallback;

    // ===== Motor de vídeo (Flyleaf A/B com preload) =====

    // ===== Transições (compositor GPU — crossfade real) =====

    private void TransitionTo(Cue cue)
    {
        SetOutput(true);
        // garantir que o compositor já existe (hwnd criado no load da janela)
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => DoTransition(cue)));
    }

    private void DoTransition(Cue cue)
    {
        if (_output?.Compositor is not { } comp) return;

        if (!_compWired)
        {
            _compWired = true;
            comp.FadeCompleted += OnFadeCompleted;
        }

        var gen = ++_transitionGen;

        if (_standbyDec is not null && _standbyCue?.Id == cue.Id)
        {
            // caminho rápido: pré-carregado e pausado no 1.º frame -> GO instantâneo
            var incoming = _standbyDec;
            _standbyDec = null;
            _standbyCue = null;
            StartTransitionSlot(cue, incoming, gen);
        }
        else
        {
            // não estava pronto: abrir em BACKGROUND (nunca bloquear a UI),
            // entrar só se ainda for a transição mais recente
            _standbyDec?.Dispose();
            _standbyDec = null;
            _standbyCue = null;
            TxtStatus.Text = $"A abrir {cue.Name}...";

            Task.Run(() =>
            {
                var dec = new FFDecoder();
                if (!dec.Open(cue.FilePath, autoPlay: false))
                {
                    var err = dec.Error;
                    dec.Dispose();
                    Dispatcher.BeginInvoke(() =>
                        TxtStatus.Text = $"ERRO ao abrir {cue.Name}: {err}");
                    return;
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _transitionGen) { dec.Dispose(); return; }
                    StartTransitionSlot(cue, dec, gen);
                });
            });
        }
    }

    private void StartTransitionSlot(Cue cue, FFDecoder incoming, int gen)
    {
        if (_output?.Compositor is not { } comp)
        {
            Video.VideoLog.Write("StartTransitionSlot: compositor NULL!");
            incoming.Dispose();
            return;
        }

        var newSlot = _liveSlot == 0 ? 1 : 0;
        Video.VideoLog.Write($"Transição -> slot {newSlot}: {cue.Name}");

        incoming.Loop = cue.End == CueEnd.Loop;
        incoming.Volume = VolumeSlider.Value / 100.0;
        incoming.Ended += Decoder_Ended;

        _slotDec[newSlot]?.Dispose();
        _slotDec[newSlot] = incoming;

        if (cue.Duration == TimeSpan.Zero && incoming.DurationTicks > 0)
            cue.Duration = TimeSpan.FromTicks(incoming.DurationTicks);

        comp.SetSource(newSlot, incoming);
        comp.SetGeometry(newSlot, 0, 0, 1, 1);
        comp.SetZ(newSlot, gen);            // o mais recente desenha por cima
        comp.SetOpacity(newSlot, 0, 0);
        ApplyVolumes();                     // respeita master mute
        incoming.Play();
        comp.SetOpacity(newSlot, 1, cue.FadeInSeconds);   // fade-in
        // (layers têm prioridade fixa no draw order do compositor)

        // Outgoing: fade-out e dispose no fim do fade
        var oldSlot = _liveSlot;
        if (oldSlot >= 0 && oldSlot != newSlot && _slotDec[oldSlot] is not null)
        {
            _closingSlots.Add(oldSlot);
            comp.SetOpacity(oldSlot, 0, cue.FadeOutSeconds);
        }

        _liveSlot = newSlot;

        BtnPause.IsEnabled = true;
        BtnStop.IsEnabled = true;
        BtnPause.Content = "PAUSE";

        UpdateStatus();
        PreloadNext();
    }

    private void OnFadeCompleted(int slot)
    {
        // render thread -> BeginInvoke (NUNCA Invoke: bloqueava o render se a UI estiver ocupada)
        Dispatcher.BeginInvoke(() =>
        {
            // slots de layers (2,3)
            if (slot >= 2)
            {
                if (!_closingLayerSlots.Remove(slot)) return;
                var li = slot - 1;
                _layers[li].Decoder?.Dispose();
                _layers[li].Decoder = null;
                _output?.Compositor?.SetSource(slot, null);
                return;
            }

            // slots de programa (0,1)
            if (!_closingSlots.Contains(slot)) return;
            _closingSlots.Remove(slot);
            _slotDec[slot]?.Dispose();
            _slotDec[slot] = null;
            _output?.Compositor?.SetSource(slot, null);
        });
    }

    private void Decoder_Ended(object? sender, EventArgs e)
    {
        // pump thread -> BeginInvoke
        Dispatcher.BeginInvoke(() =>
        {
            if (_liveSlot < 0 ||
                !ReferenceEquals(sender, _slotDec[_liveSlot]) ||
                _playlist.Current is not { } cue)
                return;

            // Loop de grupo: último filho de um grupo com "Repetir grupo" → volta ao primeiro
            if (cue.End != CueEnd.Loop)
            {
                var parent = _playlist.Cues.FirstOrDefault(c => c.Id == cue.ParentId);
                if (parent is { LoopGroup: true })
                {
                    var children = _playlist.Cues.Where(c => c.ParentId == parent.Id).ToList();
                    if (children.Count > 0 && ReferenceEquals(children[^1], cue))
                    {
                        var firstIdx = _playlist.Cues.IndexOf(children[0]);
                        if (_playlist.Select(firstIdx) is { } first)
                            TransitionTo(first);
                        return;
                    }
                }
            }

            switch (cue.End)
            {
                case CueEnd.AutoContinue:
                    Go();
                    break;
                case CueEnd.Stop:
                    StopPlayback(); // fade out (segundos do cue) e stop
                    break;
                // HoldLastFrame: fica parado no último frame (nada a fazer)
                // Loop: tratado internamente pelo decoder
            }
        });
    }

    /// <summary>Próximo cue fica aberto e pausado no 1.º frame, pronto para GO instantâneo.</summary>
    private void PreloadNext()
    {
        var next = _playlist.PeekNext(1);
        if (next is null)
        {
            _standbyCue = null;
            UpdateStatus();
            return;
        }
        if (_standbyCue?.Id == next.Id && _standbyDec is not null) return;

        _standbyCue = next;
        _standbyDec?.Dispose();
        _standbyDec = null;

        var dec = new FFDecoder();
        Task.Run(() =>
        {
            if (!dec.Open(next.FilePath, autoPlay: false))
            {
                dec.Dispose();
                return;
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (_standbyCue?.Id != next.Id)
                {
                    dec.Dispose(); // entretanto a ordem mudou
                    return;
                }
                if (next.Duration == TimeSpan.Zero && dec.DurationTicks > 0)
                    next.Duration = TimeSpan.FromTicks(dec.DurationTicks);
                _standbyDec = dec;
                UpdateStatus();
            });
        });
    }

    private Cue? _lastLiveCue;

    private static readonly SolidColorBrush CritBrush = Freeze(Color.FromRgb(0xEF, 0x44, 0x44));

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Timecode grande do header: restante do clip no ar, com cores de alerta.</summary>
    private void UpdateBigRemaining(TimeSpan? remaining)
    {
        if (remaining is null)
        {
            TxtBigRemaining.Text = "--:--:--";
            TxtBigRemaining.Foreground = (Brush)FindResource("TextMutedBrush");
            return;
        }

        var r = remaining.Value;
        TxtBigRemaining.Text = Cue.Fmt(r);
        TxtBigRemaining.Foreground =
            r.TotalSeconds <= 5 ? CritBrush : Brushes.White;
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        // painel A SEGUIR
        var next = _playlist.PeekNext(1);
        var nextName = next?.Name ?? "—";
        if (TxtNextCue.Text != nextName) TxtNextCue.Text = nextName;
        if (NextThumb.Source != next?.Thumbnail) NextThumb.Source = next?.Thumbnail;

        // tracking do cue no ar (barra de load / tempo restante na lista)
        var current = _playlist.Current;
        if (!ReferenceEquals(current, _lastLiveCue))
        {
            if (_lastLiveCue is not null)
            {
                _lastLiveCue.IsLive = false;
                _lastLiveCue.Progress = 0;
                if (_lastLiveCue.Duration != TimeSpan.Zero)
                    _lastLiveCue.TimeText = Cue.Fmt(_lastLiveCue.Duration);
            }
            _lastLiveCue = current;
        }

        var live = _liveSlot >= 0 ? _slotDec[_liveSlot] : null;
        if (live is null)
        {
            UpdateBigRemaining(null);
            return;
        }

        var cur = TimeSpan.FromTicks(live.CurTimeTicks);
        var dur = TimeSpan.FromTicks(live.DurationTicks);
        var remaining = dur > cur ? dur - cur : TimeSpan.Zero;
        TxtTime.Text = $"{cur:hh\\:mm\\:ss} / -{remaining:hh\\:mm\\:ss}";

        UpdateBigRemaining(live.DurationTicks > 0 ? remaining : null);

        if (current is not null)
        {
            current.IsLive = true;
            if (live.DurationTicks > 0)
            {
                current.Progress = Math.Clamp((double)live.CurTimeTicks / live.DurationTicks, 0, 1);
                current.TimeText = $"{Cue.Fmt(cur)} / -{Cue.Fmt(remaining)}";
            }
        }

        // loop pode ser ligado/desligado em direto (loop nativo no decoder)
        if (current is not null)
            live.Loop = current.End == CueEnd.Loop;
    }

    // ===== Companion / Stream Deck (OSC) =====

    private void HookCompanion()
    {
        _companion.GoRequested += (_, _) => Dispatcher.Invoke(Go);
        _companion.NextRequested += (_, _) => Dispatcher.Invoke(Go);
        _companion.PreviousRequested += (_, _) => Dispatcher.Invoke(Previous);
        _companion.PauseRequested += (_, _) => Dispatcher.Invoke(TogglePause);
        _companion.StopRequested += (_, _) => Dispatcher.Invoke(StopPlayback);
        _companion.PlayCueRequested += (_, n) => Dispatcher.Invoke(() =>
        {
            if (_playlist.Select(n - 1) is { } cue)
                TransitionTo(cue);
        });
        _companion.VolumeRequested += (_, v) =>
            Dispatcher.Invoke(() => VolumeSlider.Value = Math.Clamp(v, 0.0, 1.0) * 100);
        _companion.OutputRequested += (_, open) => Dispatcher.Invoke(() => SetOutput(open));
        _companion.LayerVisibilityRequested += (_, t) => Dispatcher.Invoke(() =>
        {
            if (t.Layer is 1 or 2) SetLayerVisible(t.Layer, t.Show);
        });
        _companion.LayerToggleRequested += (_, layer) => Dispatcher.Invoke(() =>
        {
            if (layer is 1 or 2) SetLayerVisible(layer, !_layers[layer].Visible);
        });
        _companion.MasterMuteRequested += (_, muted) => Dispatcher.Invoke(() => SetMasterMute(muted));
        _companion.MasterMuteToggleRequested += (_, _) => Dispatcher.Invoke(() => SetMasterMute(!_masterMuted));
        _companion.LayerMuteRequested += (_, t) => Dispatcher.Invoke(() =>
        {
            if (t.Layer is 1 or 2) SetLayerMute(t.Layer, t.Muted);
        });
        _companion.LayerMuteToggleRequested += (_, layer) => Dispatcher.Invoke(() =>
        {
            if (layer is 1 or 2) SetLayerMute(layer, !_layers[layer].Muted);
        });
    }

    // ===== Projeto (guardar / abrir) =====

    private void BtnSaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "StagePlayout project|*.stageplayout.json",
            DefaultExt = ".stageplayout.json",
            FileName = "show.stageplayout.json",
            Title = "Guardar projeto"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try { ProjectStore.Save(_playlist, dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Erro ao guardar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnOpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "StagePlayout project|*.stageplayout.json",
            Title = "Abrir projeto"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                ProjectStore.Load(_playlist, dlg.FileName);

                // normaliza paths curtos 8.3 (projetos gravados com eles)
                foreach (var c in _playlist.Cues.Where(c => !c.IsGroup))
                {
                    c.FilePath = PathHelper.ToLongPath(c.FilePath);
                    c.Name = Path.GetFileName(c.FilePath);
                }

                _standbyCue = null;
                PreloadNext();
                QueueThumbnails(_playlist.Cues);
                _cuesView?.Refresh();
                UpdateStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Erro ao abrir projeto",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private bool IsParentExpanded(Cue cue)
    {
        var parent = _playlist.Cues.FirstOrDefault(c => c.Id == cue.ParentId);
        return parent?.IsExpanded ?? true;
    }

    // ===== Grupos / playlists =====

    private void GroupSelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = CueList.SelectedItems.Cast<Cue>().Where(c => !c.IsGroup).ToList();
        if (selected.Count == 0) return;

        var group = _playlist.GroupSelection($"Nova playlist ({selected.Count})", selected);
        _cuesView?.Refresh();
        UpdateStatus();

        // focar o grupo criado
        CueList.SelectedItem = group;
        CueList.ScrollIntoView(group);
    }

    private void UngroupCue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Cue { IsGroup: true } group)
        {
            _playlist.Ungroup(group);
            _cuesView?.Refresh();
            UpdateStatus();
        }
    }

    private void GroupToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Cue { IsGroup: true } group)
            ToggleGroup(group);
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Cue { IsGroup: true } group)
        {
            var name = InputDialog.Show(this, "Nome da playlist", group.Name);
            if (name is not null)
                group.Name = name;
        }
    }

    private void GroupRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Cue { IsGroup: true } group)
        {
            ToggleGroup(group);
            e.Handled = true; // não selecionar/tocar ao clicar no cabeçalho
        }
    }

    private void ToggleGroup(Cue group)
    {
        group.IsExpanded = !group.IsExpanded;
        _cuesView?.Refresh();
    }

    // ===== Media =====

    private void BtnAddMedia_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media|*.mp4;*.mov;*.mkv;*.avi;*.m4v;*.webm;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|Vídeo|*.mp4;*.mov;*.mkv;*.avi;*.m4v;*.webm|Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|Todos os ficheiros|*.*",
            Title = "Adicionar media à playlist"
        };
        if (dlg.ShowDialog(this) == true)
            AddFiles(dlg.FileNames);
    }

    private void AddFiles(IEnumerable<string> files)
    {
        var added = new List<Cue>();
        foreach (var file in files)
        {
            // expande short paths 8.3 (ex.: 16435_~1.MP4) para o nome longo real
            var longPath = PathHelper.ToLongPath(file);
            var cue = new Cue { Name = Path.GetFileName(longPath), FilePath = longPath };
            _playlist.Add(cue);
            added.Add(cue);
        }

        QueueThumbnails(added);

        // se ainda nada está no ar, pré-carrega já o primeiro cue (1.º GO instantâneo)
        if (_liveSlot < 0)
            PreloadNext();

        UpdateStatus();
    }

    private void QueueThumbnails(IEnumerable<Cue> cues)
    {
        foreach (var cue in cues)
        {
            if (cue.Thumbnail is not null && cue.InfoText.Length > 0) continue;
            var c = cue;
            Task.Run(() =>
            {
                var bmp = c.Thumbnail is null ? ShellThumbnail.Get(c.FilePath) : null;
                MediaInfo? info = c.InfoText.Length == 0 ? MediaInfoReader.Read(c.FilePath) : null;

                if (bmp is null && info is null) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (bmp is not null) c.Thumbnail = bmp;
                    if (info is { } mi)
                    {
                        var parts = new List<string>();
                        if (mi.Width > 0) parts.Add($"{mi.Width}×{mi.Height}");
                        if (mi.Fps > 0)
                        {
                            var rounded = Math.Round(mi.Fps);
                            parts.Add(Math.Abs(mi.Fps - rounded) < 0.01
                                ? $"{rounded}fps"
                                : $"{mi.Fps:0.00}fps");
                        }
                        if (mi.VideoCodec.Length > 0) parts.Add(mi.VideoCodec);
                        if (mi.AudioCodec.Length > 0) parts.Add(mi.AudioCodec);
                        if (mi.FileSizeBytes > 0) parts.Add(FmtSize(mi.FileSizeBytes));
                        c.InfoText = string.Join(" • ", parts);

                        if (c.Duration == TimeSpan.Zero && mi.DurationTicks > 0)
                            c.Duration = TimeSpan.FromTicks(mi.DurationTicks);
                    }
                });
            });
        }
    }

    private static string FmtSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} MB";
        return $"{bytes / 1024.0:0} KB";
    }

    private void RemoveCue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is Cue cue)
        {
            if (cue.IsGroup)
                _playlist.Ungroup(cue); // remover grupo mantém os clips
            else
                _playlist.Remove(cue);

            if (_standbyCue?.Id == cue.Id) _standbyCue = null;
            _mediaPool.UpdateWindow(_playlist);
            _cuesView?.Refresh();
            PreloadNext();
            UpdateStatus();
        }
    }

    // ===== Reordenar cues (arrastar para cima/baixo = prioridade) =====

    private const string CueDragFormat = "StagePlayout.Cue";
    private Point _dragStart;
    private Cue? _dragCue;

    private Adorner? _insertAdorner;
    private ListBoxItem? _indicatorItem;
    private bool _indicatorBelow;

    private void CueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCue = HitTestItem(e.GetPosition(CueList))?.DataContext as Cue;
    }

    private void CueList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCue is null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var cue = _dragCue;
        _dragCue = null;

        SetCueOpacity(cue, 0.4);
        DragDrop.DoDragDrop(CueList, new DataObject(CueDragFormat, cue), DragDropEffects.Move);
        SetCueOpacity(cue, 1.0);
        ClearInsertionIndicator();
    }

    private void CueList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(CueDragFormat))
        {
            e.Effects = DragDropEffects.Move;
            UpdateInsertionIndicator(e);
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void CueList_DragLeave(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(CueList);
        if (pos.X < 0 || pos.Y < 0 || pos.X > CueList.ActualWidth || pos.Y > CueList.ActualHeight)
            ClearInsertionIndicator();
    }

    private void CueList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(CueDragFormat) is Cue cue)
        {
            var item = HitTestItem(e.GetPosition(CueList));
            Cue? target;
            var after = true;

            if (item is null)
            {
                target = null;
            }
            else
            {
                target = item.DataContext as Cue;
                after = e.GetPosition(item).Y > item.ActualHeight / 2;
            }

            _playlist.Move(cue, target, after);
            _cuesView?.Refresh();
            if (_standbyCue is not null)
            {
                _standbyCue = null;
                PreloadNext(); // a ordem mudou: recarregar o standby
            }
            _mediaPool.UpdateWindow(_playlist);
            ClearInsertionIndicator();
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            AddFiles(files);
    }

    private void UpdateInsertionIndicator(DragEventArgs e)
    {
        var item = HitTestItem(e.GetPosition(CueList));
        var below = true;

        if (item is null)
        {
            if (CueList.Items.Count > 0)
                item = CueList.ItemContainerGenerator
                       .ContainerFromIndex(CueList.Items.Count - 1) as ListBoxItem;
        }
        else
        {
            below = e.GetPosition(item).Y > item.ActualHeight / 2;
        }

        ShowInsertionIndicator(item, below);
    }

    private void ShowInsertionIndicator(ListBoxItem? item, bool below)
    {
        if (ReferenceEquals(item, _indicatorItem) && below == _indicatorBelow) return;
        ClearInsertionIndicator();
        if (item is null) return;

        var layer = AdornerLayer.GetAdornerLayer(CueList);
        if (layer is null) return;

        _insertAdorner = new Controls.InsertionAdorner(item, below);
        layer.Add(_insertAdorner);
        _indicatorItem = item;
        _indicatorBelow = below;
    }

    private void ClearInsertionIndicator()
    {
        if (_insertAdorner is not null)
            AdornerLayer.GetAdornerLayer(CueList)?.Remove(_insertAdorner);
        _insertAdorner = null;
        _indicatorItem = null;
    }

    private void SetCueOpacity(Cue cue, double opacity)
    {
        if (CueList.ItemContainerGenerator.ContainerFromItem(cue) is ListBoxItem item)
            item.Opacity = opacity;
    }

    private ListBoxItem? HitTestItem(Point point)
    {
        if (CueList.InputHitTest(point) is not DependencyObject element) return null;
        while (element is not null)
        {
            if (element is ListBoxItem item)
                return item;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    // ===== Transporte =====

    private void BtnGo_Click(object sender, RoutedEventArgs e) => Go();

    private void Go()
    {
        if (_playlist.Go() is { } cue)
        {
            _mediaPool.UpdateWindow(_playlist);
            TransitionTo(cue);
        }
    }

    private void TogglePause()
    {
        var live = _liveSlot >= 0 ? _slotDec[_liveSlot] : null;
        if (live is null) return;

        if (live.State == DecoderState.Playing)
        {
            live.Pause();
            BtnPause.Content = "RESUME";
        }
        else
        {
            live.Play();
            BtnPause.Content = "PAUSE";
        }
    }

    private void StopPlayback()
    {
        if (_liveSlot < 0 || _output?.Compositor is not { } comp) return;

        // fade to black; o decoder é libertado no fim do fade (OnFadeCompleted)
        var fadeOut = _playlist.Current?.FadeOutSeconds ?? 0.5;
        _closingSlots.Add(_liveSlot);
        comp.SetOpacity(_liveSlot, 0, fadeOut);
        _liveSlot = -1;
        BtnPause.Content = "PAUSE";
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    // ===== Camadas flutuantes L1/L2 =====

    private int SelectedLayer => SelL2.IsChecked == true ? 2 : 1;

    private void SelLayer_Click(object sender, RoutedEventArgs e)
    {
        SelL1.IsChecked = ReferenceEquals(sender, SelL1);
        SelL2.IsChecked = ReferenceEquals(sender, SelL2);
        RefreshGeomEditor();
    }

    private void RefreshGeomEditor()
    {
        var s = _layers[SelectedLayer];
        if (s is null) return;
        var cw = GeomCanvas.ActualWidth;
        var ch = GeomCanvas.ActualHeight;
        if (cw < 10 || ch < 10) return;

        GeomRect.Width = s.W * cw;
        GeomRect.Height = s.H * ch;
        Canvas.SetLeft(GeomRect, s.X * cw);
        Canvas.SetTop(GeomRect, s.Y * ch);
    }

    private void GeomMove_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var cw = GeomCanvas.ActualWidth;
        var ch = GeomCanvas.ActualHeight;
        var left = Canvas.GetLeft(GeomRect);
        var top = Canvas.GetTop(GeomRect);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        left = Math.Clamp(left + e.HorizontalChange, 0, cw - GeomRect.Width);
        top = Math.Clamp(top + e.VerticalChange, 0, ch - GeomRect.Height);
        Canvas.SetLeft(GeomRect, left);
        Canvas.SetTop(GeomRect, top);
        PushGeom(SelectedLayer);
    }

    private void GeomResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var cw = GeomCanvas.ActualWidth;
        var ch = GeomCanvas.ActualHeight;
        var left = Canvas.GetLeft(GeomRect);
        var top = Canvas.GetTop(GeomRect);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        GeomRect.Width = Math.Clamp(GeomRect.Width + e.HorizontalChange, cw * 0.05, cw - left);
        GeomRect.Height = Math.Clamp(GeomRect.Height + e.VerticalChange, ch * 0.05, ch - top);
        PushGeom(SelectedLayer);
    }

    private void PushGeom(int layer)
    {
        var cw = GeomCanvas.ActualWidth;
        var ch = GeomCanvas.ActualHeight;
        if (cw < 10 || ch < 10) return;

        var s = _layers[layer];
        s.X = Canvas.GetLeft(GeomRect) / cw;
        s.Y = Canvas.GetTop(GeomRect) / ch;
        s.W = GeomRect.Width / cw;
        s.H = GeomRect.Height / ch;
        _output?.Compositor?.SetGeometry(layer + 1, (float)s.X, (float)s.Y, (float)s.W, (float)s.H);
    }

    private void LayerOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag ||
            !int.TryParse(tag, out var layer) || layer is < 1 or > 2) return;

        var dlg = new OpenFileDialog
        {
            Filter = "Vídeo|*.mp4;*.mov;*.mkv;*.webm|Todos os ficheiros|*.*",
            Title = $"Escolher vídeo para a Layer {layer}"
        };
        if (dlg.ShowDialog(this) != true) return;

        var s = _layers[layer];
        s.File = PathHelper.ToLongPath(dlg.FileName);
        (layer == 1 ? TxtL1Name : TxtL2Name).Text = Path.GetFileName(s.File);
        (layer == 1 ? BtnL1Toggle : BtnL2Toggle).IsEnabled = true;

        if (s.Visible) // trocar em direto: abrir novo decoder e fazer swap no slot
            OpenLayerDecoder(layer, s.File);
    }

    /// <summary>Abre o ficheiro da layer em background e entra no slot com fade-in.</summary>
    private void OpenLayerDecoder(int layer, string file)
    {
        var s = _layers[layer];
        var slot = layer + 1;

        Task.Run(() =>
        {
            var dec = new FFDecoder();
            if (!dec.Open(file, autoPlay: false))
            {
                dec.Dispose();
                Dispatcher.BeginInvoke(() =>
                    TxtStatus.Text = $"ERRO na Layer {layer}: {file}");
                return;
            }
            dec.Loop = true;

            Dispatcher.BeginInvoke(() =>
            {
                if (!s.Visible || _output?.Compositor is not { } comp)
                {
                    dec.Dispose();
                    return;
                }
                s.Decoder?.Dispose();
                s.Decoder = dec;

                comp.SetSource(slot, dec);
                comp.SetGeometry(slot, (float)s.X, (float)s.Y, (float)s.W, (float)s.H);
                comp.SetOpacity(slot, 0, 0);
                ApplyVolumes();                     // respeita mute da layer
                dec.Play();
                comp.SetOpacity(slot, 1, 0.4); // fade-in rápido
            });
        });
    }

    private void LayerToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag &&
            int.TryParse(tag, out var layer) && layer is >= 1 and <= 2)
            SetLayerVisible(layer, !_layers[layer].Visible);
    }

    private void SetLayerVisible(int layer, bool visible)
    {
        var s = _layers[layer];
        if (visible && s.File is null) return;

        SetOutput(true);
        if (_output is null) return;

        s.Visible = visible;
        var btn = layer == 1 ? BtnL1Toggle : BtnL2Toggle;
        var slot = layer + 1;

        if (visible)
        {
            btn.Content = "OCULTAR";
            OpenLayerDecoder(layer, s.File!);
        }
        else
        {
            btn.Content = "MOSTRAR";
            if (_output.Compositor is { } comp)
            {
                // fade-out; o decoder é libertado quando o fade terminar
                _closingLayerSlots.Add(slot);
                comp.SetOpacity(slot, 0, 0.3);
            }
            else
            {
                s.Decoder?.Dispose();
                s.Decoder = null;
            }
        }
    }



    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => ApplyVolumes();

    /// <summary>Fonte única de volumes: master mute + mute por layer.</summary>
    private void ApplyVolumes()
    {
        var master = VolumeSlider.Value / 100.0;
        var comp = _output?.Compositor;
        if (comp is null) return;

        var progVol = _masterMuted ? 0.0 : master;
        comp.SetBaseVolume(0, progVol);
        comp.SetBaseVolume(1, progVol);
        for (var i = 1; i <= 2; i++)
            comp.SetBaseVolume(i + 1, _layers[i].Muted ? 0.0 : master);

        if (_standbyDec is not null) _standbyDec.Volume = progVol;
    }

    private void MasterMute_Click(object sender, RoutedEventArgs e)
        => SetMasterMute(BtnMasterMute.IsChecked != true);

    private void SetMasterMute(bool muted)
    {
        _masterMuted = muted;
        BtnMasterMute.IsChecked = !muted;
        ApplyVolumes();
    }

    private void LayerMute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag ||
            !int.TryParse(tag, out var layer) || layer is < 1 or > 2) return;

        SetLayerMute(layer, (sender as ToggleButton)?.IsChecked != true);
    }

    private void SetLayerMute(int layer, bool muted)
    {
        _layers[layer].Muted = muted;
        (layer == 1 ? BtnL1Mute : BtnL2Mute).IsChecked = !muted;
        ApplyVolumes();
    }

    private void CueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CueList.SelectedItem is not Cue c) return;

        var target = c;
        if (c.IsGroup)
        {
            // tocar grupo = tocar o primeiro filho
            target = _playlist.Cues.FirstOrDefault(x => x.ParentId == c.Id);
            if (target is null) return; // grupo vazio
        }

        var idx = _playlist.Cues.IndexOf(target);
        if (idx >= 0 && _playlist.Select(idx) is { } cue)
        {
            _mediaPool.UpdateWindow(_playlist);
            TransitionTo(cue);
        }
    }

    private void OnCurrentChanged()
    {
        if (_playlist.Current is { } cue)
        {
            CueList.SelectedItem = cue;
            CueList.ScrollIntoView(cue);
        }

        TxtNowPlaying.Text = _playlist.Current is { } cur
            ? $"CUED: {cur.Name}"
            : "Nenhum cue carregado";
        UpdateStatus();
    }

    // ===== Output (2.º ecrã / projetor) =====

    private void BtnOutput_Click(object sender, RoutedEventArgs e) => SetOutput(_output is null);

    private void SetOutput(bool open)
    {
        if (!open)
        {
            _output?.Close();
            return;
        }
        if (_output is not null) return;

        _output = new OutputWindow();
        _output.Closed += (_, _) =>
        {
            _output = null;
            BtnOutput.Content = "Abrir output";
        };

        var screens = System.Windows.Forms.Screen.AllScreens;
        var target = screens.Length > 1 ? screens[1] : screens[0];
        _output.Left = target.Bounds.Left;
        _output.Top = target.Bounds.Top;
        _output.Width = target.Bounds.Width;
        _output.Height = target.Bounds.Height;
        _output.Show();
        _output.WindowState = WindowState.Maximized;

        // deteção do modo da saída (interlaçado/progressivo) para a status bar
        _outputInfo = DisplayInfo.Describe(target.DeviceName);
        UpdateStatus();

        // reattach de todas as fontes no novo compositor (após loaded)
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_output?.Compositor is not { } comp) return;

            if (!_compWired)
            {
                _compWired = true;
                comp.FadeCompleted += OnFadeCompleted;
            }

            for (var slot = 0; slot < 2; slot++)
                if (_slotDec[slot] is { } d)
                {
                    comp.SetSource(slot, d);
                    comp.SetGeometry(slot, 0, 0, 1, 1);
                    comp.SetOpacity(slot, 1, 0);
                }

            for (var i = 1; i <= 2; i++)
            {
                var s = _layers[i];
                if (s is { Visible: true, Decoder: not null })
                {
                    comp.SetSource(i + 1, s.Decoder);
                    comp.SetGeometry(i + 1, (float)s.X, (float)s.Y, (float)s.W, (float)s.H);
                    comp.SetOpacity(i + 1, 1, 0);
                }
            }

            ApplyVolumes(); // respeita mutes
        }));

        BtnOutput.Content = "Fechar output";
    }

    // ===== Estado / atalhos =====

    private void UpdateStatus()
    {
        var preloaded = _standbyCue is not null ? $"preload: {_standbyCue.Name}" : "preload: —";
        TxtStatus.Text = $"READY  •  {_playlist.Cues.Count} cues  •  {preloaded}  •  " +
                         $"Output: {_outputInfo}  •  OSC porta {_companion.Port}";
    }

    private void Previous()
    {
        if (_playlist.Previous() is { } cue)
            TransitionTo(cue);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var handled = true;

        if (e.Key == _keyGo || e.Key == _keyNext) Go();
        else if (e.Key == _keyPrev) Previous();
        else if (e.Key == _keyStop) StopPlayback();
        else if (e.Key == _keyPause) TogglePause();
        else handled = false;

        e.Handled = handled;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        _companion.Dispose();
        foreach (var dec in _slotDec) dec?.Dispose();
        _standbyDec?.Dispose();
        _layers[1]?.Decoder?.Dispose();
        _layers[2]?.Decoder?.Dispose();
        _output?.Close();
        base.OnClosed(e);
    }
}
