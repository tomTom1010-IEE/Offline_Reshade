using OfflineReShade.Core;
using System.Text.Json;

internal static class SessionTests
{
    public static async Task RunAsync(string folder)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static async Task Fails<T>(Func<Task> action) where T : Exception
        { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        var first = Path.Combine(folder, "session-first.png");
        var second = Path.Combine(folder, "session-second.png");
        var broken = Path.Combine(folder, "session-broken.png");
        foreach (var path in new[] { first, second, broken }) await File.WriteAllTextAsync(path, "fixture");
        var input = new RenderInput(first, EffectDir: folder, OutputPath: Path.Combine(folder, "session-output.png"));
        var host = new FakeHost { BrokenPath = broken };
        await using var session = new RenderSessionController(host, Path.Combine(folder, "snapshots"));
        await session.OpenAsync(input);
        host.Gain = 0.42; host.EffectsEnabled = false;
        await session.OpenAsync(input with { ColorPath = second });
        Check(host.Starts == 1 && host.Gain == 0.42 && !host.EffectsEnabled, "Fast input switch changed editing state.");
        await session.OpenAsync(input with { Width = 640, Height = 480 });
        Check(host.Starts == 2 && host.Gain == 0.42 && !host.EffectsEnabled, "Rebuild reloaded disk defaults instead of current parameters.");
        Check(session.State.Applied!.Input.Width == 640, "Rebuild was not applied.");
        await session.CallAsync("reload_effects");
        Check(host.Reloads == 1 && host.Gain == 0.42, "Reload did not use the shared lifecycle or lost parameters.");
        var previous = session.Current;
        await Fails<InvalidOperationException>(() => session.OpenAsync(previous with { ColorPath = broken }));
        Check(session.Current == previous && session.IsConnected && session.State.Applied!.Input == previous && host.Gain == 0.42, "Failed input did not restore previous valid session.");
        host.FailCapture = true;
        var starts = host.Starts;
        await Fails<IOException>(() => session.OpenAsync(previous with { Width = 320, Height = 240 }));
        Check(host.Starts == starts && session.IsConnected && session.Current == previous, "Capture failure destroyed active session.");
        host.FailCapture = false;

        host.GateNextStart = true;
        var obsolete = session.OpenAsync(previous with { Width = 800, Height = 600 });
        await host.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var latest = session.OpenAsync(previous with { Width = 160, Height = 120 });
        await Fails<OperationCanceledException>(() => obsolete);
        await latest;
        Check(session.Current.Width == 160 && host.Gain == 0.42 && session.State.Applied!.Input.Width == 160, "Superseded change won or lost unsaved parameters.");

        host.StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.GateNextStart = true;
        var interrupted = session.OpenAsync(previous with { Width = 1024, Height = 768 });
        await host.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.StopAsync();
        await Fails<OperationCanceledException>(() => interrupted);
        Check(!session.IsConnected && session.State.Applied == null, "Stop allowed an in-flight open to resurrect the session.");
        await session.OpenAsync(input);
        host.Gain = 0.73;
        await session.StopAsync();
        await session.OpenAsync(input);
        Check(host.Gain == 0.73, "Stop/restart lost current parameter snapshot.");
        host.RecoveryRequired = true;
        await session.OpenAsync(input);
        Check(host.Gain == 0.73 && !host.RecoveryRequired, "Recovery lost current parameter snapshot.");
        for (var i = 0; i < 2; i++) { host.RecoveryRequired = true; await session.OpenAsync(input); }
        host.RecoveryRequired = true;
        await Fails<DeviceLostException>(() => session.OpenAsync(input));
        host.RecoveryRequired = false;
        await session.DisposeAsync();
        Check(host.Disposed && !host.IsConnected, "Dispose did not release the owned host.");
        await Fails<ObjectDisposedException>(() => session.OpenAsync(input));
        Console.WriteLine("PASS: shared lifecycle, parameter snapshots, failed-input rollback, capture failure, superseded open, cancellation, bounded recovery and disposal.");
    }

    private sealed class FakeHost : IRenderSessionHost
    {
        public bool IsConnected { get; private set; }
        public bool Disposed, FailCapture, GateNextStart, RecoveryRequired;
        public string BrokenPath = "";
        public double Gain = 1;
        public bool EffectsEnabled = true;
        public int Starts, Reloads;
        public TaskCompletionSource StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task StartAsync(RenderInput input, string? snapshot, CancellationToken token)
        {
            Starts++;
            if (GateNextStart)
            {
                GateNextStart = false; StartEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            if (input.ColorPath == BrokenPath) throw new InvalidOperationException("Broken image");
            Gain = snapshot == null ? 1 : JsonSerializer.Deserialize<double>(await File.ReadAllTextAsync(snapshot, token));
            EffectsEnabled = true; RecoveryRequired = false; IsConnected = true;
        }
        public Task StopAsync() { IsConnected = false; return Task.CompletedTask; }
        public async Task<JsonElement> CallAsync(string method, object? args, CancellationToken cancellationToken)
        {
            if (!IsConnected) throw new IOException("Host stopped");
            if (method == "get_runtime_info") return AutomationEngine.Json(new { loading = false, recoveryRequired = RecoveryRequired, effectsEnabled = EffectsEnabled, inputGeneration = 1, frameCount = 1 });
            if (method == "reload_effects") Reloads++;
            if (method == "export_preset")
            {
                if (FailCapture) throw new IOException("Disk full");
                var path = AutomationEngine.Json(args).GetProperty("path").GetString()!;
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Gain), cancellationToken);
            }
            if (method == "set_input_paths" && ((RenderInput)args!).ColorPath == BrokenPath)
            { Gain = 99; throw new InvalidOperationException("Bad input after resource replacement"); }
            if (method == "set_effects_state") EffectsEnabled = AutomationEngine.Json(args).GetProperty("enabled").GetBoolean();
            return AutomationEngine.Json(new { });
        }
        public ValueTask DisposeAsync() { Disposed = true; IsConnected = false; return ValueTask.CompletedTask; }
    }
}
