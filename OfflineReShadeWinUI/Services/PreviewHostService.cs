using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace OfflineReShade.WinUI.Services;

public sealed class PreviewHostService : IDisposable
{
    private const string PreviewHostClassName = "OfflineReShadeWinUIPreviewHost";
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    private readonly IntPtr _parentHwnd;
    private readonly FrameworkElement _anchor;
    private IntPtr _childHwnd;
    private bool _isVisible = true;
    private static bool _windowClassRegistered;
    private static readonly WndProc PreviewHostWndProc = (hwnd, message, wparam, lparam) =>
        DefWindowProc(hwnd, message, wparam, lparam);

    public PreviewHostService(IntPtr parentHwnd, FrameworkElement anchor)
    {
        _parentHwnd = parentHwnd;
        _anchor = anchor;
        _anchor.SizeChanged += (_, _) => UpdateBounds();
        _anchor.LayoutUpdated += (_, _) => UpdateBounds();
    }

    public IntPtr EnsureHandle()
    {
        if (_childHwnd != IntPtr.Zero)
            return _childHwnd;

        EnsureWindowClass();
        _childHwnd = CreateWindowEx(0, PreviewHostClassName, string.Empty, WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN, 0, 0, 1, 1, _parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_childHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not create preview child HWND.");

        UpdateBounds();
        return _childHwnd;
    }

    public void SetVisible(bool visible)
    {
        _isVisible = visible;
        if (_childHwnd == IntPtr.Zero)
            return;

        if (visible)
            UpdateBounds();
        else
            SetWindowPos(_childHwnd, HWND_TOP, 0, 0, 1, 1, SWP_HIDEWINDOW | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void UpdateBounds()
    {
        if (_childHwnd == IntPtr.Zero || _anchor.XamlRoot == null)
            return;

        if (!_isVisible || _anchor.Visibility != Visibility.Visible)
        {
            SetWindowPos(_childHwnd, HWND_TOP, 0, 0, 1, 1, SWP_HIDEWINDOW | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            return;
        }

        var point = _anchor.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = _anchor.XamlRoot.RasterizationScale;
        var x = (int)Math.Round(point.X * scale);
        var y = (int)Math.Round(point.Y * scale);
        var width = Math.Max(1, (int)Math.Round(_anchor.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Round(_anchor.ActualHeight * scale));
        SetWindowPos(_childHwnd, HWND_TOP, x, y, width, height, SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void Dispose()
    {
        if (_childHwnd != IntPtr.Zero)
        {
            DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }
    }

    private static void EnsureWindowClass()
    {
        if (_windowClassRegistered)
            return;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(PreviewHostWndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = PreviewHostClassName
        };

        var atom = RegisterClassEx(ref wc);
        if (atom == 0 && Marshal.GetLastWin32Error() != 1410)
            throw new InvalidOperationException("Could not register preview host window class.");

        _windowClassRegistered = true;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }
}
