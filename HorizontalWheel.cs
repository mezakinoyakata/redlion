using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EDCBViewer;

/// <summary>
/// WPF が未対応の水平ホイール (WM_MOUSEHWHEEL、MX Master のサムホイール等) を
/// ScrollViewer の横スクロールに変換する。Shift+縦ホイールにも対応。
/// WH_MOUSE_LL フックを使うことで Logitech ドライバが WndProc に直接送らない場合も動作する。
/// </summary>
internal static class HorizontalWheel
{
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WH_MOUSE_LL    = 14;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;  // HIWORD = wheel delta
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private static LowLevelMouseProc? _llProc; // GC 防止
    private static IntPtr _llHook = IntPtr.Zero;
    private static readonly List<(Window W, ScrollViewer? Target)> _targets = [];

    /// <summary>マウスカーソル直下の横スクロール可能な ScrollViewer をスクロールする。</summary>
    public static void Attach(Window w) => AttachCore(w, null);

    /// <summary>常に指定の ScrollViewer をスクロールする（番組表グリッド用）。</summary>
    public static void Attach(Window w, ScrollViewer target) => AttachCore(w, target);

    private static void AttachCore(Window w, ScrollViewer? target)
    {
        w.SourceInitialized += (_, _) =>
        {
            _targets.Add((w, target));

            // WH_MOUSE_LL を初回のみインストール
            if (_llHook == IntPtr.Zero)
            {
                _llProc = LLMouseHook;
                _llHook = SetWindowsHookEx(WH_MOUSE_LL, _llProc, GetModuleHandle(null), 0);
            }
        };

        // Shift+縦ホイール → 横スクロール（WH_MOUSE_LL を補完）
        w.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
            if (DoScroll(w, target, -e.Delta)) e.Handled = true;
        };

        w.Closed += (_, _) =>
        {
            _targets.RemoveAll(t => t.W == w);
            if (_targets.Count == 0 && _llHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_llHook);
                _llHook = IntPtr.Zero;
                _llProc = null;
            }
        };
    }

    private static IntPtr LLMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_MOUSEHWHEEL)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int delta = (short)(info.mouseData >> 16);
            var hwndUnder = WindowFromPoint(info.pt);
            foreach (var (w, t) in _targets)
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwndUnder == hwnd || IsChild(hwnd, hwndUnder))
                {
                    DoScroll(w, t, delta);
                    break;
                }
            }
        }
        return CallNextHookEx(_llHook, nCode, wParam, lParam);
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
