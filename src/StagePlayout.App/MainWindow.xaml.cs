using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly CompanionControl _companion = CompanionControl.FromConfig(CompanionConfig.Load());
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _backupTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private WriteableBitmap? _previewBmp;
    private byte[] _previewBuf = Array.Empty<byte>();
    private string _restoreRefreshDevice = "";
    private int _restoreRefreshFreq;

    // Projeto: estado sujo (prompt ao fechar) + caminho atual (auto-backup)
    private bool _projectDirty;
    private string _projectPath = "";

    // Dip: transição a meio (outgoing a descer → incoming arranca no fim do fade)
    private (int Gen, int OutSlot, int InSlot, FFDecoder Dec, double FadeIn)? _pendingDip;

    // Engine de programa: FFDecoders próprios alimentam o compositor GPU
    // (2 slots com crossfade real). Preload = decoder aberto em pausa no 1.º frame.
    private readonly FFDecoder?[] _slotDec = new FFDecoder?[2];
    private readonly double[] _slotVolScale = { 1.0, 1.0 }; // volume por cue de cada slot
    private readonly HashSet<int> _closingSlots = new();
    private int _liveSlot = -1;
    private FFDecoder? _standbyDec;
    private Cue? _standbyCue;
    private bool _compWired;
    private readonly HashSet<int> _closingLayerSlots = new();

    // Geração de transições: invalida callbacks de fades antigos (evita stops/fora de época)
    private int _transitionGen;



    // Layers dinâmicas (até 4) — slots 2..5 do compositor
    private const int MaxLayers = 4;
    private readonly ObservableCollection<LayerVm> _layerVms = new();
    private int _selectedLayer; // 0-based
    private bool _masterMuted;
    private string _masterAudioDevice = "";
    private int _oscTick;

    // Atalhos configuráveis (shortcuts.json na pasta do exe)
    private readonly ShortcutConfig _shortcuts = ShortcutConfig.Load();
    private Key _keyGo, _keyNext, _keyPrev, _keyStop, _keyPause;

    private OutputWindow? _output;
    private OverlayWindow? _overlay;
    private string _outputInfo = "—";

    private System.ComponentModel.ICollectionView? _cuesView;

    public MainWindow()
    {
        InitializeComponent();
        App.ApplyDarkMode(this);

        // Set window icon from SVG logo
        Loaded += (_, _) =>
        {
            try
            {
                var svg = new SharpVectors.Converters.SvgViewbox
                {
                    Source = new Uri("pack://application:,,,/Assets/app-icon.svg"),
                    Width = 48, Height = 48
                };
                // Force layout
                svg.Measure(new Size(48, 48));
                svg.Arrange(new Rect(0, 0, 48, 48));
                svg.UpdateLayout();
                var bmp = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(svg);
                Icon = bmp;
            }
            catch { }
        };

        // view com filtro: filhos de grupos colapsados ficam escondidos
        _cuesView = System.Windows.Data.CollectionViewSource.GetDefaultView(_playlist.Cues);
        _cuesView.Filter = o => o is Cue c && (!c.IsChild || IsParentExpanded(c));
        CueList.ItemsSource = _cuesView;

        // layers iniciais L1/L2 (lista dinâmica até 4)
        _layerVms.Add(new LayerVm(new LayerState { X = 0.68, Y = 0.66, W = 0.28, H = 0.28 }, 0));
        _layerVms.Add(new LayerVm(new LayerState { X = 0.68, Y = 0.04, W = 0.28, H = 0.28 }, 1));
        _selectedLayer = 0;
        RefreshLayerSelection();
        LayerList.ItemsSource = _layerVms;

        _playlist.CurrentChanged += (_, _) => OnCurrentChanged();
        _playlist.Cues.CollectionChanged += (_, _) => MarkDirty();

        PreloadNext();

        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        _previewTimer.Tick += PreviewTimer_Tick;
        _previewTimer.Start();

        _backupTimer.Tick += BackupTimer_Tick;
        _backupTimer.Start();

        HookCompanion();
        _companion.Start();

        _keyGo = ParseKey(_shortcuts.Go, Key.Space);
        _keyNext = ParseKey(_shortcuts.Next, Key.Right);
        _keyPrev = ParseKey(_shortcuts.Previous, Key.Left);
        _keyStop = ParseKey(_shortcuts.Stop, Key.S);
        _keyPause = ParseKey(_shortcuts.Pause, Key.P);

        Loaded += (_, _) => RefreshGeomEditor();
        Loaded += (_, _) => LoadStartupProject();
        Loaded += (_, _) => RestoreWindowState();

        UpdateStatus();
    }

    /// <summary>Restaura posição/tamanho da janela e reabre o último projeto (se existir).</summary>
    private void RestoreWindowState()
    {
        var st = AppState.Load();

        if (st.Width >= MinWidth && st.Height >= MinHeight)
        {
            if (!double.IsNaN(st.Left) && !double.IsNaN(st.Top))
            {
                // só aplica se cair dentro de algum ecrã (multi-monitor pode mudar)
                var bounds = new System.Drawing.Rectangle(
                    (int)st.Left, (int)st.Top, (int)st.Width, (int)st.Height);
                var visible = System.Windows.Forms.Screen.AllScreens
                    .Any(s => s.WorkingArea.IntersectsWith(bounds));
                if (visible)
                {
                    Left = st.Left;
                    Top = st.Top;
                }
            }
            Width = st.Width;
            Height = st.Height;
        }
        if (st.Maximized)
            WindowState = WindowState.Maximized;

        // reabrir o último projeto (a menos que a linha de comandos tenha dado outro)
        if (App.StartupProject is null && st.LastProjectPath.Length > 0 &&
            File.Exists(st.LastProjectPath))
        {
            try
            {
                LoadProject(st.LastProjectPath);
                TxtStatus.Text = $"Projeto reaberto: {Path.GetFileName(st.LastProjectPath)}";
            }
            catch
            {
                // ficheiro inválido — arranca vazio
            }
        }
    }

    // ===== Projeto: estado sujo / auto-backup / carga CLI =====

    private static readonly HashSet<string> EditorProps = new(StringComparer.Ordinal)
    {
        nameof(Cue.Name), nameof(Cue.End), nameof(Cue.FadeInSeconds), nameof(Cue.FadeOutSeconds),
        nameof(Cue.Volume), nameof(Cue.IsGroup), nameof(Cue.IsExpanded), nameof(Cue.ParentId),
        nameof(Cue.LoopGroup), nameof(Cue.TagColor), nameof(Cue.FillMode), nameof(Cue.Rotation),
        nameof(Cue.IsAudioMuted), nameof(Cue.NextCueId), nameof(Cue.JumpTargetId), nameof(Cue.FadeType),
        nameof(Cue.Output),
    };

    private void OnCueEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } p && EditorProps.Contains(p)) MarkDirty();
    }

    private void HookCueEdits(Cue cue) => cue.PropertyChanged += OnCueEditorChanged;

    private void MarkDirty()
    {
        if (_projectDirty) return;
        _projectDirty = true;
        UpdateTitle();
    }

    private void ClearDirty()
    {
        _projectDirty = false;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        Title = "Smart Play Cue" + (_showMode ? " — SHOW MODE" : "") + (_projectDirty ? " •" : "");
    }

    // ===== Show mode (lock UI em live) =====

    private bool _showMode;

    private void BtnShowMode_Toggled(object sender, RoutedEventArgs e)
        => SetShowMode(BtnShowMode.IsChecked == true);

    private void SetShowMode(bool on)
    {
        _showMode = on;
        CueList.ItemContainerStyle =
            (Style)FindResource(on ? "CueItemStyleLocked" : "CueItemStyle");
        CueList.AllowDrop = !on;
        BtnAddMedia.IsEnabled = !on;
        BtnOpenProject.IsEnabled = !on;
        foreach (var c in _playlist.Cues)
            c.EditLocked = on; // bloqueia editores inline (FadeBar)
        UpdateTitle();

        TxtStatus.Text = on
            ? "SHOW MODE — edições bloqueadas (Ctrl+L para sair)"
            : "Show mode desligado";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { UpdateStatus(); t.Stop(); };
        t.Start();
    }

    private void LoadStartupProject()
    {
        // modo de verificação pré-show: toca todos os cues, exercita layers, sai com exit code
        if (App.StartupSelfTest)
        {
            RunSelfTest(App.StartupProject);
            return;
        }

        var path = App.StartupProject;
        if (path is not null)
        {
            try
            {
                LoadProject(path);
                TxtStatus.Text = $"Projeto carregado: {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Erro ao abrir projeto (argumento)",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // arranque desatendido (kiosk): --output abre o output; --autoplay toca o 1.º cue
        if (App.StartupOpenOutput)
        {
            SetOutput(true);
            if (App.StartupAutoPlay)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Go);
        }
    }

    /// <summary>
    /// --selftest: verificação automatizada pré-show. Escreve o relatório em
    /// %TEMP%\stageplayout_selftest.log e termina com exit code 0 (OK) ou 1 (falha).
    /// </summary>
    private async void RunSelfTest(string? projectPath)
    {
        WindowState = WindowState.Minimized;
        var log = new List<string>();
        var failures = 0;
        void L(string s)
        {
            log.Add($"{DateTime.Now:HH:mm:ss} {s}");
            VideoLog.Write($"SELFTEST: {s}");
        }

        try
        {
            L($"SELFTEST arranque — projeto: {projectPath ?? "(nenhum)"}");
            if (projectPath is not null)
                LoadProject(projectPath);

            var cues = _playlist.Cues.Where(c => !c.IsGroup).ToList();
            if (cues.Count == 0)
            {
                L("FAIL: projeto sem cues");
                failures++;
                FinishSelfTest(log, failures);
                return;
            }
            L($"projeto carregado: {cues.Count} cues");

            SetOutput(true);
            await Task.Delay(2000); // compositor a carregar

            foreach (var cue in cues)
            {
                var idx = _playlist.Cues.IndexOf(cue);
                _playlist.Select(idx);
                TransitionTo(cue);
                await Task.Delay(1200);

                var dec = _liveSlot >= 0 ? _slotDec[_liveSlot] : null;
                if (dec is null || dec.State != DecoderState.Playing)
                {
                    L($"FAIL: cue {cue.DisplayId} '{cue.Name}' não está a tocar");
                    failures++;
                }
                else
                {
                    L($"OK: cue {cue.DisplayId} '{cue.Name}' ({dec.FrameWidth}x{dec.FrameHeight} " +
                      $"{dec.DurationTicks / TimeSpan.TicksPerSecond}s)");
                }
            }

            foreach (var vm in _layerVms.ToList())
            {
                if (vm.State.File is not { } f) continue;
                SetLayerVisible(vm.Index, true);
                await Task.Delay(700);
                if (vm.State.Decoder is null)
                {
                    L($"FAIL: {vm.Label} '{Path.GetFileName(f)}' não abriu");
                    failures++;
                }
                else
                {
                    L($"OK: {vm.Label} '{Path.GetFileName(f)}' a tocar");
                }
                SetLayerVisible(vm.Index, false);
                await Task.Delay(400);
            }

            StopPlayback();
            L($"drops de vsync: {_output?.Compositor?.DropCount ?? 0}");
            L(failures == 0 ? "SELFTEST COMPLETO — TUDO OK" : $"SELFTEST COM FALHAS ({failures})");
            FinishSelfTest(log, failures);
        }
        catch (Exception ex)
        {
            L($"EXCEPTION: {ex}");
            FinishSelfTest(log, failures + 1);
        }
    }

    private void FinishSelfTest(List<string> log, int failures)
    {
        try
        {
            File.WriteAllLines(
                Path.Combine(Path.GetTempPath(), "stageplayout_selftest.log"), log);
        }
        catch { }
        Dispatcher.BeginInvoke(() => Application.Current.Shutdown(failures == 0 ? 0 : 1));
    }

    private void BackupTimer_Tick(object? sender, EventArgs e)
    {
        if (_shortcuts.AutoBackupMinutes <= 0 || _projectPath.Length == 0 ||
            !_projectDirty || _playlist.Cues.Count == 0)
            return;

        try
        {
            var bak = _projectPath + ".bak";
            SaveProjectTo(bak);
            RotateBackups(bak, keep: 5);
            VideoLog.Write($"Auto-backup: {bak}");
        }
        catch (Exception ex)
        {
            VideoLog.Write($"Auto-backup FALHOU: {ex.Message}");
        }
    }

    /// <summary>Mantém as N cópias de backup mais recentes (`.bak.yyyyMMdd-HHmmss`).</summary>
    private static void RotateBackups(string bakPath, int keep)
    {
        var dir = Path.GetDirectoryName(bakPath);
        if (string.IsNullOrEmpty(dir)) return;
        var name = Path.GetFileName(bakPath);
        var rotated = Path.Combine(dir, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss}");
        try
        {
            File.Copy(bakPath, rotated);
            var old = Directory.GetFiles(dir, name + ".*")
                              .OrderByDescending(f => f)
                              .Skip(keep)
                              .ToList();
            foreach (var f in old)
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { /* rotação best-effort */ }
    }

    private static Key ParseKey(string? name, Key fallback)
        => Enum.TryParse<Key>(name ?? "", true, out var key) ? key : fallback;

    // ===== Motor de vídeo (Flyleaf A/B com preload) =====

    // ===== Transições (compositor GPU — crossfade real) =====

    private void TransitionTo(Cue cue)
    {
        // routing por cue: o output abre-se (ou move-se) para o ecrã da cue
        // (Output 0 = AUTO = segue o preset do projeto)
        SetOutput(true, ResolveCueDevice(cue));
        // garantir que o compositor já existe (hwnd criado no load da janela)
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => DoTransition(cue)));
    }

    /// <summary>Device de saída da cue: Output 1..N → ecrã N; 0 → preset do projeto.</summary>
    private static string? ResolveCueDevice(Cue cue)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        return cue.Output >= 1 && cue.Output <= screens.Length
            ? screens[cue.Output - 1].DeviceName
            : null;
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

        // Race STOP→GO: o slot pode estar em fade-out de um stop anterior.
        // Cancelar o fecho pendente para o OnFadeCompleted não matar o decoder NOVO.
        _closingSlots.Remove(newSlot);
        _pendingDip = null;

        incoming.Loop = cue.End == CueEnd.Loop;
        incoming.Volume = VolumeSlider.Value / 100.0;
        incoming.AudioDeviceId = cue.AudioOutputDevice.Length > 0
            ? cue.AudioOutputDevice
            : _masterAudioDevice; // dispositivo por cue (ou master)
        incoming.Ended += Decoder_Ended;

        _slotDec[newSlot]?.Dispose();
        _slotDec[newSlot] = incoming;

        if (cue.Duration == TimeSpan.Zero && incoming.DurationTicks > 0)
            cue.Duration = TimeSpan.FromTicks(incoming.DurationTicks);

        comp.SetSource(newSlot, incoming);
        ApplyFillGeometry(cue, incoming, comp, newSlot);
        comp.SetZ(newSlot, gen);            // o mais recente desenha por cima
        comp.SetOpacity(newSlot, 0, 0);
        var volScale = cue.IsAudioMuted ? 0.0 : Math.Clamp(cue.Volume, 0.0, 1.0);
        comp.SetVolumeScale(newSlot, volScale);
        _slotVolScale[newSlot] = volScale;  // guardar p/ reattach do output
        ApplyVolumes();                     // respeita master mute

        var oldSlot = _liveSlot;
        var dip = cue.FadeType == FadeType.Dip;
        var hasOutgoing = oldSlot >= 0 && oldSlot != newSlot && _slotDec[oldSlot] is not null;

        if (dip && hasOutgoing)
        {
            // Dip (via preto): outgoing desce primeiro; o incoming arranca no fim do fade
            _closingSlots.Add(oldSlot);
            comp.SetOpacity(oldSlot, 0, cue.FadeOutSeconds);
            _pendingDip = (gen, oldSlot, newSlot, incoming, cue.FadeInSeconds);
        }
        else
        {
            incoming.Play();
            comp.SetOpacity(newSlot, 1, cue.FadeInSeconds);   // fade-in
            if (hasOutgoing)
            {
                // Outgoing (cross): fade-out e dispose no fim do fade
                _closingSlots.Add(oldSlot);
                comp.SetOpacity(oldSlot, 0, cue.FadeOutSeconds);
            }
        }
        // (layers têm prioridade fixa no draw order do compositor)

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
            // slots de layers (2..5)
            if (slot >= 2)
            {
                if (!_closingLayerSlots.Remove(slot)) return;
                var li = slot - 2;
                if (li < _layerVms.Count)
                {
                    _layerVms[li].State.Decoder?.Dispose();
                    _layerVms[li].State.Decoder = null;
                }
                _output?.Compositor?.SetSource(slot, null);
                return;
            }

            // slots de programa (0,1)
            var wasClosing = _closingSlots.Remove(slot);

            // Dip: fade-out do outgoing terminou → arrancar o incoming
            if (_pendingDip is { } d && d.OutSlot == slot && d.Gen == _transitionGen)
            {
                _pendingDip = null;
                if (_slotDec[d.InSlot] is { } dec && ReferenceEquals(dec, d.Dec))
                {
                    d.Dec.Play();
                    _output?.Compositor?.SetOpacity(d.InSlot, 1, d.FadeIn);
                }
                else
                {
                    d.Dec.Dispose(); // transição substituída entretanto
                }
            }

            if (!wasClosing) return;
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

            // Jump to specific cue if set
            if (cue.NextCueId != Guid.Empty)
            {
                var target = _playlist.Cues.FirstOrDefault(c => c.Id == cue.NextCueId);
                if (target is not null)
                {
                    var idx = _playlist.Cues.IndexOf(target);
                    if (idx >= 0 && _playlist.Select(idx) is not null)
                        TransitionTo(target);
                }
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
        dec.AudioDeviceId = next.AudioOutputDevice.Length > 0
            ? next.AudioOutputDevice
            : _masterAudioDevice;
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

    /// <summary>Limite de extrações de thumbnail em paralelo (decoders FFmpeg por ficheiro).</summary>
    private static readonly SemaphoreSlim ThumbSemaphore = new(3, 3);

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
        var nextOut = next is null ? "" : next.Output > 0 ? $"OUT {next.Output}" : "OUT AUTO (preset)";
        if (TxtNextOut.Text != nextOut) TxtNextOut.Text = nextOut;

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
            UpdateOverlay("—");
            if (++_oscTick % 4 == 0)
            {
                _companion.SendRemainingTime(null, "STANDBY");
                _companion.SendCueInfo(0, "");
            }
            return;
        }

        var cur = TimeSpan.FromTicks(live.CurTimeTicks);
        var dur = TimeSpan.FromTicks(live.DurationTicks);
        var remaining = dur > cur ? dur - cur : TimeSpan.Zero;
        TxtTime.Text = $"{cur:hh\\:mm\\:ss} / -{remaining:hh\\:mm\\:ss}";

        UpdateBigRemaining(live.DurationTicks > 0 ? remaining : null);

        UpdateOverlay(current is not null
            ? $"CUE {current.DisplayId} — {current.Name}    -{remaining:hh\\:mm\\:ss}"
            : "—");

        // OSC feedback: remaining time + cue atual (1x por segundo)
        if (++_oscTick % 4 == 0)
        {
            _companion.SendRemainingTime(live.DurationTicks > 0 ? remaining : null, "ON AIR");
            _companion.SendCueInfo(current?.DisplayId ?? 0, current?.Name);
            _companion.SendHealth(_output?.Compositor?.DropCount ?? 0);
        }

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

        // VU meter real: picos calculados pelo decoder (0..1 → px no meter de 16px)
        if (current is not null && current.HasAudio && live.State == DecoderState.Playing)
        {
            current.AudioPeakL = Math.Clamp(live.AudioPeakL, 0, 1) * 16;
            current.AudioPeakR = Math.Clamp(live.AudioPeakR, 0, 1) * 16;
        }
        else if (current is not null)
        {
            current.AudioPeakL = current.AudioPeakR = 0;
        }
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
            var idx = t.Layer - 1;
            if (idx >= 0 && idx < _layerVms.Count) SetLayerVisible(idx, t.Show);
        });
        _companion.LayerToggleRequested += (_, layer) => Dispatcher.Invoke(() =>
        {
            var idx = layer - 1;
            if (idx >= 0 && idx < _layerVms.Count) SetLayerVisible(idx, !_layerVms[idx].State.Visible);
        });
        _companion.MasterMuteRequested += (_, muted) => Dispatcher.Invoke(() => SetMasterMute(muted));
        _companion.MasterMuteToggleRequested += (_, _) => Dispatcher.Invoke(() => SetMasterMute(!_masterMuted));
        _companion.CueMuteToggleRequested += (_, n) => Dispatcher.Invoke(() =>
        {
            if (n >= 1 && n <= _playlist.Cues.Count)
            {
                var cue = _playlist.Cues[n - 1];
                cue.IsAudioMuted = !cue.IsAudioMuted;
                ApplyCueAudioMute(cue);
            }
        });
        _companion.PanicRequested += (_, _) => Dispatcher.Invoke(() =>
        {
            // eject all cues
            StopPlayback();
            foreach (var c in _playlist.Cues)
            {
                c.IsLive = false;
                c.Progress = 0;
            }
        });
        _companion.LayerMuteRequested += (_, t) => Dispatcher.Invoke(() =>
        {
            var idx = t.Layer - 1;
            if (idx >= 0 && idx < _layerVms.Count) SetLayerMute(idx, t.Muted);
        });
        _companion.LayerMuteToggleRequested += (_, layer) => Dispatcher.Invoke(() =>
        {
            var idx = layer - 1;
            if (idx >= 0 && idx < _layerVms.Count) SetLayerMute(idx, !_layerVms[idx].State.Muted);
        });
        _companion.LayerBlendRequested += (_, t) => Dispatcher.Invoke(() =>
        {
            var idx = t.Layer - 1;
            if (idx >= 0 && idx < _layerVms.Count) SetLayerBlend(idx, t.Additive);
        });
        _companion.LayerBlendToggleRequested += (_, layer) => Dispatcher.Invoke(() =>
        {
            var idx = layer - 1;
            if (idx >= 0 && idx < _layerVms.Count) SetLayerBlend(idx, !_layerVms[idx].State.BlendAdd);
        });
    }

    // ===== Projeto (guardar / abrir) =====

    private void BtnSaveProject_Click(object sender, RoutedEventArgs e) => SaveProjectAs();

    private bool SaveProjectAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "StagePlayout project|*.stageplayout.json",
            DefaultExt = ".stageplayout.json",
            FileName = "show.stageplayout.json",
            Title = "Guardar projeto"
        };
        if (dlg.ShowDialog(this) != true) return false;

        try
        {
            SaveProjectTo(dlg.FileName);
            _projectPath = dlg.FileName;
            ClearDirty();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro ao guardar",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Guarda playlist + presets de output + estado de todas as layers.</summary>
    private void SaveProjectTo(string path)
    {
        var layers = _layerVms
            .Select(vm => new ProjectStore.LayerStateDto(
                vm.State.File ?? "", vm.State.X, vm.State.Y, vm.State.W, vm.State.H,
                vm.State.Muted, vm.State.BlendAdd))
            .ToList();
        ProjectStore.Save(_playlist, path, layers);
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
                LoadProject(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Erro ao abrir projeto",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>Carrega um projeto (playlist + presets de output + layers) e prepara o UI.</summary>
    private void LoadProject(string path)
    {
        ProjectStore.Load(_playlist, path, ApplyLayerStates);

        // ensure all cues have display IDs + hook de edições (estado sujo)
        foreach (var c in _playlist.Cues)
        {
            if (c.DisplayId == 0) c.DisplayId = Cue.NextDisplayId();
            HookCueEdits(c);
        }

        // resolve jump target display IDs after loading
        foreach (var c in _playlist.Cues)
            c.JumpTargetId = _playlist.Cues.FirstOrDefault(x => x.Id == c.NextCueId)?.DisplayId ?? 0;

        // normaliza paths curtos 8.3 (projetos gravados com eles)
        foreach (var c in _playlist.Cues.Where(c => !c.IsGroup))
        {
            c.FilePath = PathHelper.ToLongPath(c.FilePath);
            c.Name = Path.GetFileName(c.FilePath);
        }

        _projectPath = path;
        _standbyCue = null;
        PreloadNext();
        QueueThumbnails(_playlist.Cues);
        _cuesView?.Refresh();
        UpdateStatus();
        ClearDirty(); // CollectionChanged/PropertyChanged durante a carga não contam
    }

    /// <summary>Restaura o estado das layers do projeto (nunca auto-mostra).</summary>
    private void ApplyLayerStates(List<ProjectStore.LayerStateDto>? states)
    {
        if (states is null) return;

        _layerVms.Clear();
        var i = 0;
        foreach (var d in states.Take(MaxLayers))
        {
            var vm = new LayerVm(new LayerState
            {
                X = d.X, Y = d.Y, W = d.W, H = d.H,
                Muted = d.Muted, BlendAdd = d.BlendAdd,
                Visible = false, // segurança: layers nunca entram sozinhas no ar
            }, i++);
            if (!string.IsNullOrWhiteSpace(d.File))
                vm.SetFile(d.File);
            _layerVms.Add(vm);
        }

        if (_layerVms.Count == 0)
        {
            _layerVms.Add(new LayerVm(new LayerState { X = 0.68, Y = 0.66, W = 0.28, H = 0.28 }, 0));
        }
        _selectedLayer = 0;
        RefreshLayerSelection();
        RefreshGeomEditor();
    }

    private bool IsParentExpanded(Cue cue)
    {
        var parent = _playlist.Cues.FirstOrDefault(c => c.Id == cue.ParentId);
        return parent?.IsExpanded ?? true;
    }

    // ===== Grupos / playlists =====

    private void JumpToCue_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem parent) return;
        parent.Items.Clear();

        var current = (parent.DataContext ?? CueList.SelectedItem) as Cue;

        // "None" option to clear
        var noneItem = new MenuItem { Header = "— Follow playlist order —", IsCheckable = true, IsChecked = current?.NextCueId == Guid.Empty };
        noneItem.Click += (_, _) => { if (current is not null) { current.End = CueEnd.HoldLastFrame; current.NextCueId = Guid.Empty; current.JumpTargetId = 0; } };
        parent.Items.Add(noneItem);
        parent.Items.Add(new Separator());

        foreach (var c in _playlist.Cues)
        {
            if (c.Id == current?.Id) continue;
            var item = new MenuItem { Tag = c, IsCheckable = true, IsChecked = current?.NextCueId == c.Id };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.TagColor)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = $"{c.DisplayId}  {c.Name}", VerticalAlignment = VerticalAlignment.Center });
            item.Header = sp;
            item.Click += (_, _) =>
            {
                if (current is not null) { current.End = CueEnd.JumpTo; current.NextCueId = c.Id; current.JumpTargetId = c.DisplayId; }
            };
            parent.Items.Add(item);
        }
    }

    private void GroupSelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = CueList.SelectedItems.Cast<Cue>().Where(c => !c.IsGroup).ToList();
        if (selected.Count == 0) return;

        var group = _playlist.GroupSelection($"Nova playlist ({selected.Count})", selected);
        HookCueEdits(group);
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
            HookCueEdits(cue);
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

                // Fallback: Shell devolveu ícone de ficheiro (HAP/ProRes sem handler
                // de thumbnails no Windows) -> extrair frame real via FFmpeg
                // Ícone de ficheiro = bitmap quadrado (32..256px). Thumbnail real de
                // vídeo nunca é quadrado (16:9, 4:3, etc.).
                var isFileIcon = bmp is not null
                    && bmp.PixelWidth >= 24
                    && Math.Abs(bmp.PixelWidth - bmp.PixelHeight) <= 4;
                VideoLog.Write($"Thumb: Shell={bmp?.PixelWidth}x{bmp?.PixelHeight} icon={isFileIcon} {c.Name}");
                if (isFileIcon)
                {
                    ExtractThumb(c, 160, 90);
                    bmp = null; // o ícone não serve; a frame real chega via Dispatcher
                }

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
                        if (mi.AudioCodec.Length > 0) { parts.Add(mi.AudioCodec); c.HasAudio = true; }
                        else c.HasAudio = false;
                        if (mi.FileSizeBytes > 0) parts.Add(FmtSize(mi.FileSizeBytes));
                        c.InfoText = string.Join(" • ", parts);

                        if (c.Duration == TimeSpan.Zero && mi.DurationTicks > 0)
                            c.Duration = TimeSpan.FromTicks(mi.DurationTicks);
                    }
                });
            });
        }
    }

    private void ExtractThumb(Cue cue, int maxW, int maxH)
    {
        Task.Run(async () =>
        {
            // limitar decoders FFmpeg em paralelo (playlists grandes abriam dezenas de uma vez)
            await ThumbSemaphore.WaitAsync();
            try
            {
                var dec = new FFDecoder();
                try
                {
                    // autoPlay false = PreRollFirstFrame decodes frame 0 synchronously
                    if (!dec.Open(cue.FilePath, autoPlay: false)) { VideoLog.Write($"ExtractThumb: Open FALHOU {cue.Name}"); dec.Dispose(); return; }
                    // after Open + PreRollFirstFrame, frame 0 should be decoded already
                    if (dec.FrameWidth == 0) { VideoLog.Write($"ExtractThumb: FrameWidth=0 {cue.Name}"); dec.Dispose(); return; }
                    int w, h, stride;
                    byte[] copy;
                    lock (dec.FrameLock)
                    {
                        w = dec.FrameWidth; h = dec.FrameHeight; stride = dec.FrameStride;
                        var src = dec.FrameData;
                        if (src is null || src.Length == 0) { VideoLog.Write($"ExtractThumb: FrameData vazio {cue.Name}"); dec.Dispose(); return; }
                        copy = new byte[src.Length];
                        Buffer.BlockCopy(src, 0, copy, 0, src.Length);
                        VideoLog.Write($"ExtractThumb: copiado {w}x{h} stride={stride} len={src.Length} {cue.Name}");
                    }
                    dec.Dispose();
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            var full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, copy, w * 4);
                            full.Freeze();
                            if (w > maxW || h > maxH)
                            {
                                var scale = Math.Min((double)maxW / w, (double)maxH / h);
                                var scaled = new TransformedBitmap(full, new ScaleTransform(scale, scale));
                                scaled.Freeze();
                                cue.Thumbnail = scaled;
                            }
                            else
                            {
                                cue.Thumbnail = full;
                            }
                            VideoLog.Write($"ExtractThumb: OK {cue.Name} {w}x{h} -> {maxW}x{maxH}");
                        }
                        catch (Exception ex)
                        {
                            VideoLog.Write($"ExtractThumb: EXCEPTION {cue.Name}: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    VideoLog.Write($"ExtractThumb: CATCH {cue.Name}: {ex.Message}");
                    try { dec.Dispose(); } catch { }
                }
            }
            finally
            {
                ThumbSemaphore.Release();
            }
        });
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
        // arrastar a ordem só a partir de zonas "neutras" da linha: botões,
        // thumbs do FadeBar e slider não podem disparar o reorder da cue
        if (IsInteractiveSource(e.OriginalSource))
        {
            _dragCue = null;
            return;
        }
        _dragStart = e.GetPosition(null);
        _dragCue = HitTestItem(e.GetPosition(CueList))?.DataContext as Cue;
    }

    /// <summary>True se o clique caiu num controlo interativo (botão, thumb, FadeBar, scrollbar).</summary>
    private static bool IsInteractiveSource(object? original)
    {
        for (var d = original as DependencyObject; d is not null;
             d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase or Thumb or Controls.FadeBar) return true;
            if (d is ListBoxItem) break; // chegou à linha sem interativos
        }
        return false;
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
        var atLast = _playlist.Current is not null &&
                     _playlist.CurrentIndex >= _playlist.Cues.Count - 1;
        if (_playlist.Go() is { } cue)
        {
            if (atLast) WarnLastCue();
            TransitionTo(cue);
        }
    }

    /// <summary>Aviso visual: GO no último cue repete o cue atual.</summary>
    private void WarnLastCue()
    {
        TxtStatus.Text = "AVISO: GO no último cue — repetir o cue atual";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { UpdateStatus(); t.Stop(); };
        t.Start();
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
        if (_liveSlot < 0) return;

        var comp = _output?.Compositor;
        if (comp is null)
        {
            // output fechado (sem compositor): parar tudo já — o som nunca deve
            // continuar a tocar sem vídeo
            StopAllPlayback();
            return;
        }

        // fade to black; o decoder é libertado no fim do fade (OnFadeCompleted)
        var fadeOut = _playlist.Current?.FadeOutSeconds ?? 0.5;
        _pendingDip = null;
        _closingSlots.Add(_liveSlot);
        comp.SetOpacity(_liveSlot, 0, fadeOut);
        _liveSlot = -1;
        _standbyCue = null;
        BtnPause.Content = "PAUSE";

        // eject
        _playlist.Deselect();
        foreach (var c in _playlist.Cues)
        {
            c.IsLive = false;
            c.Progress = 0;
            if (!c.IsGroup) c.TimeText = Cue.Fmt(c.Duration);
        }
        TxtTime.Text = "--:--:-- / --:--:--";
        UpdateBigRemaining(null);
    }

    /// <summary>
    /// Para e liberta TODOS os decoders (programa, standby e layers) sem fades.
    /// Usado quando o output fecha (o compositor é destruído) e em estados sem output.
    /// </summary>
    private void StopAllPlayback()
    {
        _compWired = false;
        _closingSlots.Clear();
        _closingLayerSlots.Clear();
        _pendingDip = null;

        for (var i = 0; i < _slotDec.Length; i++)
        {
            _slotDec[i]?.Dispose();
            _slotDec[i] = null;
        }
        _liveSlot = -1;

        _standbyDec?.Dispose();
        _standbyDec = null;
        _standbyCue = null;

        foreach (var vm in _layerVms)
        {
            vm.State.Decoder?.Dispose();
            vm.State.Decoder = null;
            vm.State.Visible = false;
            vm.Refresh();
        }

        _playlist.Deselect();
        foreach (var c in _playlist.Cues)
        {
            c.IsLive = false;
            c.Progress = 0;
            if (!c.IsGroup) c.TimeText = Cue.Fmt(c.Duration);
        }

        BtnPause.IsEnabled = false;
        BtnStop.IsEnabled = false;
        BtnPause.Content = "PAUSE";
        TxtTime.Text = "--:--:-- / --:--:--";
        TxtNowPlaying.Text = "No cue loaded";
        TxtNowPlaying.Foreground = FindResource("TextMutedBrush") as Brush ?? Brushes.Gray;
        UpdateBigRemaining(null);
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    // ===== Layers dinâmicas (L1..L4) =====

    private LayerState? SelectedState =>
        _selectedLayer >= 0 && _selectedLayer < _layerVms.Count
            ? _layerVms[_selectedLayer].State
            : null;

    private void RefreshLayerSelection()
    {
        for (var i = 0; i < _layerVms.Count; i++)
            _layerVms[i].IsSelected = i == _selectedLayer;
        if (BtnAddLayer is not null)
            BtnAddLayer.IsEnabled = _layerVms.Count < MaxLayers;
    }

    private void LayerSelect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LayerVm vm) return;
        _selectedLayer = vm.Index;
        RefreshLayerSelection();
        RefreshGeomEditor();
    }

    private void AddLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_layerVms.Count >= MaxLayers) return;
        var vm = new LayerVm(
            new LayerState { X = 0.68, Y = 0.66, W = 0.28, H = 0.28 },
            _layerVms.Count);
        _layerVms.Add(vm);
        _selectedLayer = vm.Index;
        RefreshLayerSelection();
        RefreshGeomEditor();
        MarkDirty();
    }

    private void RemoveLayer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LayerVm vm) return;
        if (_layerVms.Count <= 1) return;

        var idx = _layerVms.IndexOf(vm);
        SetLayerVisible(idx, false); // tira do ar (fade) se visível

        var comp = _output?.Compositor;
        for (var i = idx; i < _layerVms.Count; i++)
            comp?.SetSource(2 + i, null);

        _layerVms.Remove(vm);
        _selectedLayer = Math.Min(_selectedLayer, _layerVms.Count - 1);
        RefreshLayerSelection();
        ReattachLayers();
        RefreshGeomEditor();
        MarkDirty();
    }

    /// <summary>Re-encadeia todas as layers visíveis nos slots (após add/remove/reabertura).</summary>
    private void ReattachLayers()
    {
        var comp = _output?.Compositor;
        if (comp is null) return;
        for (var i = 0; i < _layerVms.Count; i++)
        {
            var s = _layerVms[i].State;
            if (s.Visible && s.Decoder is not null)
            {
                comp.SetSource(2 + i, s.Decoder);
                comp.SetGeometry(2 + i, (float)s.X, (float)s.Y, (float)s.W, (float)s.H);
                comp.SetBlendMode(2 + i, s.BlendAdd);
                comp.SetOpacity(2 + i, 1, 0);
            }
            else
            {
                comp.SetSource(2 + i, null);
            }
        }
    }

    private void RefreshGeomEditor()
    {
        var s = SelectedState;
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
        PushGeom();
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
        PushGeom();
    }

    private void PushGeom()
    {
        var cw = GeomCanvas.ActualWidth;
        var ch = GeomCanvas.ActualHeight;
        if (cw < 10 || ch < 10) return;

        var s = SelectedState;
        if (s is null) return;
        s.X = Canvas.GetLeft(GeomRect) / cw;
        s.Y = Canvas.GetTop(GeomRect) / ch;
        s.W = GeomRect.Width / cw;
        s.H = GeomRect.Height / ch;
        _output?.Compositor?.SetGeometry(2 + _selectedLayer, (float)s.X, (float)s.Y, (float)s.W, (float)s.H);
    }

    private void LayerOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LayerVm vm) return;

        var dlg = new OpenFileDialog
        {
            Filter = "Vídeo|*.mp4;*.mov;*.mkv;*.webm|Todos os ficheiros|*.*",
            Title = $"Escolher vídeo para a {vm.Label}"
        };
        if (dlg.ShowDialog(this) != true) return;

        vm.SetFile(PathHelper.ToLongPath(dlg.FileName));
        MarkDirty();

        if (vm.State.Visible) // trocar em direto: abrir novo decoder e fazer swap no slot
            OpenLayerDecoder(vm.Index, vm.State.File!);
    }

    /// <summary>Abre o ficheiro da layer em background e entra no slot com fade-in.</summary>
    private void OpenLayerDecoder(int index, string file)
    {
        var vm = _layerVms[index];
        var s = vm.State;
        var slot = 2 + index;

        Task.Run(() =>
        {
            var dec = new FFDecoder();
            dec.AudioDeviceId = _masterAudioDevice; // layers usam o device master
            if (!dec.Open(file, autoPlay: false))
            {
                dec.Dispose();
                Dispatcher.BeginInvoke(() =>
                    TxtStatus.Text = $"ERRO na {vm.Label}: {file}");
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

                // Race hide→show: cancelar o fecho pendente ANTES do SetOpacity(0,0)
                // (o fade imediato dispara FadeCompleted e mataria o decoder novo)
                _closingLayerSlots.Remove(slot);

                s.Decoder?.Dispose();
                s.Decoder = dec;

                comp.SetSource(slot, dec);
                comp.SetGeometry(slot, (float)s.X, (float)s.Y, (float)s.W, (float)s.H);
                comp.SetBlendMode(slot, s.BlendAdd);
                comp.SetOpacity(slot, 0, 0);
                ApplyVolumes();                     // respeita mute da layer
                dec.Play();
                comp.SetOpacity(slot, 1, 0.4); // fade-in rápido
            });
        });
    }

    private void LayerToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LayerVm vm)
            SetLayerVisible(vm.Index, !vm.State.Visible);
    }

    private void SetLayerVisible(int index, bool visible)
    {
        var vm = _layerVms[index];
        var s = vm.State;
        if (visible && string.IsNullOrEmpty(s.File)) return;

        SetOutput(true);
        if (_output is null) return;

        s.Visible = visible;
        vm.Refresh();
        var slot = 2 + index;

        if (visible)
        {
            OpenLayerDecoder(index, s.File!);
        }
        else
        {
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
    {
        if (TxtVolume is not null) TxtVolume.Text = $"{e.NewValue:F0}%";
        ApplyVolumes();
    }

    /// <summary>Fonte única de volumes: master mute + mute por layer.</summary>
    private void ApplyVolumes()
    {
        var master = VolumeSlider.Value / 100.0;
        var comp = _output?.Compositor;
        if (comp is null) return;

        var progVol = _masterMuted ? 0.0 : master;
        comp.SetBaseVolume(0, progVol);
        comp.SetBaseVolume(1, progVol);
        for (var i = 0; i < _layerVms.Count; i++)
            comp.SetBaseVolume(2 + i, _layerVms[i].State.Muted ? 0.0 : master);

        if (_standbyDec is not null) _standbyDec.Volume = progVol;
    }

    private void MasterMute_Click(object sender, RoutedEventArgs e)
        => SetMasterMute(!_masterMuted);

    private void SetMasterMute(bool muted)
    {
        _masterMuted = muted;
        BtnMasterMute.Content = muted ? "\U0001F507" : "\U0001F50A";
        ApplyVolumes();
    }

    private void LayerMute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LayerVm vm)
            SetLayerMute(vm.Index, vm.SoundOn); // SoundOn = estado antes do clique
    }

    private void SetLayerMute(int index, bool muted)
    {
        var vm = _layerVms[index];
        vm.State.Muted = muted;
        vm.Refresh();
        ApplyVolumes();
    }

    private void LayerBlend_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LayerVm vm)
            SetLayerBlend(vm.Index, !vm.State.BlendAdd);
    }

    /// <summary>Blend mode da layer: alpha normal ou aditivo (Add).</summary>
    private void SetLayerBlend(int index, bool additive)
    {
        var vm = _layerVms[index];
        vm.State.BlendAdd = additive;
        vm.Refresh();
        _output?.Compositor?.SetBlendMode(2 + index, additive);
    }

    private void CueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only respond to double-click on thumbnail or name area (left ~75% of item)
        var item = CueList.ContainerFromElement((DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item is not null)
        {
            var pos = e.GetPosition(item);
            if (pos.X > item.ActualWidth * 0.50) return;
        }

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

        if (_playlist.Current is { } cur)
        {
            TxtNowPlaying.Text = $" ON AIR - {cur.Name}";
            TxtNowPlaying.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)); // red
        }
        else
        {
            TxtNowPlaying.Text = "No cue loaded";
            TxtNowPlaying.Foreground = FindResource("TextMutedBrush") as Brush ?? Brushes.Gray;
        }
        UpdateStatus();
    }

    // ===== Output (2.º ecrã / projetor) =====

    private string _outputDeviceName = "";

    private void BtnOutput_Click(object sender, RoutedEventArgs e) => SetOutput(_output is null);

    private void SetOutput(bool open) => SetOutput(open, null);

    /// <summary>
    /// Abre/fecha a janela de output. Com <paramref name="requestedDevice"/> dado,
    /// o output abre-se (ou move-se) para esse ecrã — routing por cue.
    /// </summary>
    private void SetOutput(bool open, string? requestedDevice)
    {
        if (!open)
        {
            _output?.Close(); // Closed handler para tudo (StopAllPlayback) e restaura o refresh
            return;
        }

        var screen = ResolveScreen(requestedDevice);

        if (_output is not null)
        {
            // já aberto: mover para o ecrã alvo se for diferente
            if (!string.Equals(_outputDeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
                MoveOutputTo(screen);
            return;
        }

        _outputDeviceName = screen.DeviceName;
        _output = new OutputWindow();
        _output.Closed += (_, _) =>
        {
            _output = null;
            _outputDeviceName = "";
            _overlay?.Close();
            _overlay = null;
            BtnOutput.Content = " External Display ON ";
            StopAllPlayback();
            RestoreOutputRefresh();
            _outputInfo = "—";
            UpdateStatus();
        };

        ApplyOutputRefresh(screen);

        _output.Left = screen.Bounds.Left;
        _output.Top = screen.Bounds.Top;
        _output.Width = screen.Bounds.Width;
        _output.Height = screen.Bounds.Height;
        _output.Show();
        _output.WindowState = WindowState.Maximized;

        // overlay de confiança (cue + tempo restante) por cima do output
        EnsureOverlay();

        // deteção do modo da saída (interlaçado/progressivo) para a status bar
        _outputInfo = DisplayInfo.Describe(screen.DeviceName);
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
                    comp.SetVolumeScale(slot, _slotVolScale[slot]); // manter volume por cue
                    comp.SetOpacity(slot, 1, 0);
                }

            ReattachLayers();

            ApplyVolumes(); // respeita mutes
        }));

        BtnOutput.Content = " External Display OFF ";
    }

    /// <summary>Resolve o ecrã para um device pedido (ou o preset do projeto; fallback 2.º/1.º ecrã).</summary>
    private static System.Windows.Forms.Screen ResolveScreen(string? requestedDevice)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var name = requestedDevice is { Length: > 0 } dev ? dev : null;
        var screen = name is not null
            ? screens.FirstOrDefault(s => string.Equals(s.DeviceName, name, StringComparison.OrdinalIgnoreCase))
            : null;
        return screen ?? (screens.Length > 1 ? screens[1] : screens[0]);
    }

    /// <summary>Move a janela de output para outro ecrã (routing por cue). Sem crossfade entre ecrãs.</summary>
    private void MoveOutputTo(System.Windows.Forms.Screen screen)
    {
        if (_output is null) return;

        Video.VideoLog.Write($"Routing: output -> {screen.DeviceName}");
        RestoreOutputRefresh();
        _outputDeviceName = screen.DeviceName;
        ApplyOutputRefresh(screen);

        _output.WindowState = WindowState.Normal;
        _output.Left = screen.Bounds.Left;
        _output.Top = screen.Bounds.Top;
        _output.Width = screen.Bounds.Width;
        _output.Height = screen.Bounds.Height;
        _output.WindowState = WindowState.Maximized;

        _outputInfo = DisplayInfo.Describe(screen.DeviceName);
        UpdateStatus();
    }

    /// <summary>Preset de refresh por projeto: aplica temporariamente o modo no ecrã dado.</summary>
    private void ApplyOutputRefresh(System.Windows.Forms.Screen screen)
    {
        if (_playlist.OutputRefresh > 0)
        {
            _restoreRefreshDevice = screen.DeviceName;
            _restoreRefreshFreq = DisplayInfo.TrySetRefreshRate(screen.DeviceName, _playlist.OutputRefresh, out var err);
            Video.VideoLog.Write($"Output preset: {screen.DeviceName} @{_playlist.OutputRefresh}Hz " +
                                 $"-> {(err is null ? "OK" : err)} (orig {_restoreRefreshFreq}Hz)");
        }
        else
        {
            RestoreOutputRefresh();
        }
    }

    private void RestoreOutputRefresh()
    {
        if (_restoreRefreshDevice.Length == 0 || _restoreRefreshFreq <= 0) return;
        var err = DisplayInfo.RestoreRefreshRate(_restoreRefreshDevice, _restoreRefreshFreq);
        Video.VideoLog.Write($"Output preset: restaurado {_restoreRefreshDevice} @{_restoreRefreshFreq}Hz" +
                             $"{(err is null ? "" : $" (erro: {err})")}");
        _restoreRefreshDevice = "";
        _restoreRefreshFreq = 0;
    }

    /// <summary>Menu do botão de output: escolher ecrã de saída + refresh (preset por projeto).</summary>
    private void BtnOutput_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = BtnOutput, StaysOpen = true };

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var name = screen.DeviceName.Replace("\\\\.\\", "");
            var item = new MenuItem
            {
                Header = $"Display: {name} ({screen.Bounds.Width}×{screen.Bounds.Height})",
                IsCheckable = true,
                IsChecked = string.Equals(_playlist.OutputDevice, screen.DeviceName, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => { _playlist.OutputDevice = screen.DeviceName; MarkDirty(); };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        foreach (var hz in new[] { 0, 50, 60, 75 })
        {
            var item = new MenuItem
            {
                Header = hz == 0 ? "Refresh: auto (sistema)" : $"Refresh: {hz} Hz",
                IsCheckable = true,
                IsChecked = _playlist.OutputRefresh == hz,
            };
            item.Click += (_, _) => { _playlist.OutputRefresh = hz; MarkDirty(); UpdateStatus(); };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Overlay de confiança: cue atual + tempo restante no ecrã de palco.</summary>
    private void EnsureOverlay()
    {
        if (_overlay is not null) return;
        _overlay = new OverlayWindow();
        if (BtnOverlay.IsChecked != true)
            _overlay.Visibility = Visibility.Collapsed;
        _overlay.Show();
        UpdateOverlay("—");
    }

    private void UpdateOverlay(string text)
    {
        if (_overlay is null || _output is null) return;
        _overlay.SetText(text);
        if (_overlay.Left != _output.Left || _overlay.Top != _output.Top ||
            _overlay.Width != _output.Width || _overlay.Height != _output.Height)
        {
            _overlay.Left = _output.Left;
            _overlay.Top = _output.Top;
            _overlay.Width = _output.Width;
            _overlay.Height = _output.Height;
        }
    }

    private void Overlay_Toggled(object sender, RoutedEventArgs e)
    {
        if (_overlay is not null)
            _overlay.Visibility = BtnOverlay.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>Preview PROGRAM (30 fps): lê o frame composto do GPU e mostra no WriteableBitmap.</summary>
    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (_output?.Compositor is not { } comp) return;
        var w = D3DCompositor.PreviewWidth;
        var h = D3DCompositor.PreviewHeight;

        if (_previewBuf.Length != w * h * 4)
            _previewBuf = new byte[w * h * 4];
        if (!comp.TryCopyPreviewInto(_previewBuf, out var seq)) return;

        if (_previewBmp is null || _previewBmp.PixelWidth != w || _previewBmp.PixelHeight != h)
            _previewBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        _previewBmp.WritePixels(new Int32Rect(0, 0, w, h), _previewBuf, w * 4, 0);
        if (!ReferenceEquals(PreviewImage.Source, _previewBmp))
            PreviewImage.Source = _previewBmp;
    }

    // ===== Estado / atalhos =====

    private void UpdateStatus()
    {
        var preloaded = _standbyCue is not null ? $"preload: {_standbyCue.Name}" : "preload: —";
        var preset = _playlist.OutputRefresh > 0 ? $"  •  preset: {_playlist.OutputRefresh}Hz" : "";
        var dev = _outputDeviceName.Replace("\\\\.\\", "");
        TxtStatus.Text = $"READY  •  {_playlist.Cues.Count} cues  •  {preloaded}  •  " +
                         $"Output: {(dev.Length > 0 ? dev + " " : "")}{_outputInfo}{preset}  •  OSC porta {_companion.Port}";
    }

    private void Previous()
    {
        if (_playlist.Previous() is { } cue)
            TransitionTo(cue);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var handled = true;

        if (e.Key == Key.L && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            BtnShowMode.IsChecked = !_showMode; // toggled → SetShowMode
        }
        else if (e.Key == _keyGo || e.Key == _keyNext) Go();
        else if (e.Key == _keyPrev) Previous();
        else if (e.Key == _keyStop) StopPlayback();
        else if (e.Key == _keyPause) TogglePause();
        else handled = false;

        e.Handled = handled;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_projectDirty && _playlist.Cues.Count > 0)
        {
            var res = MessageBox.Show(this,
                "O projeto tem alterações por guardar.\n\nGuardar antes de fechar?",
                "Smart Play Cue", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (res == MessageBoxResult.Yes && !SaveProjectAs()) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // lembrar janela + último projeto
        var st = AppState.Load();
        st.Left = Left;
        st.Top = Top;
        st.Width = ActualWidth;
        st.Height = ActualHeight;
        st.Maximized = WindowState == WindowState.Maximized;
        if (_projectPath.Length > 0)
            st.LastProjectPath = _projectPath;
        st.Save();

        _uiTimer.Stop();
        _previewTimer.Stop();
        _backupTimer.Stop();
        RestoreOutputRefresh();
        StopAllPlayback();
        _companion.Dispose();
        _output?.Close();
        base.OnClosed(e);
    }

    // ─── Inline cue buttons ───
    private void InlinePlay_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is Cue cue)
        {
            // Tocar ESTE cue (seleciona-o como atual e transita diretamente —
            // nunca Go(), que avança para o cue seguinte)
            var idx = _playlist.Cues.IndexOf(cue);
            if (idx >= 0 && _playlist.Select(idx) is not null)
                TransitionTo(cue);
        }
    }

    private void InlineStop_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_liveSlot >= 0) StopPlayback();
    }

    private void InlinePause_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is Cue cue && _liveSlot >= 0)
        {
            var liveCue = _playlist.Current;
            if (liveCue?.Id == cue.Id) TogglePause();
        }
    }

    private void InlineRewind_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_liveSlot < 0) return;

        var cue = _playlist.Current;
        StopPlayback();

        if (cue is not null)
        {
            // prepara o cue para replay (botão PLAY da linha); não o seleciona
            // como "no ar" — evita ON AIR falso na UI
            _standbyCue = cue;
            _standbyDec?.Dispose();
            _standbyDec = null;

            var dec = new FFDecoder();
            dec.AudioDeviceId = cue.AudioOutputDevice.Length > 0
                ? cue.AudioOutputDevice
                : _masterAudioDevice;
            Task.Run(() =>
            {
                if (!dec.Open(cue.FilePath, autoPlay: false))
                {
                    dec.Dispose();
                    return;
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (_standbyCue?.Id != cue.Id) { dec.Dispose(); return; }
                    _standbyDec = dec;
                    UpdateStatus();
                });
            });
        }
    }

    private void TagColor_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as Border)?.Tag is not Cue cue) return;

        var colors = new[] { "#E53935", "#FF9800", "#FFC107", "#4CAF50", "#2196F3", "#9C27B0", "#00BCD4", "#795548" };
        var names = new[] { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Cyan", "Brown" };
        var menu = new ContextMenu();
        for (int i = 0; i < colors.Length; i++)
        {
            var clr = colors[i];
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(clr)),
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = names[i], VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = sp };
            item.Click += (_, _) => cue.TagColor = clr;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void InlineMute_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is Cue cue)
        {
            cue.IsAudioMuted = !cue.IsAudioMuted;
            ApplyCueAudioMute(cue);
        }
    }

    private void AudioOutput_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is not Cue cue) return;

        var menu = new ContextMenu();
        var defaultItem = new MenuItem { Header = "System Default", IsCheckable = true, IsChecked = string.IsNullOrEmpty(cue.AudioOutputDevice) };
        defaultItem.Click += (_, _) => cue.AudioOutputDevice = "";
        menu.Items.Add(defaultItem);
        menu.Items.Add(new Separator());

        var devices = AudioDeviceService.GetOutputDevices();
        foreach (var (id, name) in devices)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = cue.AudioOutputDevice == id };
            var devId = id;
            item.Click += (_, _) => cue.AudioOutputDevice = devId;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void MasterAudioDevice_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu();

        var defaultItem = new MenuItem { Header = "System Default", IsCheckable = true, IsChecked = string.IsNullOrEmpty(_masterAudioDevice) };
        defaultItem.Click += (_, _) => _masterAudioDevice = "";
        menu.Items.Add(defaultItem);
        menu.Items.Add(new Separator());

        var devices = AudioDeviceService.GetOutputDevices();
        foreach (var (id, name) in devices)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = _masterAudioDevice == id };
            var devId = id;
            item.Click += (_, _) => _masterAudioDevice = devId;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void InlineFillMode_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is not Cue cue) return;

        cue.FillMode = cue.FillMode == "Uniform" ? "Fill" : "Uniform";

        // update the SVG icon
        if (sender is Button btn && btn.Content is SharpVectors.Converters.SvgViewbox svg)
        {
            svg.Source = new Uri(cue.FillMode == "Uniform"
                ? "pack://application:,,,/Assets/Icons/aspect-ratio.svg"
                : "pack://application:,,,/Assets/Icons/fit-screen.svg");
        }

        // if this cue is currently playing, update geometry live
        if (_liveSlot >= 0 && _playlist.Current?.Id == cue.Id && _output?.Compositor is { } comp)
        {
            var dec = _slotDec[_liveSlot];
            if (dec is not null && dec.FrameWidth > 0)
                ApplyFillGeometry(cue, dec, comp, _liveSlot);
        }
    }

    private void InlineRotation_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.Tag is not Cue cue) return;

        cue.Rotation = cue.Rotation switch { 0 => 90, 90 => 180, 180 => 270, _ => 0 };

        if (_liveSlot >= 0 && _playlist.Current?.Id == cue.Id && _output?.Compositor is { } comp)
        {
            var dec = _slotDec[_liveSlot];
            if (dec is not null)
                ApplyFillGeometry(cue, dec, comp, _liveSlot);
        }
    }

    private static void ApplyFillGeometry(Cue cue, FFDecoder dec, D3DCompositor comp, int slot)
    {
        float x = 0, y = 0, w = 1, h = 1;

        if (cue.FillMode == "Uniform" && dec.FrameWidth > 0 && dec.FrameHeight > 0)
        {
            var vidRatio = (double)dec.FrameWidth / dec.FrameHeight;
            var outRatio = (double)comp.OutputWidth / comp.OutputHeight;
            if (vidRatio > outRatio)
            {
                h = (float)(outRatio / vidRatio);
                y = (1f - h) / 2f;
            }
            else
            {
                w = (float)(vidRatio / outRatio);
                x = (1f - w) / 2f;
            }
        }

        comp.SetGeometry(slot, x, y, w, h);
        comp.SetRotation(slot, (float)(cue.Rotation * Math.PI / 180.0));
    }

    private void ApplyCueAudioMute(Cue cue)
    {
        if (_liveSlot >= 0 && _playlist.Current?.Id == cue.Id)
        {
            // mute por cue entra na cadeia de volume do compositor
            // (mute + volume por cue sobrevivem ao fade e ao reattach)
            var scale = cue.IsAudioMuted ? 0.0 : Math.Clamp(cue.Volume, 0.0, 1.0);
            _slotVolScale[_liveSlot] = scale;
            _output?.Compositor?.SetVolumeScale(_liveSlot, scale);
        }
    }

    private void OutputBadge_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as Border)?.Tag is not Cue cue) return;

        var menu = new ContextMenu();

        var auto = new MenuItem
        {
            Header = "Auto (preset do projeto)",
            IsCheckable = true,
            IsChecked = cue.Output == 0,
        };
        auto.Click += (_, _) => cue.Output = 0;
        menu.Items.Add(auto);
        menu.Items.Add(new Separator());

        var screens = System.Windows.Forms.Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var idx = i + 1;
            var name = screens[i].DeviceName.Replace("\\\\.\\", "");
            var item = new MenuItem
            {
                Header = $"Output {idx} — {name} ({screens[i].Bounds.Width}×{screens[i].Bounds.Height})",
                IsCheckable = true,
                IsChecked = cue.Output == idx,
            };
            item.Click += (_, _) => cue.Output = idx;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
