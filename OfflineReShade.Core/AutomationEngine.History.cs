using System.Text.Json;
using System.Text.Json.Nodes;

namespace OfflineReShade.Core;

public sealed partial class AutomationEngine
{
    private readonly EffectEditHistory _history;
    private readonly Func<string, CancellationToken, Task>? _setPresetSource;
    private JsonElement _listenerDecision = Json(new { });
    private Task _cancelTransaction = Task.CompletedTask;
    public EditHistoryStatus History => _history.Status;
    private async Task WaitForEditStateAsync(CancellationToken token)
    {
        // FX settings remain readable/savable when the watched image has become invalid.
        // A render-validity wait here would prevent opening a replacement image to recover.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            while ((await _call("get_runtime_info", null, deadline.Token)).GetProperty("loading").GetBoolean())
                await Task.Delay(50, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Timed out waiting for editable FX state."); }
    }
    private static string? Text(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? ExpectedRevision(JsonElement args) => args.TryGetProperty("expectedRevision", out var revision) && revision.ValueKind != JsonValueKind.Null ? revision.GetInt64() : null;
    private JsonElement ListenerArguments(RenderInput input) => CopyPresetDecision(Json(input), _listenerDecision);
    public static JsonElement CopyPresetDecision(JsonElement input, JsonElement decision)
    {
        var node = JsonNode.Parse(input.GetRawText())!.AsObject();
        foreach (var name in new[] { "unsavedChanges", "savePath", "expectedRevision" })
            if (decision.ValueKind == JsonValueKind.Object && decision.TryGetProperty(name, out var value)) node[name] = JsonNode.Parse(value.GetRawText());
        return Json(node);
    }
    public bool ReplacesPreset(string method, JsonElement args)
    {
        if (method == "set_preset") return true;
        if (method == "export_image") return args.TryGetProperty("applyInput", out var input) && input.ValueKind == JsonValueKind.Object && ReplacesPreset("open_input", input);
        if (method is not ("open_input" or "listen_input")) return false;
        var preset = Text(args, "presetPath");
        return !string.IsNullOrWhiteSpace(preset) && !string.Equals(Path.GetFullPath(preset), _current().PresetPath, StringComparison.OrdinalIgnoreCase);
    }
    private async Task CheckUnsavedAsync(JsonElement args, CancellationToken token)
    {
        var choice = Text(args, "unsavedChanges") ?? "reject";
        if (choice is not ("reject" or "save" or "discard")) throw new ArgumentException("unsavedChanges must be reject, save or discard.");
        // Explicit discard is also the recovery path for unreadable/broken old effects.
        if (choice != "discard" && _current().ColorPath.Length > 0) await _history.SynchronizeAsync(token);
        _history.CheckRevision(ExpectedRevision(args));
        if (!History.IsModified || choice == "discard") return;
        if (choice == "reject") throw new InvalidOperationException("The preset has unsaved FX changes. Save them or explicitly choose discard before replacing it.");
        var savePath = Text(args, "savePath");
        await SavePresetAsync(string.IsNullOrWhiteSpace(savePath) ? "save_preset" : "save_preset_as", Json(new { path = savePath }), token);
    }
    private async Task<JsonElement> ExecuteGuardedMutationAsync(string method, JsonElement args, CancellationToken token)
    {
        if (method == "get_edit_history") { await _history.SynchronizeAsync(token); return Json(History); }
        if (method == "begin_edit") return Json(new { transactionId = _history.BeginTransaction() });
        if (method == "end_edit") { _history.EndTransaction(Text(args, "transactionId") ?? throw new ArgumentException("Missing transactionId.")); return Json(History); }
        var transactionId = Text(args, "transactionId");
        _history.RequireAccess(transactionId);
        if (method is "undo" or "redo")
        {
            if (method == "undo") await _history.UndoAsync(token); else await _history.RedoAsync(token);
            return Json(History);
        }
        if (method == "check_unsaved_changes") { await CheckUnsavedAsync(args, token); return Json(History); }
        if (ReplacesPreset(method, args) && method != "export_image") await CheckUnsavedAsync(args, token);
        if (method == "listen_input") _listenerDecision = Json(new { unsavedChanges = "discard", expectedRevision = History.Revision });
        if (method is "set_parameters" or "set_uniform" or "reset_uniform" or "set_technique_state" or "set_effects_state" or "reorder_techniques" or "set_preprocessor_definition")
        {
            return await _history.ChangeAsync(method, transactionId, async ct =>
            {
                var result = await ExecuteMutationAsync(method, args, ct);
                if (method == "set_preprocessor_definition")
                {
                    await RuntimeOperations.WaitForFramesAsync(_call, 2, ct);
                    await _call("validate_export", null, ct);
                }
                return result;
            }, token);
        }
        var reset = method == "set_preset" || method == "open_input" && (_current().ColorPath.Length == 0 || ReplacesPreset(method, args));
        var response = await ExecuteMutationAsync(method, args, token);
        if (reset) _history.ResetContext();
        return response;
    }
}
