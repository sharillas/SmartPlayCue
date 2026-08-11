using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StagePlayout.App.Video;

/// <summary>
/// HwndHost que aloja o swapchain D3D11 do compositor na OutputWindow.
/// </summary>
public class CompositorHost : HwndHost
{
    public D3DCompositor? Compositor { get; private set; }

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hwnd = CreateWindowEx(0, "Static", "",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 64, 64, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        Compositor = new D3DCompositor(hwnd);
        Compositor.Start();

        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Compositor?.Dispose();
        Compositor = null;
        DestroyWindow(hwnd.Handle);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
