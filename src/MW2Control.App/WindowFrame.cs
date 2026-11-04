using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MW2Control.App;

internal static class WindowFrame
{
    internal static int HideSystemBorder(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return 0;
        var handle = new WindowInteropHelper(window).Handle;
        var dark = 1u;
        var border = 0xFFFFFFFEu; // DWMWA_COLOR_NONE; the WPF surface draws its own edge.
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(uint));
        return DwmSetWindowAttribute(handle, 34, ref border, sizeof(uint));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref uint value, int size);
}
