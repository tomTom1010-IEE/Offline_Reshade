using System.IO.Pipes;
using System.Text.Json;
using OfflineReShade.Core;

if (args is ["--native-lifecycle", var executable, var fixtures])
{
    await NativeProcessTests.RunAsync(Path.GetFullPath(executable), Path.GetFullPath(fixtures));
    return;
}

var folder = Path.Combine(Path.GetTempPath(), "OfflineReShadeTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
await WorkspaceTests.RunAsync(folder);
await SessionTests.RunAsync(folder);
await OperationTests.RunAsync(folder);
await InputWorkflowTests.RunAsync(folder);
await ExportWorkflowTests.RunAsync(folder);
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
var source = Path.Combine(folder, "source.png"); await File.WriteAllTextAsync(source, "source");
var output = Path.Combine(folder, "output.png");
await Expect<ArgumentException>(() => Task.Run(() => RuntimeOperations.OutputPath(source, source, "")));

// Cancellation after sending must drain its response, preserving the next request's ID.
var pipeName = "OfflineReShade.Tests." + Guid.NewGuid().ToString("N");
using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
var serving = Task.Run(async () => {
    await server.WaitForConnectionAsync();
    using var reader = new StreamReader(server, leaveOpen: true);
    using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
    for (var i = 0; i < 2; i++) {
        var doc = JsonDocument.Parse((await reader.ReadLineAsync())!);
        if (i == 0) await Task.Delay(100);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = doc.RootElement.GetProperty("id").GetInt32(), ok = true, result = new { value = i } }));
    }
});
using (var connection = new RuntimeConnection()) {
    await connection.ConnectAsync(pipeName);
    using var cancel = new CancellationTokenSource(40);
    await Expect<OperationCanceledException>(() => connection.CallAsync("first", cancellationToken: cancel.Token));
    var second = await connection.CallAsync("second"); Check(second.GetProperty("value").GetInt32() == 1, "RPC response became misaligned after cancellation.");
}
await serving;

using (var broker = new AutomationBroker((method, parameters, _) => Task.FromResult(AutomationEngine.Json(new { method })), "integration")) {
    Check(AutomationBroker.Discover().Any(x => x.Id == broker.Descriptor.Id), "Session discovery failed.");
    using var a = new RuntimeConnection(); using var b = new RuntimeConnection();
    await a.ConnectAsync(broker.Descriptor.PipeName); await b.ConnectAsync(broker.Descriptor.PipeName);
    await Compatibility.VerifyAsync(a, "automation");
    var responses = await Task.WhenAll(a.CallAsync("one"), b.CallAsync("two"));
    Check(responses[0].GetProperty("method").GetString() == "one" && responses[1].GetProperty("method").GetString() == "two", "Broker mixed simultaneous clients.");
}

var current = new RenderInput(source, OutputPath: output, EffectDir: folder);
var calls = 0; var frame = 0; var exports = 0; var slow = false;
async Task<JsonElement> Call(string method, object? args, CancellationToken token) {
    if (method == "get_runtime_info") { calls++; return AutomationEngine.Json(new { loading = calls <= 2, frameCount = ++frame, inputGeneration = 1 }); }
    if (method == "list_state") return AutomationEngine.Json(new { uniforms = new[] { new { id = "test::value", value = 1 } }, techniques = Array.Empty<object>() });
    if (method is "save_screenshot" or "save_output") {
        if (slow) await Task.Delay(500, token);
        var path = AutomationEngine.Json(args).GetProperty("path").GetString()!;
        await File.WriteAllTextAsync(path, "render", token); exports++;
    }
    return AutomationEngine.Json(new { });
}
await RuntimeOperations.WaitForFramesAsync(Call, 4, CancellationToken.None);
Check(calls >= 7, "Warmup did not wait for actual frames after loading.");
var engine = new AutomationEngine(Call, (input, _) => { current = input; return Task.CompletedTask; }, () => current);
async Task WaitJob() { using var stop = new CancellationTokenSource(10000); while (engine.IsBusy) await Task.Delay(10, stop.Token); }
var request = new BatchRequest([current], WarmupFrames: 0);
await engine.ExecuteAsync("batch_start", AutomationEngine.Json(request)); await WaitJob();
Check(engine.Job!.State == "completed" && exports == 1, "Batch failed: " + JsonSerializer.Serialize(engine.Job));
await engine.ExecuteAsync("batch_start", AutomationEngine.Json(request)); await WaitJob();
Check(exports == 1, "Resume ignored checkpoint/output hash.");
await File.WriteAllTextAsync(output, "tampered");
await engine.ExecuteAsync("batch_start", AutomationEngine.Json(request)); await WaitJob();
Check(exports == 2, "Resume accepted a changed output file.");
slow = true;
await engine.ExecuteAsync("batch_start", AutomationEngine.Json(request with { Resume = false }));
await Expect<InvalidOperationException>(() => engine.ExecuteAsync("set_uniform", AutomationEngine.Json(new { id = "x", value = 1 })));
await engine.ExecuteAsync("batch_cancel", AutomationEngine.Json(new { })); await WaitJob();
Check(engine.Job!.State == "cancelled", "Batch cancellation failed.");
Check(current.ColorPath == source, "Original input was not restored.");
await engine.StopAsync();
Console.WriteLine("PASS: output protection, RPC cancellation framing, real-frame warmup, batch resume/hash, edit lock and cancellation.");

foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2, 4, 10 }) {
    var surface = PreviewGeometry.Surface(7680, 4320, dpi);
    Check(surface.Width <= 8192 && surface.Height <= 8192 && (long)surface.Width * surface.Height <= PreviewGeometry.MaxPreviewPixels, "DPI preview exceeded allocation bounds.");
    Check(Math.Abs((double)surface.Width / surface.Height - 16.0 / 9) < .002, "DPI surface changed aspect ratio.");
}
foreach (var dimensions in new[] { (16384, 1), (1, 16384), (3840, 2160), (1, 1) })
foreach (var zoom in new[] { 1.0, 2, 16 }) {
    var layout = PreviewGeometry.Layout(900, 600, dimensions.Item1, dimensions.Item2, zoom, -1e8, 1e8);
    Check(double.IsFinite(layout.X + layout.Y + layout.Width + layout.Height), "Invalid preview geometry.");
    Check(layout.X < 900 && layout.X + layout.Width > 0 && layout.Y < 600 && layout.Y + layout.Height > 0, "Panning lost the image completely.");
    if (layout.Width < 900) Check(Math.Abs(layout.X - (900 - layout.Width) / 2) < .001, "Thin image was not centered horizontally.");
    if (layout.Height < 600) Check(Math.Abs(layout.Y - (600 - layout.Height) / 2) < .001, "Thin image was not centered vertically.");
}
await Expect<ArgumentException>(() => Task.Run(() => AutomationEngine.ValidateInput(current with { Width = 16384, Height = 16384 })));
var budget = new DeviceRecoveryBudget(); var now = DateTimeOffset.UtcNow;
Check(budget.TryAcquire(now) && budget.TryAcquire(now) && budget.TryAcquire(now) && !budget.TryAcquire(now), "Device recovery was not bounded.");
Check(budget.TryAcquire(now.AddSeconds(31)), "Recovery budget did not expire.");
var recoveries = 0; var lost = true;
Task<JsonElement> DeviceCall(string method, object? args, CancellationToken token) => method == "get_runtime_info"
    ? Task.FromResult(AutomationEngine.Json(new { recoveryRequired = lost, deviceError = "test device removed", loading = false, frameCount = ++frame, inputGeneration = 1 })) : Call(method, args, token);
var recoverable = new AutomationEngine(DeviceCall, (_, _) => { recoveries++; lost = false; return Task.CompletedTask; }, () => current);
slow = false;
await recoverable.ExecuteAsync("export_image", AutomationEngine.Json(new { path = output, warmupFrames = 0 }));
Check(recoveries == 1, "Export did not recover a lost device.");
lost = true; recoveries = 0;
var permanent = new AutomationEngine(DeviceCall, (_, _) => { recoveries++; return Task.CompletedTask; }, () => current);
var exportsBefore = exports;
await Expect<DeviceLostException>(() => permanent.ExecuteAsync("export_image", AutomationEngine.Json(new { path = output, warmupFrames = 0 })));
Check(recoveries == 3 && exports == exportsBefore, "Permanent device failure was exported or retried indefinitely.");
Console.WriteLine("PASS: DPI/surface bounds, extreme aspect-ratio pan/zoom, image allocation limits and bounded device recovery.");

// Compatibility must fail before mutations; old/missing/newer contracts are not guessed.
var valid = AutomationEngine.Json(Compatibility.Descriptor("native"));
Compatibility.Validate(valid, "native");
foreach (var field in new[] { "endpoint", "protocolVersion", "nativeRpcVersion", "offlineApiVersion", "previewBridgeVersion", "reShadeApiVersion", "upstreamCommit" }) {
    var incompatible = System.Text.Json.Nodes.JsonNode.Parse(valid.GetRawText())!.AsObject();
    incompatible.Remove(field);
    await Expect<InvalidOperationException>(() => Task.Run(() => Compatibility.Validate(AutomationEngine.Json(incompatible), "native")));
    incompatible[field] = "wrong";
    await Expect<InvalidOperationException>(() => Task.Run(() => Compatibility.Validate(AutomationEngine.Json(incompatible), "native")));
}
using (var badBroker = new AutomationBroker((_, _, _) => Task.FromResult(AutomationEngine.Json(new { })), "wrong endpoint")) {
    using var connection = new RuntimeConnection();
    await connection.ConnectAsync(badBroker.Descriptor.PipeName);
    await Expect<InvalidOperationException>(() => Compatibility.VerifyAsync(connection, "native"));
    Check(!connection.IsConnected, "Incompatible endpoint remained connected.");
}
var diagnostics = await Diagnostics.CollectAsync((_, _, _) => throw new IOException("No input opened"), CancellationToken.None);
Check(diagnostics.GetProperty("runtimeError").GetString() == "No input opened", "Diagnostics unavailable without a runtime.");
Console.WriteLine("PASS: native/broker compatibility handshake, incompatible connection teardown and offline diagnostics.");
