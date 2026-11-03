using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OfflineReShade.Core;

namespace OfflineReShade.Mcp;

[McpServerToolType]
public sealed class McpTools(McpBridge bridge)
{
    [McpServerTool(Name = "list_addons", ReadOnly = true), Description("List loaded Add-ons, external dependencies, event support and independent service API availability. Loading does not imply full functionality.")]
    public Task<JsonElement> ListAddons(CancellationToken cancellationToken) => bridge.CallAsync("list_addons", token: cancellationToken);

    [McpServerTool(Name = "list_addon_services", ReadOnly = true), Description("Read independent Add-on schemas, values and action arguments without opening native UI. Includes service ABI v1 providers and version-verified Marty preset adapters. Inspect privateAdapterError and limitations; private actions have no FX undo guarantee.")]
    public Task<JsonElement> ListAddonServices(CancellationToken cancellationToken) => bridge.CallAsync("list_addon_services", token: cancellationToken);

    [McpServerTool(Name = "set_addon_parameter"), Description("Set a stable independent service parameter ID. Value must match the schema value shape (normally an array). Validates target and confirms readback. Does not use ImGui capture.")]
    public Task<CallToolResult> SetAddonParameter(string addon, string id, JsonElement value, CancellationToken cancellationToken)
        => ExecuteToolAsync("mutate_addon_service", new { addon, method = "set", request = new { id, value } }, cancellationToken);

    [McpServerTool(Name = "invoke_addon_action"), Description("Invoke a declared independent action with JSON arguments from list_addon_services. Verified Marty adapters support set_curve, set_light and select_lut_from_preset; these reload effects, validate readback and restore on failure. Private actions are not covered by FX undo; export a preset copy to persist them.")]
    public Task<CallToolResult> InvokeAddonAction(string addon, string id, JsonElement arguments, CancellationToken cancellationToken)
        => ExecuteToolAsync("mutate_addon_service", new { addon, method = "action", request = new { id, arguments } }, cancellationToken);

    [McpServerTool(Name = "press_parameter"), Description("Press a boolean FX ui_type=button action once, wait for a rendered frame, then release it.")]
    public Task<CallToolResult> PressParameter(string id, CancellationToken cancellationToken) => ExecuteToolAsync("press_uniform", new { id }, cancellationToken);
    [McpServerTool(Name = "list_sessions", ReadOnly = true), Description("List running local WinUI automation sessions. Select an explicit session ID to attach.")]
    public SessionDescriptor[] ListSessions() => AutomationBroker.Discover();

    [McpServerTool(Name = "attach_session"), Description("Attach to the current preview in a running WinUI session. UI and MCP share the same parameters and batch lock.")]
    public async Task<object> AttachSession(string sessionId, CancellationToken cancellationToken) { await bridge.AttachAsync(sessionId, cancellationToken); return bridge.ConnectionInfo(); }

    [McpServerTool(Name = "open_input"), Description("Open a color/depth pair. In independent mode this launches the native host. Use absolute paths. Zero width and height preserve each source image's resolution.")]
    public Task<CallToolResult> OpenInput(string colorPath, string outputPath, string effectDir, CancellationToken cancellationToken,
        string depthPath = "", string depthFormat = "raw", string depthProfile = "kks", string depthDownsample = "max2x2", string presetPath = "", int width = 0, int height = 0, bool watchInput = false,
        string unsavedChanges = "reject", string? savePath = null, long? expectedRevision = null)
        => ExecuteToolAsync("open_input", Decision(new RenderInput(colorPath, depthPath, depthFormat, depthProfile, depthDownsample, outputPath, effectDir, presetPath, width, height, watchInput), unsavedChanges, savePath, expectedRevision), cancellationToken);

    [McpServerTool(Name = "get_status", ReadOnly = true), Description("Read resolution, loading, frame count, input generation, rendering adapter, preview transport and device recovery diagnostics.")]
    public Task<JsonElement> GetStatus(CancellationToken cancellationToken) => bridge.CallAsync("get_runtime_info", token: cancellationToken);

    [McpServerTool(Name = "listen_input"), Description("Listen for a color/depth pair that may not exist yet. Validates settings, waits for stable files, and opens them through the shared session. While waiting, exporting the previous image is blocked; malformed files are reported and retried only after they change.")]
    public Task<CallToolResult> ListenInput(string colorPath, string outputPath, string effectDir, CancellationToken cancellationToken,
        string depthPath = "", string depthFormat = "raw", string depthProfile = "kks", string depthDownsample = "max2x2", string presetPath = "", int width = 0, int height = 0,
        string unsavedChanges = "reject", string? savePath = null, long? expectedRevision = null)
        => ExecuteToolAsync("listen_input", Decision(new RenderInput(colorPath, depthPath, depthFormat, depthProfile, depthDownsample, outputPath, effectDir, presetPath, width, height, true), unsavedChanges, savePath, expectedRevision), cancellationToken);

