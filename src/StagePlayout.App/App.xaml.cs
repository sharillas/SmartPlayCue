using System.Runtime.InteropServices;
using System.Windows;

namespace StagePlayout.App;

public partial class App : Application
{
    // Timer multimédia a 1ms — essencial para o pacing de frames do decoder
    // (sem isto, Thread.Sleep(1) pode dormir até ~15ms -> judder em motion graphics)
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uMilliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uMilliseconds);

    protected override void OnStartup(StartupEventArgs e)
    {
        timeBeginPeriod(1);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        timeEndPeriod(1);
        base.OnExit(e);
    }
}
