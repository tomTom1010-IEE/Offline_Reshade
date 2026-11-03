using System.Text.Json;
using OfflineReShade.Core;

internal static class AddonServiceTests
{
    public static async Task RunAsync(string folder)
    {
        double gain = 1; bool drop = false; int writes = 0;
        object Schema() => new { schemaVersion = 1, parameters = new[] { new { id = "gain", type = "float", min = 0, max = 1, value = new[] { gain } } }, actions = Array.Empty<object>() };
        Task<JsonElement> Call(string method, object? args, CancellationToken token)
        {
            if (method == "list_addons") return Task.FromResult(AutomationEngine.Json(new { addons = new[] { new { name = "fixture", loaded = true, businessApiVersion = 1 } } }));
            if (method == "call_addon_service") {
                var a = AutomationEngine.Json(args);
                if (a.GetProperty("serviceMethod").GetString() == "set") {
                    writes++;
                    using var req = JsonDocument.Parse(a.GetProperty("request").GetString()!);
                    if (!drop) gain = req.RootElement.GetProperty("value")[0].GetDouble();
                }
                return Task.FromResult(AutomationEngine.Json(new { supported = true, payload = JsonSerializer.Serialize(Schema()) }));
            }
            return Task.FromResult(AutomationEngine.Json(new { }));
        }
        var engine = new AutomationEngine(Call, (_, _) => Task.CompletedTask, () => new RenderInput(Path.Combine(folder, "test.png")));
        JsonElement Edit(string id, double value) => AutomationEngine.Json(new { addon = "fixture", method = "set", request = new { id, value = new[] { value } } });
        var list = await engine.ExecuteAsync("list_addon_services", AutomationEngine.Json(new { }));
        if (list.GetProperty("services").GetArrayLength() != 1) throw new Exception("Service discovery failed.");
        await engine.ExecuteAsync("mutate_addon_service", Edit("gain", .25));
        if (gain != .25) throw new Exception("Service edit not applied.");
        foreach (var bad in new[] { Edit("unknown", .5), Edit("gain", 2) }) {
            var rejected = false;
            try { await engine.ExecuteAsync("mutate_addon_service", bad); } catch (ArgumentException) { rejected = true; }
            if (!rejected || writes != 1) throw new Exception("Invalid service write reached Add-on.");
        }
        drop = true; var mismatch = false;
        try { await engine.ExecuteAsync("mutate_addon_service", Edit("gain", .5)); } catch (InvalidOperationException) { mismatch = true; }
        if (!mismatch) throw new Exception("Unapplied service write reported success.");
        foreach (var operation in new[] { "set_addon_imgui_value", "press_uniform" }) {
            using var cancel = new CancellationTokenSource();
            var pending = false;
            Task<JsonElement> CancelCall(string method, object? args, CancellationToken token) {
                if (method == "list_state") return Task.FromResult(AutomationEngine.Json(new { uniforms = new[] { new { id = "trigger", type = "bool", uiType = "button", value = new[] { false } } } }));
                if (method == "set_addon_imgui_value" || method == "set_uniform" && AutomationEngine.Json(args).GetProperty("value").GetBoolean()) {
                    pending = true; cancel.Cancel(); return Task.FromCanceled<JsonElement>(token);
                }
                if (method is "cancel_addon_imgui_value" or "set_uniform") pending = false;
                return Task.FromResult(AutomationEngine.Json(new { }));
            }
            var cancelledEngine = new AutomationEngine(CancelCall, (_, _) => Task.CompletedTask, () => new RenderInput(Path.Combine(folder, "test.png")));
            try { await cancelledEngine.ExecuteAsync(operation, AutomationEngine.Json(new { id = "trigger", value = "true" }), cancel.Token); }
            catch (OperationCanceledException) { }
            if (pending) throw new Exception("Cancellation during dispatch left a pending edit or a pressed button.");
        }
        Console.WriteLine("PASS: independent service discovery, validation and confirmed readback");
    }
}
