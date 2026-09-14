using System.Net;
using System.Net.Sockets;
using StagePlayout.App.Video;
using StagePlayout.Core.Services;

namespace StagePlayout.App.Services;

/// <summary>
/// Controlo remoto via OSC (UDP) — compatível com Bitfocus Companion / Stream Deck.
/// Implementação própria (sem Rug.Osc): recebe comandos em :8010 e envia feedback
/// (tempo restante) para o host/porta configurados em companion.json.
///
/// Configuração no Companion:
///   1. Adicionar ligação "Generic OSC"
///   2. Target IP: IP desta máquina (ou 127.0.0.1 se for a mesma)
///   3. Port: 8010
///
/// Comandos (Send OSC message):
///   /stageplayout/go              → GO (avançar e tocar)
///   /stageplayout/pause           → pausa
///   /stageplayout/stop            → stop / fade to black
///   /stageplayout/next            → cue seguinte
///   /stageplayout/prev            → cue anterior
///   /stageplayout/cue  [int]      → tocar cue N (1-based)
///   /stageplayout/volume [0-1]    → volume master
///   /stageplayout/output [0|1]    → abrir/fechar janela de output
///   /stageplayout/panic           → eject all
///   /stageplayout/layer/1/show    → mostrar camada 1 (layer flutuante)
///   /stageplayout/layer/1/hide    → ocultar camada 1
///   /stageplayout/layer/1/toggle  → alternar camada 1
///   /stageplayout/layer/2/...     → idem para a camada 2
///   /stageplayout/mute [0|1]      → mute master (sem arg = toggle)
///   /stageplayout/mute/toggle     → toggle mute master
///   /stageplayout/layer/1/mute [0|1]    → mute layer 1 (1 = muda)
///   /stageplayout/layer/1/mute/toggle   → toggle mute layer 1
///
/// Feedback enviado (para companion.json → FeedbackHost:FeedbackPort):
///   /smartcue/time/hh|mm|ss       → horas/min/seg restantes (int)
///   /smartcue/time/total          → segundos totais restantes (int)
///   /smartcue/status              → "STANDBY" | "ON AIR" (string)
/// </summary>
public sealed class CompanionControl : IDisposable
{
    public const int DefaultPort = 8010;
    public const int DefaultFeedbackPort = 8011;

    private readonly UdpClient _receiver;
    private UdpClient? _sender;
    private readonly object _sendLock = new();
    private Thread? _thread;
    private volatile bool _running;

    private string _sendHost;
    private int _sendPort;

    public event EventHandler? GoRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler<int>? PlayCueRequested;      // 1-based
    public event EventHandler<double>? VolumeRequested;    // 0.0 – 1.0
    public event EventHandler<bool>? OutputRequested;
    public event EventHandler<(int Layer, bool Show)>? LayerVisibilityRequested;
    public event EventHandler<int>? LayerToggleRequested;
    public event EventHandler<bool>? MasterMuteRequested;          // true = muted
    public event EventHandler? MasterMuteToggleRequested;
    public event EventHandler<(int Layer, bool Muted)>? LayerMuteRequested;
    public event EventHandler<int>? LayerMuteToggleRequested;
    public event EventHandler<(int Layer, bool Additive)>? LayerBlendRequested;
    public event EventHandler<int>? LayerBlendToggleRequested;
    public event EventHandler<int>? CueMuteToggleRequested;        // 1-based
    public event EventHandler? PanicRequested;                     // eject all

    public int Port { get; }

    /// <summary>IP/porta para onde o feedback (tempo restante) é enviado.</summary>
    public string FeedbackHost => _sendHost;
    public int FeedbackPort => _sendPort;

    public CompanionControl(int port = DefaultPort, string feedbackHost = "127.0.0.1", int feedbackPort = DefaultFeedbackPort)
    {
        Port = port;
        _sendHost = feedbackHost;
        _sendPort = feedbackPort;
        _receiver = new UdpClient();
    }

    public static CompanionControl FromConfig(CompanionConfig cfg)
        => new(cfg.ListenPort, cfg.FeedbackHost, cfg.FeedbackPort);

    /// <summary>Envia o contador de frame drops (saúde do vsync) para o Companion.</summary>
    public void SendHealth(long drops)
    {
        Send($"/smartcue/health/drops", drops);
    }

    /// <summary>
    /// Envia o cue atual para o Companion (nome + número; 0 = nenhum cue no ar).
    /// </summary>
    public void SendCueInfo(int? displayId, string? name)
    {
        Send($"/smartcue/cue/id", displayId ?? 0);
        Send($"/smartcue/cue/name", name ?? "");
    }

    /// <summary>
    /// Envia o tempo restante para o Companion (feedback nos botões HH/MM/SS).
    /// </summary>
    public void SendRemainingTime(TimeSpan? remaining, string status)
    {
        var hh = remaining?.Hours ?? 0;
        var mm = remaining?.Minutes ?? 0;
        var ss = remaining?.Seconds ?? 0;
        Send($"/smartcue/time/hh", hh);
        Send($"/smartcue/time/mm", mm);
        Send($"/smartcue/time/ss", ss);
        Send($"/smartcue/time/total", remaining is { } r ? (int)r.TotalSeconds : 0);
        Send($"/smartcue/status", status);
    }

