using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StagePlayout.App;

/// <summary>
/// Registo de crashes: %TEMP%\stageplayout_crash.log (+ minidump em crashes fatais).
/// Nunca lança exceções — o log é a última linha de defesa num live event.
/// </summary>
public static class CrashLog
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "stageplayout_crash.log");
    private static readonly object Sync = new();
    private const long MaxBytes = 512 * 1024;

    private const uint MiniDumpNormal = 0x00000002;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, SafeFileHandle hFile, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    public static void Write(string section, Exception ex)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{section}]\n{ex}\n\n");
                RotateIfNeeded();
            }
        }
        catch
        {
            // nunca falhar por causa do log
        }
    }

    /// <summary>
    /// Escreve um minidump (%TEMP%\stageplayout_crash_*.dmp) para diagnóstico
    /// de crashes fatais (nativos incluídos) — pós-mortem com WinDbg/VS.
    /// </summary>
    public static void WriteDump(Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(),
                $"stageplayout_crash_{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var proc = Process.GetCurrentProcess();
            var ok = MiniDumpWriteDump(proc.Handle, (uint)proc.Id,
                fs.SafeFileHandle, MiniDumpNormal, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Write("Dump", new Exception($"minidump {(ok ? "OK" : "FALHOU")}: {path}"));
        }
        catch
        {
            // best-effort
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists || fi.Length <= MaxBytes) return;
            File.Delete(LogPath + ".old");
            File.Move(LogPath, LogPath + ".old");
        }
        catch
        {
            // best-effort
        }
    }
}
