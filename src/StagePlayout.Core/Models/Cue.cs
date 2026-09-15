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

    private static int _nextId = 1;
    public static int NextDisplayId() => _nextId++;
    public int DisplayId { get; set; }

    private string _tagColor = "#555555";
    public string TagColor
    {
        get => _tagColor;
        set { _tagColor = value; OnPropertyChanged(); }
    }

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
        set { _isLive = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowAudioMeter)); }
    }

    /// <summary>O que acontece quando o clip chega ao fim.</summary>
    private CueEnd _end = CueEnd.HoldLastFrame;
    public CueEnd End
    {
        get => _end;
        set {
            _end = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowStopIcon));
            OnPropertyChanged(nameof(ShowLoopIcon));
            OnPropertyChanged(nameof(ShowHoldIcon));
        }
    }

    private double _fadeInSeconds = 0.5;
    public double FadeInSeconds
    {
        get => _fadeInSeconds;
        set { _fadeInSeconds = value; OnPropertyChanged(); }
    }

    private double _fadeOutSeconds = 0.5;
    public double FadeOutSeconds
    {
        get => _fadeOutSeconds;
        set { _fadeOutSeconds = value; OnPropertyChanged(); }
    }

    /// <summary>Tipo de transição de entrada: Cross (dissolve) ou Dip (via preto).</summary>
    private FadeType _fadeType = FadeType.Cross;
    public FadeType FadeType
    {
        get => _fadeType;
        set { _fadeType = value; OnPropertyChanged(); }
    }

    /// <summary>0.0 – 1.0</summary>
    private double _volume = 1.0;
    public double Volume
    {
        get => _volume;
        set { _volume = value; OnPropertyChanged(); }
    }

    /// <summary>Output screen index (1-based).</summary>
    [JsonIgnore]
    public int Output
    {
        get => _output;
        set { _output = value; OnPropertyChanged(); }
    }
    private int _output = 2;

    /// <summary>Audio muted for this cue.</summary>
    [JsonIgnore]
    public bool IsAudioMuted
    {
        get => _isAudioMuted;
        set { _isAudioMuted = value; OnPropertyChanged(); OnPropertyChanged(nameof(MuteIcon)); }
    }
    private bool _isAudioMuted;

    [JsonIgnore] public string MuteIcon => _isAudioMuted ? "\U0001F507" : "\U0001F50A";

    /// <summary>Fill mode: Fill (stretch) or Uniform (keep aspect ratio with letterbox).</summary>
    [JsonIgnore]
    public string FillMode
    {
        get => _fillMode;
        set { _fillMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(FillModeIcon)); }
    }
    private string _fillMode = "Fill";

    [JsonIgnore] public string FillModeIcon => _fillMode == "Uniform" ? "⊡" : "⊞";

    /// <summary>Rotation angle: 0, 90, 180, or 270.</summary>
    [JsonIgnore]
    public int Rotation
    {
        get => _rotation;
        set { _rotation = value; OnPropertyChanged(); OnPropertyChanged(nameof(RotationText)); }
    }
    private int _rotation = 0;

    [JsonIgnore] public string RotationText => $"{_rotation}°";

    /// <summary>True se o cue tem um audio track.</summary>
    [JsonIgnore]
    public bool HasAudio
    {
        get => _hasAudio;
        set { _hasAudio = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowAudioMeter)); }
    }
    private bool _hasAudio;

    /// <summary>Show mode: true bloqueia editores inline (FadeBar) deste cue.</summary>
    [JsonIgnore]
    private bool _editLocked;
    public bool EditLocked
    {
        get => _editLocked;
        set { _editLocked = value; OnPropertyChanged(); }
    }

    /// <summary>True if audio meter should be visible (has audio AND is live).</summary>
    [JsonIgnore] public bool ShowAudioMeter => _hasAudio && IsLive;

    /// <summary>Audio peak levels (0-1)</summary>
    [JsonIgnore]
    public double AudioPeakL
    {
        get => _audioPeakL;
        set { _audioPeakL = value; OnPropertyChanged(); }
    }
    private double _audioPeakL;
    [JsonIgnore]
    public double AudioPeakR
    {
        get => _audioPeakR;
        set { _audioPeakR = value; OnPropertyChanged(); }
    }
    private double _audioPeakR;

    /// <summary>Audio output device ID (NAudio device ID). Empty = system default.</summary>
    [JsonIgnore]
    public string AudioOutputDevice
    {
        get => _audioOutputDevice;
        set { _audioOutputDevice = value; OnPropertyChanged(); }
    }
    private string _audioOutputDevice = "";

    /// <summary>ID of the cue to jump to when this one ends (0 = follow playlist order).</summary>
    private Guid _nextCueId;
    public Guid NextCueId
    {
        get => _nextCueId;
        set { _nextCueId = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowJumpIcon)); }
    }

    [JsonIgnore] public bool ShowStopIcon => End == CueEnd.Stop;
    [JsonIgnore] public bool ShowLoopIcon => End == CueEnd.Loop;
    [JsonIgnore] public bool ShowJumpIcon => NextCueId != Guid.Empty;
    [JsonIgnore] public bool ShowHoldIcon => End == CueEnd.HoldLastFrame;

    [JsonIgnore]
    public int JumpTargetId
    {
        get
        {
            // This can't easily reference the playlist; we'll set it externally
            return _jumpTargetId;
        }
        set { _jumpTargetId = value; OnPropertyChanged(); OnPropertyChanged(nameof(JumpTargetText)); }
    }
    private int _jumpTargetId;

    [JsonIgnore] public string JumpTargetText => _jumpTargetId > 0 ? _jumpTargetId.ToString() : "";

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