    [McpServerTool(Name = "stop_listening"), Description("Stop waiting for game files and disable automatic input updates. Retains the last applied image and parameters.")]
    public Task<CallToolResult> StopListening(CancellationToken cancellationToken) => ExecuteToolAsync("stop_listening", null, cancellationToken);

    [McpServerTool(Name = "get_diagnostics", ReadOnly = true), Description("Read component versions, pinned upstream identity, release provenance, core binary integrity and runtime/device health. Works before opening an input. Does not change settings or reset the device.")]
    public Task<JsonElement> GetDiagnostics(CancellationToken cancellationToken) => bridge.CallAsync("get_diagnostics", token: cancellationToken);

    [McpServerTool(Name = "list_effects", ReadOnly = true), Description("List effect/parameter metadata and current values. Filter by FX filename to keep large shader libraries manageable. Wait for get_status.loading=false first.")]
    public async Task<object> ListEffects(CancellationToken cancellationToken, string effectName = "", int offset = 0, int limit = 100)
    {
        if (offset < 0 || limit is < 1 or > 500) throw new ArgumentException("Offset must be nonnegative; limit must be 1–500.");
        var state = await bridge.CallAsync("list_state", token: cancellationToken);
        bool Match(JsonElement x) => effectName.Length == 0 || x.GetProperty("effectName").GetString()!.Contains(effectName, StringComparison.OrdinalIgnoreCase);
        var uniforms = state.GetProperty("uniforms").EnumerateArray().Where(Match).ToArray();
        var techniques = state.GetProperty("techniques").EnumerateArray().Where(Match).ToArray();
        var definitions = state.GetProperty("preprocessorDefinitions").EnumerateArray().Where(Match).ToArray();
        return new { totalParameters = uniforms.Length, totalTechniques = techniques.Length, totalDefinitions = definitions.Length, offset, limit,
            uniforms = uniforms.Skip(offset).Take(limit).ToArray(), techniques = techniques.Skip(offset).Take(limit).ToArray(), definitions = definitions.Skip(offset).Take(limit).ToArray() };
    }

    [McpServerTool(Name = "set_parameters"), Description("Set 1–256 uniform values by stable effect.fx::uniform ID. Values use real shader units; slider display mapping does not affect them.")]
    public Task<CallToolResult> SetParameters(JsonElement[] updates, CancellationToken cancellationToken, string? transactionId = null) => ExecuteToolAsync("set_parameters", new { updates, transactionId }, cancellationToken);

    [McpServerTool(Name = "set_technique"), Description("Enable or disable a technique by its effect.fx::technique ID.")]
    public Task<CallToolResult> SetTechnique(string id, bool enabled, CancellationToken cancellationToken, string? transactionId = null) => ExecuteToolAsync("set_technique_state", new { id, enabled, transactionId }, cancellationToken);

    [McpServerTool(Name = "set_effects_enabled"), Description("Enable or disable all ReShade effects. This is an undoable preset edit that affects exported images.")]
    public Task<CallToolResult> SetEffectsEnabled(bool enabled, CancellationToken cancellationToken, string? transactionId = null) => ExecuteToolAsync("set_effects_state", new { enabled, transactionId }, cancellationToken);

    [McpServerTool(Name = "set_preview_comparison"), Description("Show the original input in the live canvas, or return to processed preview. Transient display only: exports, preview captures and preset/history remain processed and unchanged. Resets after runtime rebuild.")]
    public Task<CallToolResult> SetPreviewComparison(bool original, CancellationToken cancellationToken) => ExecuteToolAsync("set_preview_comparison", new { original }, cancellationToken);

    [McpServerTool(Name = "reorder_techniques"), Description("Set technique execution order using the complete ordered list of technique IDs returned by list_effects.")]
    public Task<CallToolResult> ReorderTechniques(string[] ids, CancellationToken cancellationToken, string? transactionId = null) => ExecuteToolAsync("reorder_techniques", new { ids, transactionId }, cancellationToken);

