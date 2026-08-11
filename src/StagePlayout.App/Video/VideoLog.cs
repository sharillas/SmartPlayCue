using System.IO;

namespace StagePlayout.App.Video;

/// <summary>Log de diagnóstico para %TEMP%\stageplayout_video.log.</summary>
public static class VideoLog
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "stageplayout_video.log");
    private static readonly object Sync = new();

    public static void Write(string msg)
    {
        try
        {
            lock (Sync)
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { /* nunca falhar por causa do log */ }
    }

    public static void Clear()
    {
        try { File.Delete(LogPath); } catch { }
    }
}
