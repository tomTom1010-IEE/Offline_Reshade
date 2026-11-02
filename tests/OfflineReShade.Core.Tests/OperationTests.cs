using OfflineReShade.Core;
using System.Text.Json;

internal static class OperationTests
{
    public static async Task RunAsync(string folder)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static async Task<Exception> Fails<T>(Func<Task> action) where T : Exception
        { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
        var color = Path.Combine(folder, "activity-source.png"); await File.WriteAllTextAsync(color, "source");
        var preset = Path.Combine(folder, "activity.ini"); await File.WriteAllTextAsync(preset, "original");
        var input = new RenderInput(color, OutputPath: Path.Combine(folder, "activity.png"), EffectDir: folder, PresetPath: preset);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var block = false; var exportPreset = true; var failReload = false; var value = 0;
        Task Open(RenderInput next, CancellationToken token) { input = next; return Task.CompletedTask; }
        async Task<JsonElement> Call(string method, object? args, CancellationToken token)
        {
            if (method == "get_runtime_info") return AutomationEngine.Json(new { frameCount = 1, inputGeneration = 1, loading = false, presetPath = preset });
            if (method == "set_uniform")
            {
                if (block) { block = false; entered.TrySetResult(); await release.Task.WaitAsync(token); }
                value = AutomationEngine.Json(args).GetProperty("value").GetInt32(); calls.Add("edit" + value);
            }
            if (method == "reload_effects" && failReload) throw new IOException("Broken reload");
            if (method is "save_output" or "save_screenshot") { calls.Add("export" + value); return AutomationEngine.Json(new { path = input.OutputPath, warning = "Output is entirely black; verify that this is intentional." }); }
            if (method == "export_preset" && exportPreset)
                await File.WriteAllTextAsync(AutomationEngine.Json(args).GetProperty("path").GetString()!, "new preset", token);
            return AutomationEngine.Json(new { });
        }
        var engine = new AutomationEngine(Call, Open, () => input);
        Task<JsonElement> Run(string method, object args) => engine.ExecuteAsync(method, AutomationEngine.Json(args));
        Check(engine.Activity.Snapshot.ShowEmpty && engine.Activity.Snapshot.StatusText == "No image opened", "Disconnected session presented as ready.");
        engine.Activity.WaitForInput(color);
        Check(engine.Activity.Snapshot.EmptyTitle == "Waiting for game output", "Waiting and invalid input were conflated.");
        engine.Activity.Observe(await Call("get_runtime_info", null, CancellationToken.None), input);
        Check(engine.Activity.Snapshot.ShowEmpty, "Polling a previous input erased the explicit waiting state.");
        engine.Activity.ClearWaiting();
        Check(!engine.Activity.Snapshot.ShowEmpty, "A valid black frame must not be treated as empty.");

        block = true;
        var first = Run("set_uniform", new { value = 1 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = Run("set_uniform", new { value = 2 });
        var export = Run("export_image", new { path = input.OutputPath, warmupFrames = 0 });
        Check(engine.IsExclusive, "Queued export did not lock further editing.");
        var rejected = await Fails<InvalidOperationException>(() => Run("set_uniform", new { value = 3 }));
        Check(((OperationOutcome)rejected.Data[nameof(OperationOutcome)]!).Status == "rejected", "Busy rejection is not structured.");
        release.TrySetResult();
        await Task.WhenAll(first, second, export);
        Check(calls.SequenceEqual(new[] { "edit1", "edit2", "export2" }), "Export overtook pending parameter changes.");
        Check(export.Result.GetProperty("operation").GetProperty("status").GetString() == "warning", "Black output was rejected or its warning was lost.");
        Check(!engine.IsBusy && engine.Activity.Snapshot.Notice?.Status == "warning", "Warning not retained after operation finished.");

        // File validation and native silent save failures may never erase an old preset.
        exportPreset = false;
        var silent = await Fails<IOException>(() => Run("export_preset", new { path = preset }));
        Check(await File.ReadAllTextAsync(preset) == "original", "Failed save destroyed the old preset.");
        Check(((OperationOutcome)silent.Data[nameof(OperationOutcome)]!).Code == "io_failure", "Preset failure lost its category.");
        exportPreset = true;
        await engine.RetryAsync();
        Check(await File.ReadAllTextAsync(preset) == "new preset" && engine.Activity.Snapshot.Notice == null, "Successful retry did not clear the matching error.");
        Check(!Directory.EnumerateFiles(folder, ".OfflineReShade-*.ini").Any(), "Temporary preset leaked.");
        failReload = true;
        await Fails<IOException>(() => Run("reload_effects", new { }));
        Check(engine.Activity.Snapshot.Notice?.Operation == "reload_effects", "Reload failure was hidden.");
        await Run("get_runtime_info", new { });
        Check(engine.Activity.Snapshot.Notice?.Operation == "reload_effects", "Polling erased the error.");
        failReload = false; await engine.RetryAsync();

        // A rejected request must not replace the task Stop awaits.
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        block = true;
        var cancelled = Run("set_uniform", new { value = 4 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = engine.StopSessionAsync(async () => { stopEntered.TrySetResult(); await stopRelease.Task; });
        await Fails<OperationCanceledException>(() => cancelled);
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Fails<InvalidOperationException>(() => Run("open_input", input));
        stopRelease.TrySetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!engine.IsBusy && !engine.Activity.Snapshot.Runtime.Connected, "Stop left a busy or connected session.");

        // The broker must carry the same outcome and accept cancellation on a second client.
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously); block = true;
        using var broker = new AutomationBroker(engine.ExecuteAsync, "operation tests");
        using var a = new RuntimeConnection(); using var b = new RuntimeConnection();
        await a.ConnectAsync(broker.Descriptor.PipeName); await b.ConnectAsync(broker.Descriptor.PipeName);
        var remote = a.CallAsync("set_uniform", new { value = 5 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var noBatch = await b.CallAsync("batch_cancel");
        Check(!noBatch.GetProperty("cancelling").GetBoolean() && !remote.IsCompleted, "Cancelling an absent batch cancelled an unrelated edit.");
        var active = await b.CallAsync("get_operation_status");
        Check(active.GetProperty("active").GetProperty("operation").GetString() == "set_uniform", "Active request unavailable through broker.");
        await b.CallAsync("cancel_operation");
        var remoteError = await Fails<InvalidOperationException>(() => remote);
        Check(((OperationOutcome)remoteError.Data[nameof(OperationOutcome)]!).Status == "cancelled", "Broker flattened cancellation into an unclassified failure.");
        await engine.StopAsync();
        await Run("set_uniform", new { value = 6 });
        Check(!engine.IsBusy && value == 6, "Cancel corrupted the next operation.");
        Console.WriteLine("PASS: shared operation queue/barrier, busy rejection, cancel/stop, atomic preset failures/retry, persistent feedback, black-frame semantics and structured broker errors.");
    }
}
