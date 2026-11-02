using OfflineReShade.Core;
using System.Text.Json;

internal static class InputWorkflowTests
{
    public static async Task RunAsync(string root)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static async Task Wait(Func<bool> predicate)
        { using var deadline = new CancellationTokenSource(7000); while (!predicate()) await Task.Delay(25, deadline.Token); }
        static async Task Fails<T>(Func<Task> action) where T : Exception
        { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        var folder = Path.Combine(root, "input-workflow"); Directory.CreateDirectory(folder);
        var color = Path.Combine(folder, "frame-Color.png"); await File.WriteAllTextAsync(color, "good");
        var raw = Path.Combine(folder, "frame-Depth.rfloat"); await File.WriteAllTextAsync(raw, "depth");
        var rgba = Path.Combine(folder, "frame-Depth.PNG"); await File.WriteAllTextAsync(rgba, "depth");
        Check(InputPairing.Resolve(color, "kks").IsAmbiguous, "Multiple depth formats were silently chosen.");
        Check(InputPairing.Resolve(color, "kk").AutomaticDepth?.Path == raw, "KK pairing selected an unsupported packed depth.");
        var video = Path.Combine(folder, "0001.png"); await File.WriteAllTextAsync(video, "good");
        Check(InputPairing.Resolve(video, "kks").AutomaticDepth == null, "Color-only image acquired an unrelated depth.");
        await File.WriteAllTextAsync(Path.Combine(folder, "0001.depth.rfloat"), "depth");
        Check(InputPairing.Resolve(video, "kks").AutomaticDepth?.Format == "raw", "Video frame pairing regressed.");
        Check(!InputPairing.IsColorFile(rgba) && !InputPairing.IsColorFile(Path.Combine(folder, "0001.reshade.png")), "Gallery included generated/depth images as color inputs.");
        var game = Path.Combine(folder, "coloroutput.png");
        var gameDepth = Path.Combine(folder, "depthoutput.rfloat");
        var input = new RenderInput(color, OutputPath: Path.Combine(folder, "result.png"), EffectDir: folder);
        var original = input;
        var portable = new EditorDocument { ColorPath = "frame-Color.png", EffectDir = "Effects", OutputPath = "output/result.png" }.ToRenderInput(folder);
        Check(portable.ColorPath == color && portable.EffectDir == Path.Combine(folder, "Effects") && portable.DepthPath == "" && portable.PresetPath == "",
            "Portable UI paths depend on the launch working directory, or optional paths were filled in.");
        var attempts = 0; var exports = 0; var block = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Open(RenderInput next, CancellationToken token)
        {
            attempts++;
            if (block) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (await File.ReadAllTextAsync(next.ColorPath, token) == "bad") throw new InvalidDataException("Invalid color data");
            input = next;
        }
        Task<JsonElement> Call(string method, object? args, CancellationToken token)
        {
            if (method is "save_output" or "save_screenshot") exports++;
            return Task.FromResult(AutomationEngine.Json(new { width = 32, height = 32, loading = false, frameCount = 1, inputGeneration = 1 }));
        }
        var engine = new AutomationEngine(Call, Open, () => input, setInputWatch: (enabled, _) => { input = input with { WatchInput = enabled }; return Task.CompletedTask; });
        Task<JsonElement> Run(string method, object args) => engine.ExecuteAsync(method, AutomationEngine.Json(args));
        var target = input with { ColorPath = game, DepthPath = gameDepth, WatchInput = true };
        await Run("listen_input", target);
        Check(engine.WaitingInput?.State == "waiting" && input == original, "Listener applied an absent input.");
        await Run("get_runtime_info", new { });
        Check(engine.Activity.Snapshot.ShowEmpty && engine.Activity.Snapshot.Runtime.WaitingForInput, "Polling exposed an old image as the waiting target.");
        await Fails<InvalidOperationException>(() => Run("export_image", new { path = input.OutputPath, warmupFrames = 0 }));
        Check(exports == 0, "Waiting listener exported the previous input.");
        await File.WriteAllTextAsync(game, "bad"); await Task.Delay(650);
        Check(attempts == 0, "Listener opened a partial pair without its required depth.");
        await File.WriteAllTextAsync(gameDepth, "depth");
        await Wait(() => engine.WaitingInput?.State == "invalid");
        Check(input == original && engine.Activity.Snapshot.Runtime.Error == "Invalid color data", "Invalid pair replaced the editing context or hid its reason.");
        var failedAttempts = attempts; await Task.Delay(700);
        Check(attempts == failedAttempts, "Unchanged damaged files caused an unbounded restart loop.");
        await File.WriteAllTextAsync(game, "good");
        await Wait(() => !engine.IsBusy);
        Check(input.ColorPath == game && input.WatchInput && !engine.Activity.Snapshot.ShowEmpty, "Listener did not apply the repaired pair.");
        Check(InputPairing.Resolve(game, "kks").AutomaticDepth?.Path == gameDepth, "Game output pairing failed.");
        await Run("stop_listening", new { });
        Check(!input.WatchInput && input.ColorPath == game, "Stopping the listener lost the current image.");

        var future = target with { ColorPath = Path.Combine(folder, "later.png"), DepthPath = "" };
        await Run("listen_input", future);
        await Run("open_input", original);
        await File.WriteAllTextAsync(future.ColorPath, "good"); await Task.Delay(700);
        Check(input == original && engine.WaitingInput == null, "Superseded listener overwrote a manually opened input.");
        using (var locked = new FileStream(game, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Run("listen_input", target);
            await Wait(() => engine.WaitingInput?.State == "unreadable");
            Check(engine.WaitingInput!.Error!.Contains(game) && input == original, "Unreadable files were reported only as missing or replaced the current input.");
            await Run("stop_listening", new { });
        }
        block = true;
        await Run("listen_input", target);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopSessionAsync(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        Check(!engine.IsBusy && engine.WaitingInput == null && input == original, "Stopping during initial arrival leaked a listener or applied its input.");
        Console.WriteLine("PASS: depth pairing/ambiguity, missing-pair waiting, stale-export rejection, damaged-input feedback, change-only retry, manual supersession and listener cancellation.");
    }
}
