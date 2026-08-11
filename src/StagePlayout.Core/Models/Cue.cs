using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace StagePlayout.Core.Models;

/// <summary>
/// Um cue = um clip na playlist (modelo inspirado no Mitti).
/// </summary>
public class Cue : INotifyPropertyChanged
{
    // settable para restauro de projetos (ligações ParentId)
    public Guid Id { get; set; } = Guid.NewGuid();

    private string _name = "";
    public required string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public required string FilePath { get; set; }

    /// <summary>Só para grupos: no fim do último filho, volta ao primeiro (loop da playlist).</summary>
    private bool _loopGroup;
    public bool LoopGroup
    {
        get => _loopGroup;
        set { _loopGroup = value; OnPropertyChanged(); }
    }

    // ===== Grupos / playlists =====

    private bool _isGroup;
    public bool IsGroup
    {
        get => _isGroup;
        set { _isGroup = value; OnPropertyChanged(); }
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandGlyph)); }
    }

    [JsonIgnore]
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    private Guid? _parentId;
    public Guid? ParentId
    {
        get => _parentId;
        set { _parentId = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsChild)); }
    }

    [JsonIgnore]
    public bool IsChild => ParentId != null;

    private int _childCount;
    public int ChildCount
    {
        get => _childCount;
        set { _childCount = value; OnPropertyChanged(); }
    }

    private TimeSpan _duration;
    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            _duration = value;
            OnPropertyChanged();
            if (!IsLive) TimeText = Fmt(value);
        }
    }

    /// <summary>Formatação timecode: sempre HH:MM:SS.</summary>
    public static string Fmt(TimeSpan t) => t.ToString(@"hh\:mm\:ss");

    // ===== Info / runtime (lista de cues) =====

    private string _infoText = "";
    /// <summary>Ex.: "1920×1080 • h264 • aac • 245 MB"</summary>
    public string InfoText
    {
        get => _infoText;
        set { _infoText = value; OnPropertyChanged(); }
    }

    private string _timeText = "";
    /// <summary>Duração ("03:12") ou, quando no ar, "12:34 / -01:02".</summary>
    public string TimeText
    {
        get => _timeText;
        set { _timeText = value; OnPropertyChanged(); }
    }

    private double _progress;
    [JsonIgnore]
    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    private bool _isLive;
    [JsonIgnore]
    public bool IsLive
    {
        get => _isLive;
        set { _isLive = value; OnPropertyChanged(); }
    }

    /// <summary>O que acontece quando o clip chega ao fim.</summary>
    private CueEnd _end = CueEnd.HoldLastFrame;
    public CueEnd End
    {
        get => _end;
        set { _end = value; OnPropertyChanged(); }
    }

    public double FadeInSeconds { get; set; } = 0.5;
    public double FadeOutSeconds { get; set; } = 0.5;

    /// <summary>0.0 – 1.0</summary>
    public double Volume { get; set; } = 1.0;

    private BitmapSource? _thumbnail;
    [JsonIgnore]
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Name;
}
