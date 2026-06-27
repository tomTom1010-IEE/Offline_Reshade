using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace OfflineReShade.WinUI.Services;

public sealed class PreviewHostService : IDisposable
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly IntPtr _parentHwnd;
    private readonly FrameworkElement _anchor;
    private IntPtr _childHwnd;

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

        _childHwnd = CreateWindowEx(0, "STATIC", string.Empty, WS_CHILD | WS_VISIBLE, 0, 0, 1, 1, _parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_childHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not create preview child HWND.");

        UpdateBounds();
        return _childHwnd;
    }

    public void UpdateBounds()
    {
        if (_childHwnd == IntPtr.Zero || _anchor.XamlRoot == null)
            return;

        var point = _anchor.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = _anchor.XamlRoot.RasterizationScale;
        var x = (int)Math.Round(point.X * scale);
        var y = (int)Math.Round(point.Y * scale);
        var width = Math.Max(1, (int)Math.Round(_anchor.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Round(_anchor.ActualHeight * scale));
        SetWindowPos(_childHwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_SHOWWINDOW);
    }

    public void Dispose()
    {
        if (_childHwnd != IntPtr.Zero)
        {
            DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);
}
