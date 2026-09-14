using System.IO;

namespace StagePlayout.App.Video;

/// <summary>Log de diagnóstico para %TEMP%\stageplayout_video.log (com rotação).</summary>
public static class VideoLog
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "stageplayout_video.log");
    private static readonly object Sync = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Write(string msg)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
                RotateIfNeeded();
            }
        }
        catch { /* nunca falhar por causa do log */ }
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
        catch { /* rotação é best-effort */ }
    }

    public static void Clear()
    {
        try { File.Delete(LogPath); } catch { }
    }
}
