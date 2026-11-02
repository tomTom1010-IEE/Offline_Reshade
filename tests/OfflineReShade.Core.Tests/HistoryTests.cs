using OfflineReShade.Core;
using System.Text.Json;

internal static class HistoryTests
{
    public static JsonElement SimpleState(int value) => AutomationEngine.Json(new { effectsEnabled = true,
        uniforms = new[] { new { id = "Test.fx::Gain", type = "int", value = new[] { value } } },
        techniques = Array.Empty<object>(), preprocessorDefinitions = Array.Empty<object>() });
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Fails<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    public static async Task RunAsync(string root)
    {
        var folder = Path.Combine(root, "history"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.png"); await File.WriteAllTextAsync(source, "image");
        var preset = Path.Combine(folder, "source.ini"); await File.WriteAllTextAsync(preset, "original");
        var nextPreset = Path.Combine(folder, "next.ini"); await File.WriteAllTextAsync(nextPreset, "next");
        var input = new RenderInput(source, EffectDir: folder, PresetPath: preset, OutputPath: Path.Combine(folder, "result.png"));
        var runtime = new Fake(); var opens = 0; var failSave = false; var failOpen = false; var invalidImage = false;
        async Task<JsonElement> Call(string method, object? args, CancellationToken token)
        {
            if (method == "get_runtime_info") return AutomationEngine.Json(new { loading = false, frameCount = ++runtime.Frame, inputGeneration = 1, effectsEnabled = runtime.Enabled,
                inputError = invalidImage ? "The old watched image became unreadable" : "" });
            if (method == "export_preset")
            {
                if (!failSave) await File.WriteAllTextAsync(AutomationEngine.Json(args).GetProperty("path").GetString()!, "[FX]\nGain=" + runtime.Gain, token);
                return AutomationEngine.Json(new { });
            }
            return await runtime.Call(method, args, token);
        }
        Task Open(RenderInput next, CancellationToken token)
        {
            if (failOpen) throw new IOException("bad input");
            if (next.PresetPath != input.PresetPath) runtime.Gain = 1;
            input = next; opens++; return Task.CompletedTask;
        }
        var engine = new AutomationEngine(Call, Open, () => input, setPresetSource: (path, _) => { input = input with { PresetPath = path }; return Task.CompletedTask; });
        Task<JsonElement> Run(string method, object? args = null, CancellationToken token = default) => engine.ExecuteAsync(method, AutomationEngine.Json(args ?? new { }), token);
        Task<JsonElement> Gain(double value, string? group = null) => Run("set_uniform", new { id = "Test.fx::Gain", value, transactionId = group });
        await Run("get_edit_history");
        Check(!engine.History.IsModified && !engine.History.CanUndo, "Initial state was dirty.");
        var transaction = (await Run("begin_edit")).GetProperty("transactionId").GetString()!;
        await Gain(.2, transaction); await Gain(.4, transaction); await Gain(.6, transaction);
        await Fails<InvalidOperationException>(() => Gain(.8));
        await Fails<InvalidOperationException>(() => Run("export_image", new { path = input.OutputPath, warmupFrames = 0 }));
        await Run("end_edit", new { transactionId = transaction }); await Run("end_edit", new { transactionId = transaction });
        await Run("undo"); Check(runtime.Gain == 1 && !engine.History.IsModified && !engine.History.CanUndo, "One drag did not undo as one entry.");
        await Run("redo"); Check(runtime.Gain == .6 && engine.History.IsModified, "Redo lost the latest drag value.");
        await Run("save_preset");
        Check(!engine.History.IsModified && File.ReadAllText(preset).Contains("Gain=0.6"), "Save did not publish and set the saved point.");
        await Gain(.3); await Run("undo"); Check(!engine.History.IsModified, "Undo to saved content remained dirty.");
        await Run("redo"); Check(engine.History.IsModified, "Redo away from saved content was clean.");
        var currentFile = File.ReadAllText(preset); failSave = true;
        await Fails<IOException>(() => Run("save_preset"));
        Check(engine.History.IsModified && File.ReadAllText(preset) == currentFile, "Failed save changed source or dirty state."); failSave = false;
        var copy = Path.Combine(folder, "copy.ini"); await Run("export_preset", new { path = copy });
        Check(engine.History.IsModified && input.PresetPath == preset, "Export-copy silently rebound source or cleared dirty state.");
        await Run("set_effects_state", new { enabled = false });
        var savedAs = Path.Combine(folder, "saved-as.ini"); await Run("save_preset_as", new { path = savedAs });
        Check(!engine.History.IsModified && input.PresetPath == savedAs && opens == 0 && File.ReadAllText(preset) == currentFile && !PresetMetadata.ReadEffectsEnabled(savedAs), "Save As reloaded state, changed original, or lost global toggle metadata.");
        await Run("set_effects_state", new { enabled = true }); await Run("undo"); Check(!runtime.Enabled && !engine.History.IsModified, "Global effects toggle was not undoable.");
        await Run("reset_uniform", new { id = "Test.fx::Gain" }); Check(runtime.Gain == 1, "Reset did not use shader default.");
        await Run("undo"); Check(runtime.Gain == .3 && !engine.History.IsModified, "Reset was not one undoable edit.");

        var beforeFailure = engine.History.Revision;
        await Fails<ArgumentException>(() => Run("set_parameters", new { updates = new[] { new { id = "Test.fx::Gain", value = .9 }, new { id = "missing", value = .2 } } }));
        Check(runtime.Gain == .3 && engine.History.Revision == beforeFailure && !engine.History.IsModified, "A failed multi-edit retained partial values or created history.");
        await Run("set_uniform", new { id = "Test.fx::Flags", value = new[] { false, true } });
        await Run("undo"); Check(runtime.Flags.SequenceEqual(new[] { true, false }), "Bool-vector undo changed components.");
        await Run("redo"); Check(runtime.Flags.SequenceEqual(new[] { false, true }), "Bool-vector redo changed components.");
        var changedRevision = engine.History.Revision;
        await Fails<InvalidOperationException>(() => Run("set_preset", new { path = nextPreset }));
        await Fails<InvalidOperationException>(() => Run("open_input", input with { PresetPath = nextPreset }));
        await Gain(.4);
        await Fails<InvalidOperationException>(() => Run("set_preset", new { path = nextPreset, unsavedChanges = "discard", expectedRevision = changedRevision }));
        Check(opens == 0, "Unsaved or stale decisions replaced the current context.");
        failSave = true;
        await Fails<IOException>(() => Run("set_preset", new { path = nextPreset, unsavedChanges = "save" })); failSave = false;
        Check(opens == 0 && engine.History.IsModified, "Failed save allowed preset replacement.");
        failOpen = true;
        await Fails<IOException>(() => Run("set_preset", new { path = nextPreset, unsavedChanges = "discard" })); failOpen = false;
        Check(engine.History.IsModified && engine.History.CanUndo, "Failed preset replacement destroyed history.");
        await Run("open_input", input with { Width = 20, Height = 20 });
        Check(engine.History.IsModified && engine.History.CanUndo, "Input-only change discarded history or saved state.");
        await Run("set_preset", new { path = nextPreset, unsavedChanges = "discard", expectedRevision = engine.History.Revision });
        await Run("get_edit_history"); Check(!engine.History.IsModified && !engine.History.CanUndo, "Successful preset load retained old history.");
        await Gain(.7); runtime.Gain = .8; await Run("get_edit_history");
        Check(engine.History.IsModified && !engine.History.CanUndo && engine.History.Warning != null, "External native FX change left unsafe undo history.");
        transaction = (await Run("begin_edit")).GetProperty("transactionId").GetString()!;
        await Gain(.2, transaction); await Gain(.8, transaction); await Run("end_edit", new { transactionId = transaction });
        Check(!engine.History.CanUndo, "Dragging back to the original value left a spurious history entry.");
        transaction = (await Run("begin_edit")).GetProperty("transactionId").GetString()!;
        await Gain(.9, transaction); await Run("cancel_operation");
        Check(engine.History.TransactionId == null && engine.History.CanUndo && !engine.IsBusy, "Cancel left an abandoned transaction owning the session.");
        await Fails<InvalidOperationException>(() => Gain(.1, transaction));
        await Run("undo"); Check(runtime.Gain == .8, "Cancelling a gesture lost its applied undo entry.");
        invalidImage = true;
        await Run("save_preset");
        Check(!engine.History.IsModified, "An invalid image blocked saving valid FX settings before recovery.");
        await Run("open_input", input with { PresetPath = savedAs });
        invalidImage = false;
        await Gain(.7);
        var closeDecision = AutomationEngine.Json(new { unsavedChanges = "discard", expectedRevision = engine.History.Revision });
        await Gain(.6); var stopped = false;
        await Fails<InvalidOperationException>(() => engine.StopSessionAsync(() => { stopped = true; return Task.CompletedTask; }, closeDecision));
        Check(!stopped && engine.History.IsModified, "A new edit between close review and stop was discarded.");
        await engine.StopAsync();

        // Rollback has its own token; cancellation and silent/native failures cannot look successful.
        var history = new EffectEditHistory(runtime.Call, _ => Task.CompletedTask);
        using var cancel = new CancellationTokenSource();
        await Fails<OperationCanceledException>(() => history.ChangeAsync("cancelled", null, _ => { runtime.Gain = .2; cancel.Cancel(); return Task.FromResult(AutomationEngine.Json(new { })); }, cancel.Token));
        Check(runtime.Gain == .6 && !history.CanUndo && !history.IsModified, "Cancelled edit was not rolled back.");
        runtime.IgnoreWrites = true;
        await Fails<IOException>(() => history.ChangeAsync("broken rollback", null, _ => { runtime.Gain = .2; throw new IOException("failed after applying"); }));
        Check(!history.Status.Available && history.IsModified && !history.CanUndo, "Unverified rollback was presented as a clean editable state.");
        await Fails<InvalidOperationException>(() => Task.Run(history.MarkSaved)); runtime.IgnoreWrites = false;
        history.ResetContext(); await history.SynchronizeAsync(); Check(!history.IsModified, "Explicit context reset did not recover unavailable history.");

        var metadata = Path.Combine(folder, "metadata.ini");
        await File.WriteAllTextAsync(metadata, "; retained comment\n[FX]\nGain=0.42\n[OfflineReShade]\nFuture=keep\nEffectsEnabled=1\nEffectsEnabled=0\n[Next]\nValue=2\n");
        await PresetMetadata.WriteEffectsEnabledAsync(metadata, true, CancellationToken.None);
        var text = File.ReadAllText(metadata);
        Check(PresetMetadata.ReadEffectsEnabled(metadata) && text.Contains("Future=keep") && text.Contains("Gain=0.42") && text.Split("EffectsEnabled=").Length == 2, "Metadata rewrite lost another field or retained duplicate global flags.");
        Console.WriteLine("PASS: shared history transactions, atomic multi-edit rollback, saved-content markers, global/bool-vector undo, reset, Save As/source safety, unsaved/stale decisions, context retention, external changes, cancellation and unavailable-state recovery.");
    }
    private sealed class Fake
    {
        public double Gain = 1;
        public bool[] Flags = [true, false];
        public bool Enabled = true, IgnoreWrites;
        public int Frame;
        public Task<JsonElement> Call(string method, object? args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (method == "get_edit_state") return Task.FromResult(AutomationEngine.Json(new { effectsEnabled = Enabled,
                uniforms = new object[] { new { id = "Test.fx::Flags", type = "bool", value = Flags }, new { id = "Test.fx::Gain", type = "float", value = new[] { Gain } } },
                techniques = Array.Empty<object>(), preprocessorDefinitions = Array.Empty<object>() }));
            var p = AutomationEngine.Json(args ?? new { });
            if (method is "set_uniform" or "reset_uniform")
            {
                var id = p.GetProperty("id").GetString();
                if (id is not ("Test.fx::Gain" or "Test.fx::Flags")) throw new ArgumentException("Unknown uniform");
                if (!IgnoreWrites)
                {
                    if (id == "Test.fx::Flags") Flags = p.GetProperty("value").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
                    else if (method == "reset_uniform") Gain = 1;
                    else { var value = p.GetProperty("value"); Gain = (value.ValueKind == JsonValueKind.Array ? value[0] : value).GetDouble(); }
                }
            }
            if (method == "set_effects_state" && !IgnoreWrites) Enabled = p.GetProperty("enabled").GetBoolean();
            return Task.FromResult(AutomationEngine.Json(new { }));
        }
    }
}
