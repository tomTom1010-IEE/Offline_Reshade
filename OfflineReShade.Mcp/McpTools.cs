using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OfflineReShade.Core;

namespace OfflineReShade.Mcp;

[McpServerToolType]
public sealed class McpTools(McpBridge bridge)
{
    [McpServerTool(Name = "list_sessions", ReadOnly = true), Description("List running local WinUI automation sessions. Select an explicit session ID to attach.")]
    public SessionDescriptor[] ListSessions() => AutomationBroker.Discover();

    [McpServerTool(Name = "attach_session"), Description("Attach to the current preview in a running WinUI session. UI and MCP share the same parameters and batch lock.")]
    public async Task<object> AttachSession(string sessionId, CancellationToken cancellationToken) { await bridge.AttachAsync(sessionId, cancellationToken); return bridge.ConnectionInfo(); }

    [McpServerTool(Name = "open_input"), Description("Open a color/depth pair. In independent mode this launches the native host. Use absolute paths. Zero width and height preserve each source image's resolution.")]
    public Task<CallToolResult> OpenInput(string colorPath, string outputPath, string effectDir, CancellationToken cancellationToken,
        string depthPath = "", string depthFormat = "raw", string depthProfile = "kks", string depthDownsample = "max2x2", string presetPath = "", int width = 0, int height = 0, bool watchInput = false)
        => ExecuteToolAsync("open_input", new RenderInput(colorPath, depthPath, depthFormat, depthProfile, depthDownsample, outputPath, effectDir, presetPath, width, height, watchInput), cancellationToken);

    [McpServerTool(Name = "get_status", ReadOnly = true), Description("Read resolution, loading, frame count, input generation, rendering adapter, preview transport and device recovery diagnostics.")]
    public Task<JsonElement> GetStatus(CancellationToken cancellationToken) => bridge.CallAsync("get_runtime_info", token: cancellationToken);

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
    public Task<CallToolResult> SetParameters(JsonElement[] updates, CancellationToken cancellationToken) => ExecuteToolAsync("set_parameters", new { updates }, cancellationToken);

    [McpServerTool(Name = "set_technique"), Description("Enable or disable a technique by its effect.fx::technique ID.")]
    public Task<CallToolResult> SetTechnique(string id, bool enabled, CancellationToken cancellationToken) => ExecuteToolAsync("set_technique_state", new { id, enabled }, cancellationToken);

    [McpServerTool(Name = "set_effects_enabled"), Description("Enable or disable all ReShade effects for before/after comparison.")]
    public Task<CallToolResult> SetEffectsEnabled(bool enabled, CancellationToken cancellationToken) => ExecuteToolAsync("set_effects_state", new { enabled }, cancellationToken);

    [McpServerTool(Name = "reorder_techniques"), Description("Set technique execution order using the complete ordered list of technique IDs returned by list_effects.")]
    public Task<CallToolResult> ReorderTechniques(string[] ids, CancellationToken cancellationToken) => ExecuteToolAsync("reorder_techniques", new { ids }, cancellationToken);

    [McpServerTool(Name = "load_preset"), Description("Load an existing preset INI. Query loading status and wait before exporting.")]
    public Task<CallToolResult> LoadPreset(string path, CancellationToken cancellationToken)
        => ExecuteToolAsync("set_preset", new { path }, cancellationToken);
    [McpServerTool(Name = "save_preset"), Description("Export current parameters and technique order to the given absolute INI path.")]
    public Task<CallToolResult> SavePreset(string path, CancellationToken cancellationToken)
        => ExecuteToolAsync("export_preset", new { path }, cancellationToken);

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
    [McpServerTool(Name = "export_image"), Description("Export the current image at full runtime resolution with GUI excluded. Waits for compilation and the requested real rendered frame count. Prevents overwriting input files.")]
    public Task<CallToolResult> ExportImage(string path, CancellationToken cancellationToken, int warmupFrames = 5, bool reShadeShot = true)
        => ExecuteToolAsync("export_image", new { path, warmupFrames, reShadeShot }, cancellationToken);

    [McpServerTool(Name = "start_batch"), Description("Start a cancellable batch using an immutable list of inputs and output paths. Resumes matching completed files using a checkpoint and output hashes. Returns a job ID immediately.")]
    public Task<CallToolResult> StartBatch(RenderInput[] items, CancellationToken cancellationToken, int warmupFrames = 5, bool reShadeShot = true, bool resume = true, int retries = 1)
        => ExecuteToolAsync("batch_start", new BatchRequest(items, warmupFrames, reShadeShot, resume, retries), cancellationToken);
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
