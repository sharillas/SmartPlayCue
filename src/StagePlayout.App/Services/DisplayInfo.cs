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

    /// <summary>Ex.: "1920×1080i50" (interlaçado) ou "2560×1440p60" (progressivo).</summary>
    public static string Describe(string? deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
            return "—";

        var interlaced = (dm.dmDisplayFlags & DM_INTERLACED) != 0;
        return $"{dm.dmPelsWidth}×{dm.dmPelsHeight}{(interlaced ? "i" : "p")}{dm.dmDisplayFrequency}";
    }
}
