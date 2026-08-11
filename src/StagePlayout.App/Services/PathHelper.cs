using System.Runtime.InteropServices;
using System.Text;

namespace StagePlayout.App.Services;

public static class PathHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLongPathName(string lpszShortPath, StringBuilder lpszLongPath, int cchBuffer);

    /// <summary>
    /// Expande short paths 8.3 (ex.: "16435_~1.MP4") para o nome longo real do ficheiro.
    /// Necessário porque drag&amp;drop de algumas apps entrega paths curtos.
    /// </summary>
    public static string ToLongPath(string path)
    {
        try
        {
            var sb = new StringBuilder(1024);
            var len = GetLongPathName(path, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : path;
        }
        catch
        {
            return path;
        }
    }
}
