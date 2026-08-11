using System.Net;
using Rug.Osc;

namespace StagePlayout.App.Services;

/// <summary>
/// Controlo remoto via OSC (UDP) — compatível com Bitfocus Companion / Stream Deck.
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
///   /stageplayout/layer/1/show    → mostrar camada 1 (layer flutuante)
///   /stageplayout/layer/1/hide    → ocultar camada 1
///   /stageplayout/layer/1/toggle  → alternar camada 1
///   /stageplayout/layer/2/...     → idem para a camada 2
///   /stageplayout/mute [0|1]      → mute master (sem arg = toggle)
///   /stageplayout/mute/toggle     → toggle mute master
///   /stageplayout/layer/1/mute [0|1]    → mute layer 1 (1 = muda)
///   /stageplayout/layer/1/mute/toggle   → toggle mute layer 1
/// </summary>
public sealed class CompanionControl : IDisposable
{
    public const int DefaultPort = 8010;

    private readonly OscReceiver _receiver;
    private Thread? _thread;
    private volatile bool _running;

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

    public int Port { get; }

    public CompanionControl(int port = DefaultPort)
    {
        Port = port;
        _receiver = new OscReceiver(IPAddress.Any, port);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(ListenLoop) { IsBackground = true, Name = "CompanionOSC" };
        _thread.Start();
    }

    private void ListenLoop()
    {
        try
        {
            _receiver.Connect();
            while (_running)
            {
                var packet = _receiver.Receive(); // bloqueante
                switch (packet)
                {
                    case OscMessage msg:
                        Dispatch(msg);
                        break;
                    case OscBundle bundle:
                        foreach (var p in bundle)
                            if (p is OscMessage m)
                                Dispatch(m);
                        break;
                }
            }
        }
        catch
        {
            // socket fechado no Dispose — saída normal
        }
    }

    private void Dispatch(OscMessage msg)
    {
        var address = msg.Address.ToLowerInvariant().TrimEnd('/');
        var args = msg.ToArray();

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

    private static bool ToInt(object value, out int result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case float f: result = (int)f; return true;
            case string s when int.TryParse(s, out var p): result = p; return true;
            default: result = 0; return false;
        }
    }

    private static bool ToDouble(object value, out double result)
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
    }
}
