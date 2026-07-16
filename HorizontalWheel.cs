using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EDCBViewer;

/// <summary>
/// WPF が未対応の水平ホイール (WM_MOUSEHWHEEL、MX Master のサムホイール等) を
/// ScrollViewer の横スクロールに変換する。Shift+縦ホイールにも対応。
/// </summary>
internal static class HorizontalWheel
{
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WM_MOUSEWHEEL  = 0x020A;
    private const int WM_HSCROLL     = 0x0114;

    // 一時診断ログ（原因特定後に削除する）
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "edcbviewer_input.log");
    private static void Log(string s)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); }
        catch { }
    }

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
                    if (msg is WM_MOUSEHWHEEL or WM_MOUSEWHEEL or WM_HSCROLL)
                        Log($"{w.GetType().Name} msg=0x{msg:X4} wParam=0x{wParam.ToInt64():X16}");

                    if (msg == WM_MOUSEHWHEEL)
                    {
                        // 上位ワードが delta（右回転で正）
                        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                        if (DoScroll(w, target, delta)) handled = true;
                    }
                    return IntPtr.Zero;
                });
        };

        // Shift+縦ホイール → 横スクロール（ドライバがこの形式で送るケースに対応）
        w.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
            Log($"{w.GetType().Name} Shift+Wheel delta={e.Delta}");
            if (DoScroll(w, target, -e.Delta)) e.Handled = true;
        };
    }

    private static bool DoScroll(Window w, ScrollViewer? target, int delta)
    {
        var sv = target ?? FindUnderMouse(w);
        if (sv == null) return false;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset + delta);
        return true;
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
