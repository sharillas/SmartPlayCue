using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace StagePlayout.App.Video;

/// <summary>
/// Carregamento das DLLs nativas do FFmpeg (pasta .\FFmpeg).
/// O loader do .NET 8 NÃO honra SetDllDirectory — é preciso AddDllDirectory
/// + um DllImportResolver com paths absolutos (à prova de bala).
/// </summary>
public static class FFmpegNatives
{
    private static int _done;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;

        var dir = Path.Combine(AppContext.BaseDirectory, "FFmpeg");
        if (Directory.Exists(dir))
            AddDllDirectory(dir);

        NativeLibrary.SetDllImportResolver(
            typeof(Flyleaf.FFmpeg.Raw).Assembly, ResolveImport);
    }

    private static IntPtr ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "FFmpeg");
        string? candidate = Path.Combine(dir, libraryName + ".dll");

        // os DllImports vêm sem versão ("avformat") mas os ficheiros têm ("avformat-62.dll")
        if (!File.Exists(candidate) && Directory.Exists(dir))
            candidate = Directory.GetFiles(dir, libraryName + "-*.dll").FirstOrDefault();

        return candidate is not null && File.Exists(candidate)
            ? NativeLibrary.Load(candidate)
            : IntPtr.Zero; // zero -> fallback à resolução default
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
