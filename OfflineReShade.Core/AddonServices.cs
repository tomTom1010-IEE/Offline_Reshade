using System.Text.Json;

namespace OfflineReShade.Core;

public sealed partial class AutomationEngine
{
    private async Task<JsonElement> AddonServiceAsync(string addon, string method, JsonElement request, CancellationToken token)
    {
        var effect = addon switch { "MARTYSMODS LUT-Manager" => "MartysMods_LUTMANAGER.fx", "MARTYSMODS ReGrade+" => "MartysMods_REGRADE+.fx", _ => "" };
        var loaded = await _call("list_addons", null, token);
        var info = loaded.GetProperty("addons").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == addon && a.GetProperty("loaded").GetBoolean());
        if (info.ValueKind == JsonValueKind.Undefined) throw new NotSupportedException("The requested Add-on is not loaded.");
        var hasProvider = info.TryGetProperty("businessApiVersion", out var providerVersion) && providerVersion.GetInt32() == 1;
        if (effect.Length != 0 && !hasProvider)
        {
            // Deliberately limited to the companion FX's public contract. Private
            // LUT selection and grading curves are not inferred from ImGui state.
            var allowed = effect == "MartysMods_LUTMANAGER.fx"
                ? new[] { "UPSAMPLE_LUT", "SHOW_LUT_PREVIEW", "LUT_BLEND_INTENSITY_CHROMA", "LUT_BLEND_INTENSITY_LUMA" }
                : new[] { "REGRADE_LUT_MODE" };
            var state = await _call("list_state", null, token);
            var companionParameters = state.GetProperty("uniforms").EnumerateArray().Where(u => u.GetProperty("effectName").GetString() == effect && allowed.Contains(u.GetProperty("name").GetString())).Select(u => u.Clone()).ToArray();
            if (companionParameters.Length != allowed.Length) throw new NotSupportedException("The installed companion FX does not match this adapter schema.");
            foreach (var parameter in companionParameters) {
                var name = parameter.GetProperty("name").GetString();
                var expected = name == "REGRADE_LUT_MODE" ? "int" : name is "UPSAMPLE_LUT" or "SHOW_LUT_PREVIEW" ? "bool" : "float";
                if (parameter.GetProperty("type").GetString() != expected || parameter.GetProperty("value").GetArrayLength() != 1) throw new NotSupportedException("The companion parameter type changed; adapter requires review.");
            }
            if (method == "action") throw new NotSupportedException("This companion adapter has no independent private Add-on actions.");
            if (method == "set") {
                var id = request.GetProperty("id").GetString();
                if (!companionParameters.Any(p => p.GetProperty("id").GetString() == id)) throw new ArgumentException("Unknown companion parameter.");
                await _history.ChangeAsync("set_uniform", null, ct => _call("set_uniform", new { id, value = request.GetProperty("value") }, ct), token);
                return await AddonServiceAsync(addon, "get", Json(new { }), token);
            }
            return Json(new { schemaVersion = 1, scope = "companion_fx", privateControls = false, fxHistory = true, parameters = companionParameters, actions = Array.Empty<object>() });
        }
        var reply = await _call("call_addon_service", new { addon, serviceMethod = method, request = request.GetRawText() }, token);
        if (!reply.GetProperty("supported").GetBoolean()) throw new NotSupportedException(reply.GetProperty("error").GetString());
        using var parsed = JsonDocument.Parse(reply.GetProperty("payload").GetString()!);
        var schema = parsed.RootElement;
        if (schema.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
        if (!schema.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1 || !schema.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid independent Add-on schema.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters.EnumerateArray())
            if (!parameter.TryGetProperty("id", out var id) || string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!)) throw new InvalidDataException("Missing or duplicate Add-on parameter ID.");
        return schema.Clone();
    }

    private async Task<JsonElement> ListAddonServicesAsync(CancellationToken token)
    {
        var state = await _call("list_addons", null, token);
        var services = new List<object>();
        foreach (var addon in state.GetProperty("addons").EnumerateArray())
        {
            var name = addon.GetProperty("name").GetString()!;
            if (!addon.GetProperty("loaded").GetBoolean()) continue;
            if ((!addon.TryGetProperty("businessApiVersion", out var version) || version.GetInt32() != 1) && name is not ("MARTYSMODS LUT-Manager" or "MARTYSMODS ReGrade+")) continue;
            try { services.Add(new { addon = name, schema = await AddonServiceAsync(name, "get", Json(new { }), token), error = "" }); }
            catch (Exception error) when (error is not OperationCanceledException) { services.Add(new { addon = name, schema = (object?)null, error = error.Message }); }
        }
        return Json(new { schemaVersion = 1, services, privateStateHistory = false });
    }

    private async Task<JsonElement> MutateAddonServiceAsync(JsonElement args, CancellationToken token)
    {
        var addon = args.GetProperty("addon").GetString()!;
        var method = args.GetProperty("method").GetString();
        if (method is not ("set" or "action")) throw new ArgumentException("Use set or action for a service mutation.");
        var request = args.GetProperty("request");
        var before = await AddonServiceAsync(addon, "get", Json(new { }), token);
        var id = request.GetProperty("id").GetString();
        var entries = method == "set" ? before.GetProperty("parameters") : before.GetProperty("actions");
        var target = entries.EnumerateArray().FirstOrDefault(x => x.GetProperty("id").GetString() == id);
        if (target.ValueKind == JsonValueKind.Undefined || (target.TryGetProperty("readOnly", out var readOnly) && readOnly.GetBoolean())) throw new ArgumentException("Unknown or read-only service target.");
        if (method == "set")
        {
            var values = request.GetProperty("value");
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != target.GetProperty("value").GetArrayLength()) throw new ArgumentException("Parameter value has the wrong shape.");
            var type = target.GetProperty("type").GetString();
            foreach (var value in values.EnumerateArray()) {
                if (type == "bool") { if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Expected boolean value."); continue; }
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new ArgumentException("Expected finite numeric value.");
                if (type is "int" or "uint" && (Math.Truncate(number) != number || number < (type == "uint" ? 0 : int.MinValue) || number > (type == "uint" ? uint.MaxValue : int.MaxValue))) throw new ArgumentException("Integer out of range.");
                if (target.TryGetProperty("min", out var min) && number < min.GetDouble() || target.TryGetProperty("max", out var max) && number > max.GetDouble()) throw new ArgumentException("Parameter is outside its declared range.");
            }
        }
        await AddonServiceAsync(addon, method, request, token);
        var after = await AddonServiceAsync(addon, "get", Json(new { }), token);
        if (method == "set")
        {
            var observed = after.GetProperty("parameters").EnumerateArray().FirstOrDefault(x => x.GetProperty("id").GetString() == id);
            if (observed.ValueKind == JsonValueKind.Undefined || !SameValue(request.GetProperty("value"), observed.GetProperty("value"), target.GetProperty("type").GetString() == "float")) throw new InvalidOperationException("Add-on readback did not match the requested value.");
        }
        return Json(new { status = "applied", addon, schema = after, privateStateHistory = false });
    }

    private static bool SameValue(JsonElement requested, JsonElement actual, bool floating)
    {
        if (requested.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
            return requested.GetDouble() == actual.GetDouble() || floating && (float)requested.GetDouble() == (float)actual.GetDouble();
        if (requested.ValueKind == JsonValueKind.Array && actual.ValueKind == JsonValueKind.Array)
            return requested.GetArrayLength() == actual.GetArrayLength() && requested.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => SameValue(pair.First, pair.Second, floating));
        return requested.ValueKind == actual.ValueKind && requested.ToString() == actual.ToString();
    }
}