    [McpServerTool(Name = "load_preset"), Description("Load an existing preset INI. Query loading status and wait before exporting.")]
    public Task<CallToolResult> LoadPreset(string path, CancellationToken cancellationToken, string unsavedChanges = "reject", string? savePath = null, long? expectedRevision = null)
        => ExecuteToolAsync("set_preset", new { path, unsavedChanges, savePath, expectedRevision }, cancellationToken);
    [McpServerTool(Name = "save_preset"), Description("Save FX edits to their source INI, or Save As when an absolute path is supplied. Publishes atomically and marks FX content saved. Add-on private state is not included.")]
    public Task<CallToolResult> SavePreset(CancellationToken cancellationToken, string path = "")
        => ExecuteToolAsync(string.IsNullOrWhiteSpace(path) ? "save_preset" : "save_preset_as", new { path }, cancellationToken);
    [McpServerTool(Name = "export_preset_copy"), Description("Write a separate INI copy without changing the source preset or its unsaved marker.")]
    public Task<CallToolResult> ExportPresetCopy(string path, CancellationToken cancellationToken)
        => ExecuteToolAsync("export_preset", new { path }, cancellationToken);
    [McpServerTool(Name = "get_edit_history", ReadOnly = true), Description("Read shared FX undo/redo, unsaved marker, decision revision and Add-on capability limits. Reconciles external changes when idle; returns cached state during an operation.")]
    public Task<CallToolResult> GetEditHistory(CancellationToken cancellationToken) => ExecuteToolAsync("get_edit_history", null, cancellationToken);
    [McpServerTool(Name = "begin_edit"), Description("Begin one undoable gesture. Pass returned transactionId with all its FX edits; end_edit releases it. Other writers are rejected until end or cancel_operation.")]
    public Task<CallToolResult> BeginEdit(CancellationToken cancellationToken) => ExecuteToolAsync("begin_edit", null, cancellationToken);
    [McpServerTool(Name = "end_edit"), Description("Finish a grouped FX gesture while preserving its applied values as one undo entry.")]
    public Task<CallToolResult> EndEdit(string transactionId, CancellationToken cancellationToken) => ExecuteToolAsync("end_edit", new { transactionId }, cancellationToken);
    [McpServerTool(Name = "undo"), Description("Undo the most recent successful shared FX edit, including edits made in WinUI.")]
    public Task<CallToolResult> Undo(CancellationToken cancellationToken) => ExecuteToolAsync("undo", null, cancellationToken);
    [McpServerTool(Name = "redo"), Description("Redo an undone shared FX edit.")]
    public Task<CallToolResult> Redo(CancellationToken cancellationToken) => ExecuteToolAsync("redo", null, cancellationToken);
    [McpServerTool(Name = "reset_parameter"), Description("Reset an editable uniform to its shader default as one undoable edit.")]
    public Task<CallToolResult> ResetParameter(string id, CancellationToken cancellationToken, string? transactionId = null)
        => ExecuteToolAsync("reset_uniform", new { id, transactionId }, cancellationToken);
    [McpServerTool(Name = "set_preprocessor_definition"), Description("Change an FX preprocessor override, await recompilation and validate it. Failed edits roll back and do not enter history.")]
    public Task<CallToolResult> SetPreprocessorDefinition(string effectName, string name, string value, CancellationToken cancellationToken, string? transactionId = null)
        => ExecuteToolAsync("set_preprocessor_definition", new { effectName, name, value, transactionId }, cancellationToken);

