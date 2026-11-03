using OfflineReShade.Core;
using System.Globalization;
using System.Text.Json;

internal static class BatchTests
{
    private static void Check(bool value, string error) { if (!value) throw new Exception(error); }
    private static async Task WaitAsync(AutomationEngine engine) { using var timeout = new CancellationTokenSource(20000); while (engine.IsBusy) await Task.Delay(5, timeout.Token); }
    public static async Task RunAsync(string root)
    {
        var dir = Path.Combine(root, "batch"); Directory.CreateDirectory(dir);
        var effects = Path.Combine(dir, "effects"); Directory.CreateDirectory(effects);
        var shader = Path.Combine(effects, "test.fx"); await File.WriteAllTextAsync(shader, "effect1");
        var color = Path.Combine(dir, "color.png"); await File.WriteAllTextAsync(color, "color");
        var bad = Path.Combine(dir, "bad.png"); await File.WriteAllTextAsync(bad, "bad");
        var preset = Path.Combine(dir, "source.ini"); await File.WriteAllTextAsync(preset, "Gain=1\n");
        var other = Path.Combine(dir, "other.ini"); await File.WriteAllTextAsync(other, "Gain=0.7\n");
        var input = new RenderInput(color, EffectDir: effects, PresetPath: preset, OutputPath: Path.Combine(dir, "out.png"));
        var host = new Host();
        await using var session = new RenderSessionController(host, Path.Combine(dir, "snapshots"));
        await session.OpenAsync(input);
        AutomationEngine Engine() => new(session.CallAsync, session.OpenAsync, () => session.Current, session.LoadPresetAsync, session.SetInputWatchAsync,
            setPresetSource: session.SetPresetSourceAsync, renderSession: session);
        var engine = Engine();
        await engine.ExecuteAsync("set_uniform", AutomationEngine.Json(new { id = "gain", value = new[] { 0.42 } }));
        var history = engine.History;
        var draft = session.State.Draft with { RenderWidth = "still typing", RenderHeight = "" };
        session.State.Edit(draft); host.PreviewOriginal = true;
        var request = new BatchRequest([input with { PresetPath = other }, input with { ColorPath = bad, OutputPath = Path.Combine(dir, "bad-out.png") },
            input with { OutputPath = Path.Combine(dir, "last.png") }], WarmupFrames: 0, Retries: 0);
        await engine.ExecuteAsync("batch_start", AutomationEngine.Json(request)); await WaitAsync(engine);
        Check(engine.Job!.State == "failed" && engine.Job.Exported == 2 && engine.Job.Failed == 1 && engine.Job.Completed == 3, "A failed item stopped the batch or became success.");
        Check(await File.ReadAllTextAsync(input.OutputPath) == "0.7" && await File.ReadAllTextAsync(Path.Combine(dir, "last.png")) == "0.42", "Frozen per-item/live presets were mixed.");
        Check(session.Current == input && host.Gain == 0.42 && session.State.Draft == draft && host.PreviewOriginal && engine.History == history,
            "Cross-preset batch changed original unsaved FX, draft, preview or history.");
        var manifest = engine.Job.ManifestPath!;
        var outputs = host.Exports;
        File.Delete(Path.Combine(dir, ".OfflineReShade.batch.json"));
        await engine.ExecuteAsync("batch_resume", AutomationEngine.Json(new { path = manifest })); await WaitAsync(engine);
        Check(engine.Job!.Reused == 2 && host.Exports == outputs, "Resume did not retain its own verification evidence after shared cache removal.");
        await File.WriteAllTextAsync(bad, "fixed");
        // A different engine must resume from persisted settings, not the current edited preset.
        var fresh = Engine(); host.Gain = 0.88;
        await fresh.ExecuteAsync("batch_retry", AutomationEngine.Json(new { path = manifest })); await WaitAsync(fresh);
        Check(fresh.Job!.Failed == 0 && fresh.Job.Exported == 1 && fresh.Job.Reused == 2 && host.Exports == outputs + 1 && host.Gain == 0.88,
            "Retry lost prior results, used new editor settings, or failed to restore them.");
        Check(await File.ReadAllTextAsync(Path.Combine(dir, "bad-out.png")) == "0.42", "Retry used current edits instead of frozen settings.");
        await File.WriteAllTextAsync(input.OutputPath, "tampered");
        await fresh.ExecuteAsync("batch_resume", AutomationEngine.Json(new { path = manifest })); await WaitAsync(fresh);
        Check(fresh.Job!.Exported == 1 && fresh.Job.Reused == 2 && await File.ReadAllTextAsync(input.OutputPath) == "0.7", "Tampered output was reused.");
        await fresh.ExecuteAsync("batch_start", AutomationEngine.Json(new BatchRequest([input], 0, ConflictPolicy: "skip"))); await WaitAsync(fresh);
        Check(fresh.Job!.Skipped == 1 && fresh.Job.Exported == 0 && fresh.Job.Reused == 0, "Explicit skip was counted as export/reuse.");
        var exports = host.Exports;
        await fresh.ExecuteAsync("batch_start", AutomationEngine.Json(new BatchRequest([input], 0, Resume: false, Retries: 0, ConflictPolicy: "fail"))); await WaitAsync(fresh);
        Check(fresh.Job!.Failed == 1 && host.Exports == exports, "Conflict-fail rendered or replaced an existing file.");
        host.Warning = "Image is black; review if unexpected.";
        await fresh.ExecuteAsync("batch_start", AutomationEngine.Json(new BatchRequest([input], 0, Resume: false, ConflictPolicy: "rename"))); await WaitAsync(fresh);
        Check(fresh.Job!.Exported == 1 && fresh.Job.Items![0].OutputPath != input.OutputPath && fresh.Job.Items[0].Warning != null, "Rename or black warning semantics failed.");
        host.Warning = null;
        var missing = input with { ColorPath = Path.Combine(dir, "missing.png"), OutputPath = Path.Combine(dir, "missing-out.png") };
        var preflight = AutomationEngine.PreflightBatch(new([missing, input], 0));
        Check(preflight.Valid == 1 && preflight.Invalid == 1 && preflight.Items[0].Error != null, "Preflight silently dropped an invalid input.");
        var collision = AutomationEngine.PreflightBatch(new([input, input with { OutputPath = color }], 0));
        Check(collision.Invalid == 1, "Preflight failed to protect other input files.");
        var texture = Path.Combine(effects, "protected.png"); await File.WriteAllTextAsync(texture, "texture");
        await fresh.ExecuteAsync("batch_start", AutomationEngine.Json(new BatchRequest([input with { OutputPath = texture }], 0, Resume: false, Retries: 0))); await WaitAsync(fresh);
        Check(fresh.Job!.Failed == 1 && await File.ReadAllTextAsync(texture) == "texture", "Batch output overwrote a frozen effect dependency.");
        host.BeforeExport = () => File.WriteAllTextAsync(shader, "changed");
        await File.WriteAllTextAsync(input.OutputPath, "old-artwork");
        await fresh.ExecuteAsync("batch_start", AutomationEngine.Json(new BatchRequest([input], 0, Resume: false, Retries: 0))); await WaitAsync(fresh);
        Check(fresh.Job!.Failed == 1 && await File.ReadAllTextAsync(input.OutputPath) == "old-artwork", "Dependency drift published an invalid output over the previous image.");
        host.BeforeExport = null;
        var failedManifest = fresh.Job.ManifestPath!;
        await fresh.ExecuteAsync("batch_resume", AutomationEngine.Json(new { path = failedManifest })); await WaitAsync(fresh);
        Check(fresh.Job!.Failed == 1 && fresh.Job.Items![0].Error!.Contains("dependency changed"), "Resume silently adopted changed task effects.");
        host.Slow = true;
        await fresh.ExecuteAsync("batch_start", AutomationEngine.Json(new BatchRequest([input], 0, Resume: false)));
        await fresh.ExecuteAsync("batch_cancel", AutomationEngine.Json(new { })); await WaitAsync(fresh);
        Check(fresh.Job!.State == "cancelled" && session.Current == input && session.State.Draft == draft && host.Gain == 0.88,
            "Cancellation failed to restore original editor context.");
        host.Slow = false;
        using (var locked = new FileStream(other, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rejected = false;
            try { await fresh.ExecuteAsync("batch_start", AutomationEngine.Json(new BatchRequest([input with { PresetPath = other }], 0))); }
            catch (IOException) { rejected = true; }
            Check(rejected && !fresh.IsBusy && fresh.Job!.State != "running" && session.Current == input && host.Gain == 0.88,
                "Failure to freeze all presets left a phantom running task or damaged the editor.");
        }
        await session.StopAsync();
        await fresh.ExecuteAsync("batch_start", AutomationEngine.Json(new BatchRequest([input], 0, Resume: false))); await WaitAsync(fresh);
        Check(!session.IsConnected && session.State.Draft == draft, "Batch unexpectedly reopened an originally stopped editor.");
        await fresh.StopAsync(); await engine.StopAsync();
        Console.WriteLine("PASS: batch frozen presets, complete context/history restore, per-item failure, durable retry/resume, verified reuse, tampering, conflicts, black warnings, atomic output, dependency drift and cancellation.");
    }

    private sealed class Host : IRenderSessionHost
    {
        public bool IsConnected { get; private set; }
        public double Gain = 1;
        public bool EffectsEnabled = true, PreviewOriginal, Slow;
        public int Exports;
        public string? Warning;
        public Func<Task>? BeforeExport;
        private RenderInput _input = new("");
        private int _frame;
        public async Task StartAsync(RenderInput input, string? snapshot, CancellationToken token)
        {
            _input = input;
            var path = snapshot ?? input.PresetPath;
            var content = path.Length == 0 ? "" : await File.ReadAllTextAsync(path, token);
            var line = content.Split('\n').FirstOrDefault(x => x.StartsWith("Gain="));
            Gain = line == null ? 1 : double.Parse(line[5..], CultureInfo.InvariantCulture);
            EffectsEnabled = true; PreviewOriginal = false; IsConnected = true;
        }
        public Task StopAsync() { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
        public async Task<JsonElement> CallAsync(string method, object? args, CancellationToken token)
        {
            if (!IsConnected) throw new IOException("Disconnected");
            var json = AutomationEngine.Json(args ?? new { });
            if (method == "get_runtime_info") return AutomationEngine.Json(new { loading = false, effectsEnabled = EffectsEnabled, previewOriginal = PreviewOriginal, inputGeneration = 1, frameCount = ++_frame });
            if (method is "get_edit_state" or "list_state") return AutomationEngine.Json(new { uniforms = new[] { new { id = "gain", type = "float", value = new[] { Gain } } },
                techniques = Array.Empty<object>(), preprocessorDefinitions = Array.Empty<object>(), effectsEnabled = EffectsEnabled });
            if (method == "export_preset") await File.WriteAllTextAsync(json.GetProperty("path").GetString()!, "Gain=" + Gain.ToString("R", CultureInfo.InvariantCulture) + "\n", token);
            if (method == "set_uniform") Gain = json.GetProperty("value")[0].GetDouble();
            if (method == "set_effects_state") EffectsEnabled = json.GetProperty("enabled").GetBoolean();
            if (method == "set_preview_comparison") PreviewOriginal = json.GetProperty("original").GetBoolean();
            if (method == "validate_export" && await File.ReadAllTextAsync(_input.ColorPath, token) == "bad") throw new IOException("Damaged input");
            if (method is "save_screenshot" or "save_output")
            {
                if (Slow) await Task.Delay(1000, token);
                if (BeforeExport != null) await BeforeExport();
                await File.WriteAllTextAsync(json.GetProperty("path").GetString()!, Gain.ToString("R", CultureInfo.InvariantCulture), token);
                Exports++; return AutomationEngine.Json(new { warning = Warning });
            }
            return AutomationEngine.Json(new { });
        }
    }
}
