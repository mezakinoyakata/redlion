using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EDCBViewer;

/// <summary>タイトルバーを Windows のダークモード描画にする。</summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void Apply(Window w)
    {
        if (new WindowInteropHelper(w).Handle is var h && h != IntPtr.Zero)
            Set(h);
        else
            w.SourceInitialized += (_, _) =>
                Set(new WindowInteropHelper(w).Handle);
    }

    private static void Set(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }
}