    [McpServerTool(Name = "render_preview"), Description("Return a clean PNG preview image to the model after FX compilation and actual warmup frames. Maximum width is 2048 pixels.")]
    public async Task<CallToolResult> RenderPreview(CancellationToken cancellationToken, int maxWidth = 1280, int warmupFrames = 5)
    {
        var folder = Path.Combine(AutomationBroker.SessionDirectory, "previews");
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            Directory.CreateDirectory(folder);
            var preview = await bridge.CallAsync("render_preview", new { path, maxWidth, warmupFrames }, cancellationToken);
            var bytes = Convert.FromBase64String(preview.GetProperty("data").GetString()!);
            return new CallToolResult { Content = [ImageContentBlock.FromBytes(bytes, "image/png")],
                StructuredContent = AutomationEngine.Json(new { operation = preview.GetProperty("operation") }) };
        }
        catch (Exception error) { return Failure(error, "render_preview"); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [McpServerTool(Name = "export_image"), Description("Export the applied image after pending parameter submissions, compilation and warmup. ReShade screenshot is the default; direct PNG is explicit. conflictPolicy: replace, rename or fail. Pending UI configuration requires explicit applyInput (applied and exported under one lock). Optional expectedInput rejects a changed source. Returns the actual output path.")]
    public Task<CallToolResult> ExportImage(string path, CancellationToken cancellationToken, int warmupFrames = 5, bool reShadeShot = true,
        string conflictPolicy = "replace", RenderInput? applyInput = null, RenderInput? expectedInput = null,
        string unsavedChanges = "reject", string? savePath = null, long? expectedRevision = null)
        => ExecuteToolAsync("export_image", Decision(new ImageExportRequest(path, warmupFrames, reShadeShot, conflictPolicy, applyInput, expectedInput), unsavedChanges, savePath, expectedRevision), cancellationToken);

    [McpServerTool(Name = "start_batch"), Description("Start a cancellable batch using an immutable list of inputs and output paths. Resumes matching completed files using a checkpoint and output hashes. Returns a job ID immediately.")]
    public Task<CallToolResult> StartBatch(RenderInput[] items, CancellationToken cancellationToken, int warmupFrames = 5, bool reShadeShot = true, bool resume = true, int retries = 1,
        string conflictPolicy = "replace", long? expectedRevision = null, RenderInput? expectedInput = null)
        => ExecuteToolAsync("batch_start", new BatchRequest(items, warmupFrames, reShadeShot, resume, retries, conflictPolicy, expectedRevision, expectedInput), cancellationToken);
    [McpServerTool(Name = "preflight_batch", ReadOnly = true), Description("Review every batch item, invalid reason, output directories and conflict policy. Does not render or validate compilation; those checks run before each export.")]
    public Task<JsonElement> PreflightBatch(RenderInput[] items, CancellationToken cancellationToken, int warmupFrames = 5, bool reShadeShot = true, string conflictPolicy = "replace")
        => bridge.CallAsync("batch_preflight", new BatchRequest(items, warmupFrames, reShadeShot, ConflictPolicy: conflictPolicy), cancellationToken);
    [McpServerTool(Name = "resume_batch"), Description("Resume a saved job.json using its frozen presets and output settings, revalidating successful output hashes. Restores the original editor context when finished or cancelled.")]
    public Task<CallToolResult> ResumeBatch(string path, CancellationToken cancellationToken) => ExecuteToolAsync("batch_resume", new { path }, cancellationToken);
    [McpServerTool(Name = "retry_failed_batch"), Description("Retry only failed/invalid items from a saved job.json, preserving other results and the task's frozen settings. Changed effect dependencies require a new task.")]
    public Task<CallToolResult> RetryFailedBatch(string path, CancellationToken cancellationToken) => ExecuteToolAsync("batch_retry", new { path }, cancellationToken);
    [McpServerTool(Name = "get_job", ReadOnly = true), Description("Read the current or latest batch job's progress, state and error.")]
    public Task<JsonElement> GetJob(CancellationToken cancellationToken) => bridge.CallAsync("batch_status", token: cancellationToken);
    [McpServerTool(Name = "cancel_job"), Description("Cancel the active batch. Completed output files and the checkpoint are preserved for resume.")]
    public Task<CallToolResult> CancelJob(CancellationToken cancellationToken) => ExecuteToolAsync("batch_cancel", null, cancellationToken);

    [McpServerTool(Name = "get_operation_status", ReadOnly = true), Description("Read active/latest operation and persistent warning, cancellation or error details. Available while disconnected and during another operation.")]
    public Task<JsonElement> GetOperationStatus(CancellationToken cancellationToken)
        => bridge.CallAsync("get_operation_status", token: cancellationToken);

    [McpServerTool(Name = "cancel_operation"), Description("Request cancellation of the active operation and pending edits. The operation remains busy until native work drains and input restoration completes.")]
    public Task<CallToolResult> CancelOperation(CancellationToken cancellationToken)
        => ExecuteToolAsync("cancel_operation", null, cancellationToken);

    private static JsonElement Decision(object args, string unsavedChanges, string? savePath, long? expectedRevision)
        => AutomationEngine.CopyPresetDecision(AutomationEngine.Json(args), AutomationEngine.Json(new { unsavedChanges, savePath, expectedRevision }));
    private async Task<CallToolResult> ExecuteToolAsync(string method, object? args, CancellationToken token)
    {
        try
        {
            var result = await bridge.CallAsync(method, args, token);
            return new CallToolResult { Content = [new TextContentBlock { Text = result.GetRawText() }], StructuredContent = result };
        }
        catch (Exception error) { return Failure(error, method); }
    }
    private static CallToolResult Failure(Exception error, string method)
    {
        var outcome = error.Data[nameof(OperationOutcome)] as OperationOutcome ?? new OperationOutcome(0, method,
            error is OperationCanceledException ? "cancelled" : "failed", error is OperationCanceledException ? "cancelled" : "connection_or_request_failed", error.Message, "", null, ["details"]);
        var result = AutomationEngine.Json(new { operation = outcome });
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = result.GetRawText() }], StructuredContent = result };
    }
}
