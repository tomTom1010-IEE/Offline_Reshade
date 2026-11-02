namespace OfflineReShade.Core;

public readonly record struct PreviewSurfaceSize(uint Width, uint Height);
public readonly record struct PreviewLayout(double X, double Y, double Width, double Height, double PanX, double PanY);

public static class PreviewGeometry
{
    public const long MaxRenderPixels = 64L * 1024 * 1024;
    public const long MaxPreviewPixels = 16L * 1024 * 1024;

    public static PreviewSurfaceSize Surface(double width, double height, double dpi)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(dpi) || width <= 0 || height <= 0 || dpi <= 0)
            return new(1, 1);
        var w = Math.Min(width * dpi, 1e9); var h = Math.Min(height * dpi, 1e9);
        var factor = Math.Min(1.0, Math.Min(8192.0 / Math.Max(w, h), Math.Sqrt(MaxPreviewPixels / (w * h))));
        return new((uint)Math.Max(1, Math.Floor(w * factor)), (uint)Math.Max(1, Math.Floor(h * factor)));
    }

    public static PreviewLayout Layout(double panelWidth, double panelHeight, double sourceWidth, double sourceHeight, double zoom, double panX, double panY)
    {
        if (!double.IsFinite(panelWidth) || !double.IsFinite(panelHeight) || !double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight) || panelWidth <= 0 || panelHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return new(0, 0, 1, 1, 0, 0);
        zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, 1, 16) : 1;
        var fit = Math.Min(panelWidth / sourceWidth, panelHeight / sourceHeight);
        var fitWidth = sourceWidth * fit; var fitHeight = sourceHeight * fit;
        var width = fitWidth * zoom; var height = fitHeight * zoom;
        var baseX = (panelWidth - fitWidth) * .5 * zoom;
        var baseY = (panelHeight - fitHeight) * .5 * zoom;
        // Clamp the actual fitted image, not its letterboxed panel. Thin panoramas stay visible.
        var x = width <= panelWidth ? (panelWidth - width) * .5 : Math.Clamp(baseX + (double.IsFinite(panX) ? panX : 0), panelWidth - width, 0);
        var y = height <= panelHeight ? (panelHeight - height) * .5 : Math.Clamp(baseY + (double.IsFinite(panY) ? panY : 0), panelHeight - height, 0);
        return new(x, y, width, height, x - baseX, y - baseY);
    }
}

public sealed class DeviceRecoveryBudget
{
    private readonly Queue<DateTimeOffset> _attempts = new();
    public bool TryAcquire(DateTimeOffset now)
    {
        while (_attempts.TryPeek(out var first) && now - first >= TimeSpan.FromSeconds(30)) _attempts.Dequeue();
        if (_attempts.Count >= 3) return false;
        _attempts.Enqueue(now); return true;
    }
}
