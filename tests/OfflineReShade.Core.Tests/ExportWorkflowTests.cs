using OfflineReShade.Core;
using System.Text.Json;

internal static class ExportWorkflowTests
{
    public static async Task RunAsync(string root)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static async Task Fails<T>(Func<Task> action) where T : Exception
        { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        var folder = Path.Combine(root, "export-workflow"); Directory.CreateDirectory(folder);
        var color = Path.Combine(folder, "color.png"); await File.WriteAllTextAsync(color, "source");
        var output = Path.Combine(folder, "result.png");
        var current = new RenderInput(color, OutputPath: output, EffectDir: folder);
        RenderInput? draft = null;
        var invalidDraft = false;
        var value = 0; var exports = 0; var notices = 0; var failWrite = false; var silentWrite = false; var failOpen = false;
        var calls = new List<string>();
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockOpen = false;
        async Task Open(RenderInput input, CancellationToken token)
        {
            if (blockOpen) { openEntered.TrySetResult(); await openRelease.Task.WaitAsync(token); }
            if (failOpen) throw new IOException("Invalid input");
            current = input; draft = null;
        }
        async Task<JsonElement> Call(string method, object? args, CancellationToken token)
        {
            if (method == "get_runtime_info") return AutomationEngine.Json(new { loading = false, frameCount = 1, inputGeneration = 1 });
            if (method == "get_edit_state") return HistoryTests.SimpleState(value);
            if (method == "set_uniform")
            {
                var component = AutomationEngine.Json(args).GetProperty("value");
                var requested = (component.ValueKind == JsonValueKind.Array ? component[0] : component).GetInt32();
                if (requested < 0) throw new ArgumentException("Parameter rejected");
                value = requested;
                calls.Add("edit" + value);
            }
            if (method is "save_screenshot" or "save_output")
            {
                exports++; calls.Add(method + value);
                var path = AutomationEngine.Json(args).GetProperty("path").GetString()!;
                if (!silentWrite) await File.WriteAllTextAsync(path, "pixels" + value, token);
                if (failWrite) throw new IOException("Damaged effect");
                return AutomationEngine.Json(new { path, warning = "Black output is allowed" });
            }
            return AutomationEngine.Json(new { });
        }
        using var edits = new DeferredEditQueue(_ => notices++, 60000);
        var engine = new AutomationEngine(Call, Open, () => current, submitPendingEdits: edits.FlushAsync,
            pendingInput: () => invalidDraft ? throw new ArgumentException("Invalid draft dimensions") : draft);
        Task<JsonElement> Run(string method, object args) => engine.ExecuteAsync(method, AutomationEngine.Json(args));
        void Edit(int next) => edits.Enqueue("gain", async () => await engine.ExecuteSubmittedEditAsync("set_uniform", AutomationEngine.Json(new { value = next })));
        ImageExportRequest Request(string policy = "replace", bool screenshot = true, RenderInput? apply = null, RenderInput? expected = null)
            => new(output, 0, screenshot, policy, apply, expected);

        // The final UI value has not entered the operation queue yet: export must submit it.
        Edit(1); Edit(2);
        await Run("export_image", Request()).WaitAsync(TimeSpan.FromSeconds(3));
        Check(calls.SequenceEqual(new[] { "edit2", "save_screenshot2" }) && await File.ReadAllTextAsync(output) == "pixels2",
            "Export ignored the debounced final edit, submitted duplicates, or changed the default capture method.");
        Edit(-1);
        await Fails<ArgumentException>(() => Run("export_image", Request()));
        Check(exports == 1 && notices == 1 && await File.ReadAllTextAsync(output) == "pixels2", "Failed parameter submission exported stale values or lost its error.");
        Edit(3); await Run("export_image", Request(screenshot: false));
        Check(calls.Last() == "save_output3", "Direct PNG was silently replaced by screenshot capture.");
        Edit(6);
        await edits.SubmitNowAsync("gain", () => engine.ExecuteSubmittedEditAsync("set_uniform", AutomationEngine.Json(new { value = 4 })));
        Check(value == 4 && !calls.Contains("edit6"), "An older debounced slider value overwrote a newer immediate text edit.");
        Edit(5); await Run("set_uniform", new { value = 3 });
        Check(value == 3 && calls.TakeLast(2).SequenceEqual(new[] { "edit5", "edit3" }), "A delayed UI edit overtook a newer MCP edit.");

        var original = current;
        draft = current with { Width = 128, Height = 96 };
        var before = exports;
        await Fails<InvalidOperationException>(() => Run("export_image", Request()));
        Check(exports == before && current == original, "Pending configuration silently exported the applied input.");
        invalidDraft = true;
        var status = await Run("get_operation_status", new { });
        Check(status.GetProperty("pendingConfiguration").GetProperty("error").GetString() == "Invalid draft dimensions", "Invalid draft was hidden from MCP status.");
        await Fails<ArgumentException>(() => Run("export_image", Request())); invalidDraft = false;
        failOpen = true;
        await Fails<IOException>(() => Run("export_image", Request(apply: draft, expected: original)));
        Check(current == original && exports == before && await File.ReadAllTextAsync(output) == "pixels3", "Failed apply exported the old image or replaced the previous output.");
        failOpen = false; blockOpen = true;
        var applying = Run("export_image", Request(apply: draft, expected: original));
        await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Fails<InvalidOperationException>(() => Run("set_uniform", new { value = 9 }));
        openRelease.TrySetResult(); await applying; blockOpen = false;
        Check(current.Width == 128 && exports == before + 1, "Apply/export did not complete under one operation lock.");
        await Fails<InvalidOperationException>(() => Run("export_image", Request(expected: original)));

        before = exports;
        await Fails<IOException>(() => Run("export_image", Request("fail")));
        Check(exports == before, "A known file conflict still rendered output.");
        var renamed = await Run("export_image", Request("rename"));
        var actualPath = renamed.GetProperty("path").GetString()!;
        Check(actualPath == Path.Combine(folder, "result (1).png") && File.Exists(actualPath) && await File.ReadAllTextAsync(output) == "pixels3",
            "Rename policy overwrote the original or returned a temporary path.");
        Check(renamed.GetProperty("operation").GetProperty("status").GetString() == "warning", "Publishing lost the black output warning.");
        Check(renamed.GetProperty("operation").GetProperty("targetPath").GetString() == actualPath, "Warning/locate feedback points to the original file instead of the numbered export.");
        failWrite = true;
        await Fails<IOException>(() => Run("export_image", Request())); failWrite = false;
        silentWrite = true;
        await Fails<IOException>(() => Run("export_image", Request())); silentWrite = false;
        Check(await File.ReadAllTextAsync(output) == "pixels3" && !Directory.EnumerateFiles(folder, ".OfflineReShade-export-*").Any(), "Failed exports damaged the prior output or leaked staging files.");
        await Fails<ArgumentException>(() => Run("export_image", Request() with { Path = color }));

        // A file created by another process during rendering must not be overwritten by fail/rename.
        var racing = Path.Combine(folder, "race.png");
        async Task<JsonElement> Race(string temporary, CancellationToken token)
        {
            await File.WriteAllTextAsync(racing, "other process", token);
            await File.WriteAllTextAsync(temporary, "new render", token);
            return AutomationEngine.Json(new { path = temporary });
        }
        await Fails<IOException>(() => ImageExport.WriteAsync(racing, "fail", Race, CancellationToken.None));
        Check(await File.ReadAllTextAsync(racing) == "other process", "Fail policy overwrote a file created during rendering.");
        File.Delete(racing);
        var raced = await ImageExport.WriteAsync(racing, "rename", Race, CancellationToken.None);
        Check(await File.ReadAllTextAsync(racing) == "other process" && raced.GetProperty("path").GetString() != racing, "Rename policy overwrote a racing file.");
        using var cancel = new CancellationTokenSource();
        await Fails<OperationCanceledException>(() => ImageExport.WriteAsync(output, "replace", async (temporary, token) => {
            await File.WriteAllTextAsync(temporary, "cancelled", token); cancel.Cancel(); return AutomationEngine.Json(new { });
        }, cancel.Token));
        Check(await File.ReadAllTextAsync(output) == "pixels3", "Cancellation after rendering still replaced the output.");
        await engine.StopAsync();
        Console.WriteLine("PASS: final debounced edit barrier/failures, pending configuration, atomic apply/export, source binding, both explicit capture paths, conflict races, cancellation and prior-output preservation.");
    }
}
