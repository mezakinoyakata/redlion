using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EDCBViewer;

/// <summary>
/// WPF が未対応の水平ホイール (WM_MOUSEHWHEEL、MX Master のサムホイール等) を
/// ScrollViewer の横スクロールに変換する。
/// </summary>
internal static class HorizontalWheel
{
    private const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>マウスカーソル直下の横スクロール可能な ScrollViewer をスクロールする。</summary>
    public static void Attach(Window w) => AttachCore(w, null);

    /// <summary>常に指定の ScrollViewer をスクロールする（番組表グリッド用）。</summary>
    public static void Attach(Window w, ScrollViewer target) => AttachCore(w, target);

    private static void AttachCore(Window w, ScrollViewer? target)
    {
        w.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(w) is HwndSource src)
                src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WM_MOUSEHWHEEL)
                    {
                        // 上位ワードが delta（右回転で正）
                        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        var sv = target ?? FindUnderMouse(w);
                        if (sv != null)
                        {
                            sv.ScrollToHorizontalOffset(sv.HorizontalOffset + delta);
                            handled = true;
                        }
                    }
                    return IntPtr.Zero;
                });
        };
    }

    private static ScrollViewer? FindUnderMouse(Window w)
    {
        var d = w.InputHitTest(Mouse.GetPosition(w)) as DependencyObject;
        while (d != null)
        {
            if (d is ScrollViewer sv && sv.ScrollableWidth > 0) return sv;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
