using System.Text.Json;
using OfflineReShade.Core;

internal static class MartyPrivateTests
{
    public static async Task RunAsync(string folder)
    {
        var receipt = "INFO | [MARTYSMODS LUT-Manager] Reading preset...\nINFO | [MARTYSMODS LUT-Manager] LUT specified in preset found.\n";
        AutomationEngine.VerifyMartyLutReceipt(receipt);
        foreach (var invalid in new[] { "", receipt + "INFO | [MARTYSMODS LUT-Manager] Reading preset...\n", receipt + "INFO | [MARTYSMODS LUT-Manager] Preset specifies unknown LUT, defaulting to neutral.\n" }) {
            var rejected = false; try { AutomationEngine.VerifyMartyLutReceipt(invalid); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Missing or fallback LUT receipt was accepted.");
        }
        var initial = "Techniques=\n[MartysMods_REGRADE+.fx]\nEXT_CURVES_NODE_COUNT_RGB=2\n" +
            string.Join('\n', Enumerable.Range(0, 10).Select(i => $"EXT_CURVES_NODE_COORDS_RGB{i}={(i == 1 ? 65535 : 0)}")) + "\n[Unrelated]\nKeep=42\n";
        var doc = new MartyPresetCodec(initial);
        JsonElement Curve(double[][] points) => AutomationEngine.Json(new { channel = "RGB", points });
        foreach (var points in new[] { new[] { new[] { 0d, 0 }, new[] { .0001, .5 }, new[] { 1d, 1 } }, new[] { new[] { .1, 0 }, new[] { 1d, 1 } }, new[] { new[] { 0d, 0 }, new[] { 1d, 2 } } }) {
            var rejected = false; try { MartyPresetCodec.CurvePatch(Curve(points)); } catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid curve was accepted.");
        }
        var args = Curve([[0, 0], [.5, .8], [1, 1]]);
        var patch = MartyPresetCodec.CurvePatch(args);
        var changed = new MartyPresetCodec(doc.Patch("MartysMods_REGRADE+.fx", patch));
        changed.Verify("MartysMods_REGRADE+.fx", patch);
        if (changed.Get("Unrelated", "Keep") != "42") throw new Exception("Private patch modified unrelated FX state.");

        var work = Path.Combine(folder, "marty-private"); Directory.CreateDirectory(work);
        var original = Path.Combine(work, "OfflinePreset.ini");
        var text = initial; string path = original; ulong frame = 0;
        bool drop = false, cancelDispatch = false, failRestore = false; CancellationTokenSource? cancel = null;
        Task<JsonElement> Call(string method, object? obj, CancellationToken token) {
            var a = AutomationEngine.Json(obj);
            if (method == "get_runtime_info") return Task.FromResult(AutomationEngine.Json(new { presetPath = path, loading = false, frameCount = ++frame, inputGeneration = 1 }));
            if (method == "export_preset") File.WriteAllText(a.GetProperty("path").GetString()!, text);
            if (method == "set_preset") {
                if (failRestore && a.GetProperty("path").GetString()!.EndsWith("before.ini")) throw new IOException("Simulated restore failure.");
                path = a.GetProperty("path").GetString()!; text = File.ReadAllText(path);
                if (cancelDispatch) { cancelDispatch = false; cancel!.Cancel(); return Task.FromCanceled<JsonElement>(token); }
            }
            if (method == "reload_effects" && drop) { drop = false; text = initial; }
            return Task.FromResult(AutomationEngine.Json(new { }));
        }
        var engine = new AutomationEngine(Call, (_, _) => Task.CompletedTask, () => new RenderInput(Path.Combine(folder, "test.png"), PresetPath: Path.Combine(folder, "source.ini")));
        var profile = new AutomationEngine.MartyProfile("MartysMods_REGRADE+.fx", "", "");
        var request = AutomationEngine.Json(new { id = "set_curve", arguments = args });
        await engine.ApplyMartyActionAsync(profile, request, CancellationToken.None);
        if (path != original || new MartyPresetCodec(text).Get(profile.Effect, "EXT_CURVES_NODE_COUNT_RGB") != "3") throw new Exception("Confirmed private edit lost its working preset.");
        text = initial; drop = true;
        var mismatch = false;
        try { await engine.ApplyMartyActionAsync(profile, request, CancellationToken.None); } catch (InvalidOperationException) { mismatch = true; }
        if (!mismatch || text != initial || path != original) throw new Exception("Unconfirmed private edit did not restore the original state.");
        cancel = new(); cancelDispatch = true;
        try { await engine.ApplyMartyActionAsync(profile, request, cancel.Token); } catch (OperationCanceledException) { }
        if (text != initial || path != original) throw new Exception("Cancellation at dispatch lost the private-state rollback.");
        cancel.Dispose();
        if (Directory.EnumerateDirectories(work, ".marty-edit-*").Any()) throw new Exception("Private transaction files leaked.");
        drop = true; failRestore = true;
        try { await engine.ApplyMartyActionAsync(profile, request, CancellationToken.None); } catch (AggregateException) { }
        var blocked = false;
        try { await engine.ExecuteAsync("export_image", AutomationEngine.Json(new { path = Path.Combine(work, "must-not-export.png") })); }
        catch (InvalidOperationException e) when (e.Message.Contains("restoration failed")) { blocked = true; }
        if (!blocked || !Directory.EnumerateDirectories(work, ".marty-edit-*").Any()) throw new Exception("Unrecovered private state was exportable or its recovery snapshot was lost.");
        Console.WriteLine("PASS: Marty curve validation, unrelated-state preservation, confirmed apply, mismatch rollback and dispatch cancellation");
    }
}
