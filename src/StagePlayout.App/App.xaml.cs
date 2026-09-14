using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using StagePlayout.App.Video;

namespace StagePlayout.App;

public partial class App : Application
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uMilliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uMilliseconds);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    public struct MARGINS { public int left, right, top, bottom; }

    private bool _handlingCrash;

    /// <summary>Projeto a carregar no arranque (argumento .stageplayout.json).</summary>
    public static string? StartupProject { get; private set; }

    /// <summary>Abrir a janela de output automaticamente (flag --output).</summary>
    public static bool StartupOpenOutput { get; private set; }

    /// <summary>Tocar o 1.º cue automaticamente (flag --autoplay).</summary>
    public static bool StartupAutoPlay { get; private set; }

    /// <summary>Modo de verificação pré-show (flag --selftest): toca tudo e sai com exit code.</summary>
    public static bool StartupSelfTest { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        timeBeginPeriod(1);

        // arranque desatendido: SmartPlayCue.exe show.stageplayout.json --output --autoplay
        var projArg = e.Args.FirstOrDefault(a =>
            a.EndsWith(".stageplayout.json", StringComparison.OrdinalIgnoreCase));
        if (projArg is not null && File.Exists(projArg))
            StartupProject = Path.GetFullPath(projArg);

        StartupOpenOutput = e.Args.Any(a => a.Equals("--output", StringComparison.OrdinalIgnoreCase));
        StartupAutoPlay = e.Args.Any(a => a.Equals("--autoplay", StringComparison.OrdinalIgnoreCase));
        StartupSelfTest = e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

        // Em live events NADA pode morrer silenciosamente:
        // - UI thread: log + diálogo (continuar mantém o show no ar)
        // - threads de background / tasks: log apenas (o motor de vídeo continua)
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += App_UnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += App_UnobservedTaskException;

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        timeEndPeriod(1);
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        CrashLog.Write("UI thread", ex);
        VideoLog.Write($"EXCEPTION UI: {ex.GetType().Name}: {ex.Message}");

        // nunca deixar o WPF matar a app por defeito
        e.Handled = true;

        if (_handlingCrash) return; // exceção durante o tratamento — não reentrar
        _handlingCrash = true;
        try
        {
            var result = MessageBox.Show(
                "Erro inesperado:\n\n" +
                $"{ex.Message}\n\n" +
                "Continuar mantém o show no ar (detalhes em %TEMP%\\stageplayout_crash.log).\n" +
                "Fechar termina a aplicação.",
                "Smart Play Cue — erro", MessageBoxButton.OKCancel, MessageBoxImage.Error);
            if (result != MessageBoxResult.OK)
                Shutdown(-1);
        }
        finally
        {
            _handlingCrash = false;
        }
    }

    private void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            CrashLog.WriteDump(ex); // fatal: minidump para pós-mortem
            CrashLog.Write("AppDomain", ex);
            VideoLog.Write($"EXCEPTION AppDomain: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void App_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write("Task", e.Exception);
        VideoLog.Write($"EXCEPTION Task: {e.Exception.GetType().Name}: {e.Exception.Message}");
        e.SetObserved();
    }

    public static void ApplyDarkMode(Window window)
    {
        window.Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int useDark = 1;
            DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
            DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
        };
    }
}