    private void Send(string address, object value)
    {
        try
        {
            lock (_sendLock)
            {
                if (_sender is null)
                {
                    _sender = new UdpClient();
                    _sender.Connect(_sendHost, _sendPort);
                }
                var data = OscUdp.Pack(address, value);
                _sender.Send(data, data.Length);
            }
        }
        catch
        {
            try { _sender?.Close(); } catch { }
            _sender = null; // reconectar na próxima
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        try
        {
            _receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _receiver.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        catch (Exception ex)
        {
            VideoLog.Write($"OSC listen :{Port} FALHOU: {ex.Message}");
        }

        _thread = new Thread(ListenLoop) { IsBackground = true, Name = "CompanionOSC" };
        _thread.Start();
    }

    private void ListenLoop()
    {
        try
        {
            while (_running)
            {
                IPEndPoint? remote = null;
                byte[] data;
                try
                {
                    data = _receiver.Receive(ref remote);
                }
                catch (SocketException) { break; }         // socket fechado no Dispose
                catch (ObjectDisposedException) { break; }

                foreach (var (address, args) in OscUdp.Unpack(data))
                    Dispatch(address, args);
            }
        }
        catch
        {
            // socket fechado no Dispose — saída normal
        }
    }

    private void Dispatch(string address, object?[] args)
    {
        address = address.ToLowerInvariant().TrimEnd('/');

        // Camadas flutuantes: /stageplayout/layer/{1|2}/{show|hide|toggle|mute|mute/toggle}
        if (address.StartsWith("/stageplayout/layer/"))
        {
            var parts = address.Split('/'); // ["", stageplayout, layer, N, ação, (toggle)]
            if (parts.Length >= 5 && int.TryParse(parts[3], out var layer))
            {
                var toggle = parts.Length == 6 && parts[5] == "toggle";
                switch (parts[4])
                {
                    case "show": LayerVisibilityRequested?.Invoke(this, (layer, true)); break;
                    case "hide": LayerVisibilityRequested?.Invoke(this, (layer, false)); break;
                    case "toggle": LayerToggleRequested?.Invoke(this, layer); break;
                    case "mute":
                        if (toggle)
                            LayerMuteToggleRequested?.Invoke(this, layer);
                        else if (args.Length > 0 && ToInt(args[0], out var m))
                            LayerMuteRequested?.Invoke(this, (layer, m != 0));
                        break;
                    case "blend":
                        if (toggle)
                            LayerBlendToggleRequested?.Invoke(this, layer);
                        else if (args.Length > 0 && ToInt(args[0], out var b))
                            LayerBlendRequested?.Invoke(this, (layer, b != 0));
                        break;
                }
            }
            return;
        }

        switch (address)
        {
            case "/stageplayout/go":
                GoRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "/stageplayout/pause":
                PauseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "/stageplayout/stop":
                StopRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "/stageplayout/mute":
                if (args.Length > 0 && ToInt(args[0], out var mm))
                    MasterMuteRequested?.Invoke(this, mm != 0);
                else
                    MasterMuteToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "/stageplayout/mute/toggle":
                MasterMuteToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "/stageplayout/next":
                NextRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "/stageplayout/prev":
                PreviousRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "/stageplayout/cue":
                if (args.Length > 0 && ToInt(args[0], out var n))
                    PlayCueRequested?.Invoke(this, n);
                break;
            case "/stageplayout/cue/mute":
                if (args.Length > 0 && ToInt(args[0], out var cm))
                    CueMuteToggleRequested?.Invoke(this, cm);
                break;
            case "/stageplayout/panic":
                PanicRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "/stageplayout/volume":
                if (args.Length > 0 && ToDouble(args[0], out var v))
                    VolumeRequested?.Invoke(this, v > 1.0 ? v / 100.0 : v);
                break;
            case "/stageplayout/output":
                var open = args.Length == 0 || (ToInt(args[0], out var o) && o != 0);
                OutputRequested?.Invoke(this, open);
                break;
        }
    }

    private static bool ToInt(object? value, out int result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case float f: result = (int)f; return true;
            case double d: result = (int)d; return true;
            case bool b: result = b ? 1 : 0; return true;
            case string s when int.TryParse(s, out var p): result = p; return true;
            default: result = 0; return false;
        }
    }

    private static bool ToDouble(object? value, out double result)
    {
        switch (value)
        {
            case float f: result = f; return true;
            case int i: result = i; return true;
            case double d: result = d; return true;
            case string s when double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var p):
                result = p; return true;
            default: result = 0; return false;
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _receiver.Close(); } catch { /* ignore */ }
        _receiver.Dispose();
        try { _sender?.Close(); } catch { /* ignore */ }
        _sender?.Dispose();
        _sender = null;
    }
}
