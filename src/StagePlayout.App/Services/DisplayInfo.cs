using System.Runtime.InteropServices;

namespace StagePlayout.App.Services;

/// <summary>
/// Deteção do modo da saída de vídeo: resolução, refresh e interlaçado/progressivo
/// (via EnumDisplaySettings + DM_INTERLACED).
/// </summary>
public static class DisplayInfo
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DM_INTERLACED = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode,
        IntPtr hwnd, int dwflags, IntPtr lParam);

    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;

    /// <summary>Ex.: "1920×1080i50" (interlaçado) ou "2560×1440p60" (progressivo).</summary>
    public static string Describe(string? deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
            return "—";

        var interlaced = (dm.dmDisplayFlags & DM_INTERLACED) != 0;
        return $"{dm.dmPelsWidth}×{dm.dmPelsHeight}{(interlaced ? "i" : "p")}{dm.dmDisplayFrequency}";
    }

    /// <summary>
    /// Muda temporariamente o refresh do ecrã de saída (mantém a resolução atual).
    /// Devolve o refresh original (para restauro com RestoreRefreshRate).
    /// </summary>
    public static int TrySetRefreshRate(string deviceName, int refreshHz, out string? error)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
        {
            error = "EnumDisplaySettings falhou";
            return 0;
        }

        var original = dm.dmDisplayFrequency;
        if (original == refreshHz)
        {
            error = null;
            return 0; // já está no modo pedido
        }

        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        dm.dmDisplayFrequency = refreshHz;

        var result = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
        error = result == 0 ? null : $"ChangeDisplaySettingsEx = {result}";
        return original;
    }

    /// <summary>Restaura o refresh original (mudança temporária).</summary>
    public static string? RestoreRefreshRate(string deviceName, int refreshHz)
    {
        if (refreshHz <= 0) return null;

        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
            return "EnumDisplaySettings falhou";

        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        dm.dmDisplayFrequency = refreshHz;

        var result = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
        return result == 0 ? null : $"ChangeDisplaySettingsEx = {result}";
    }
}
