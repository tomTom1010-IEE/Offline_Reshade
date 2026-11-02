using OfflineReShade.Core;

static class PreviewTests
{
    public static async Task RunAsync()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2, 3, 4 })
        foreach (var source in new[] { (1, 1), (1920, 1080), (4096, 2160), (16384, 1), (1, 16384) })
        {
            var view = new PreviewViewport();
            var fit = view.Layout(640, 400, source.Item1, source.Item2, dpi);
            Check(fit.Width <= 640.001 && fit.Height <= 400.001, "Fit overflows canvas.");
            view.ActualPixels();
            var actual = view.Layout(640, 400, source.Item1, source.Item2, dpi);
            Check(Math.Abs(actual.Width * dpi - source.Item1) < .001 && Math.Abs(actual.Height * dpi - source.Item2) < .001, "100% does not map one render pixel to one display pixel.");
            view.Pan(1e10, -1e10);
            actual = view.Layout(320, 240, source.Item1, source.Item2, dpi);
            Check(actual.X + actual.Width > 0 && actual.Y + actual.Height > 0 && actual.X < 320 && actual.Y < 240, "Pan loses the image after resize.");
            for (var i = 0; i < 30; i++) view.ZoomAt(1 / 1.15, 160, 120, 320, 240, source.Item1, source.Item2, dpi);
            var zoomed = view.Layout(320, 240, source.Item1, source.Item2, dpi);
            Check(double.IsFinite(zoomed.Width + zoomed.Height + zoomed.X + zoomed.Y), "Zoom produced invalid geometry.");
            view.Fit(); Check(view.IsFit && view.Layout(320, 240, source.Item1, source.Item2, dpi).Width <= 320.001, "Fit reset failed.");
        }
        var anchored = new PreviewViewport(); anchored.ActualPixels();
        var before = anchored.Layout(800, 600, 4000, 3000, 1.5);
        anchored.ZoomAt(1.15, 300, 200, 800, 600, 4000, 3000, 1.5);
        var after = anchored.Layout(800, 600, 4000, 3000, 1.5);
        Check(Math.Abs((300 - before.X) / before.Width - (300 - after.X) / after.Width) < 1e-9, "Pointer-anchored zoom shifted image content.");
        foreach (var width in new[] { 280.0, 640, 900, 960, 1280, 1920 })
        foreach (var panel in new[] { 280.0, 400, 640, double.NaN })
        {
            var layout = WorkspaceLayout.Arrange(width, panel, true);
            Check(layout.EffectsWidth > 0 && layout.EffectsWidth <= width - 48, "Overlay panel hides its dismiss margin.");
            if (layout.EffectsInline) Check(width - layout.EffectsWidth - (layout.GalleryInline ? 280 : 0) >= 320, "Inline panels consume canvas minimum width.");
        }
        var defaults = new WorkspaceConfiguration();
        var legacy = WorkspaceConfigurationCodec.Read("{\"colorPath\":\"retained.png\"}", defaults);
        Check(legacy.Editor.ColorPath == "retained.png" && legacy.Preferences.EffectsPaneOpen && legacy.Preferences.Theme == "System", "Old preferences lost defaults or existing paths.");
        var configured = defaults with { Preferences = defaults.Preferences with { EffectsPaneWidth = 560, EffectsPaneOpen = false, Theme = "Dark" } };
        Check(WorkspaceConfigurationCodec.Read(WorkspaceConfigurationCodec.Write(configured), defaults) == configured, "Layout preferences do not roundtrip.");
        var submitted = false;
        var input = new RenderInput("unchanged.png", Width: 4000, Height: 3000);
        var engine = new AutomationEngine((method, args, _) => Task.FromResult(AutomationEngine.Json(new { previewOriginal = true })),
            (_, _) => throw new Exception("Display action reopened input."), () => input,
            submitPendingEdits: () => { submitted = true; throw new Exception("Display action committed unfinished edits."); });
        var history = engine.History;
        await engine.ExecuteAsync("set_preview_comparison", AutomationEngine.Json(new { original = true }));
        Check(!submitted && engine.History == history && !engine.IsBusy && input.Width == 4000, "Comparison changed edit state or output.");
        await engine.StopAsync();
        Console.WriteLine("PASS: render-pixel zoom at six DPI scales, tiny/panoramic inputs, pointer anchor, pan/resize, adaptive panes, preference migration and preview-only admission.");
    }
}
