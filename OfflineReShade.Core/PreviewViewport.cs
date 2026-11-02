namespace OfflineReShade.Core;

// Presentation state only. Pixel scale means physical display pixels per rendered image pixel.
public sealed class PreviewViewport
{
    public bool IsFit { get; private set; } = true;
    public double PixelScale { get; private set; } = 1;
    public double PanX { get; private set; }
    public double PanY { get; private set; }
    public void Fit() { IsFit = true; PanX = PanY = 0; }
    public void ActualPixels() { IsFit = false; PixelScale = 1; PanX = PanY = 0; }
    public void Pan(double x, double y) { PanX += x; PanY += y; }
    public double EffectiveScale(double w, double h, double sourceW, double sourceH, double dpi) =>
        IsFit ? FitScale(w, h, sourceW, sourceH) * ValidDpi(dpi) : PixelScale;
    public PreviewLayout Layout(double w, double h, double sourceW, double sourceH, double dpi)
    {
        var fit = FitScale(w, h, sourceW, sourceH);
        var relative = IsFit ? 1 : PixelScale / ValidDpi(dpi) / fit;
        var result = PreviewGeometry.Layout(w, h, sourceW, sourceH, relative, PanX, PanY);
        PanX = result.PanX; PanY = result.PanY;
        return result;
    }
    public void ZoomAt(double factor, double x, double y, double w, double h, double sourceW, double sourceH, double dpi)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        var old = Layout(w, h, sourceW, sourceH, dpi);
        var oldScale = EffectiveScale(w, h, sourceW, sourceH, dpi);
        PixelScale = Math.Clamp(oldScale * factor, 1.0 / 256, 16);
        IsFit = false;
        var relative = PixelScale / ValidDpi(dpi) / FitScale(w, h, sourceW, sourceH);
        var fit = PreviewGeometry.Layout(w, h, sourceW, sourceH, 1, 0, 0);
        var ratio = PixelScale / oldScale;
        PanX = x - (x - old.X) * ratio - (w - fit.Width) * .5 * relative;
        PanY = y - (y - old.Y) * ratio - (h - fit.Height) * .5 * relative;
        Layout(w, h, sourceW, sourceH, dpi);
    }
    private static double ValidDpi(double dpi) => double.IsFinite(dpi) && dpi > 0 ? dpi : 1;
    private static double FitScale(double w, double h, double sw, double sh) =>
        w > 0 && h > 0 && sw > 0 && sh > 0 && double.IsFinite(w + h + sw + sh) ? Math.Min(w / sw, h / sh) : 1;
}

public readonly record struct WorkspaceLayout(bool EffectsInline, bool GalleryInline, double EffectsWidth)
{
    public static WorkspaceLayout Arrange(double width, double preferredEffectsWidth, bool galleryOpen)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        var preferred = double.IsFinite(preferredEffectsWidth) ? Math.Clamp(preferredEffectsWidth, 280, 640) : 400;
        var galleryInline = galleryOpen && width >= preferred + 320 + 280;
        var available = Math.Max(0, width - (galleryInline ? 280 : 0));
        return new(available >= preferred + 320, galleryInline, Math.Max(1, Math.Min(preferred, available - 48)));
    }
}
