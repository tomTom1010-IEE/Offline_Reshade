using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OfflineReShade.Core;

public sealed record RenderInput(string ColorPath, string DepthPath = "", string DepthFormat = "raw", string DepthProfile = "kks",
    string DepthDownsample = "max2x2", string OutputPath = "", string EffectDir = "", string PresetPath = "", int Width = 0, int Height = 0, bool WatchInput = false);
public sealed record BatchRequest(RenderInput[] Items, int WarmupFrames = 5, bool ReShadeShot = true, bool Resume = true, int Retries = 1);
public sealed record BatchStatus(string Id, string State, int Completed, int Total, string? Current, string? Error, string? Warning = null);

public sealed class AutomationEngine
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly Func<string, object?, CancellationToken, Task<JsonElement>> _call;
    private readonly Func<RenderInput, CancellationToken, Task> _open;
    private readonly Func<RenderInput> _current;
    private readonly Func<string, CancellationToken, Task>? _loadPreset;
    private readonly OperationCoordinator _operations = new();
    private bool _restoreAfterBatch;
    public bool IsBusy => _operations.IsBusy;
    public bool IsExclusive => _operations.IsExclusive;
    public ApplicationActivity Activity => _operations.Activity;
    public BatchStatus? Job { get; private set; }
    public event Action? Changed;
    public AutomationEngine(Func<string, object?, CancellationToken, Task<JsonElement>> call, Func<RenderInput, CancellationToken, Task> open, Func<RenderInput> current,
        Func<string, CancellationToken, Task>? loadPreset = null)
    {
        _call = async (m, a, t) => { var result = await call(m, a, t); if (m == "get_runtime_info") Activity.Observe(result, current()); return result; };
        _open = open; _current = current; _loadPreset = loadPreset;
        _operations.Changed += () => Changed?.Invoke();
        Activity.Changed += () => Changed?.Invoke();
    }

    public async Task<JsonElement> ExecuteAsync(string method, JsonElement args, CancellationToken token = default)
    {
        if (method == "get_diagnostics") return await Diagnostics.CollectAsync(_call, token);
        if (method == "get_operation_status") return Json(Activity.Snapshot);
        if (method == "batch_status") return Json(Job ?? new BatchStatus("", "idle", 0, 0, null, null));
        if (method is "batch_cancel" or "cancel_operation") {
            var cancelling = method == "cancel_operation" ? IsBusy : Job?.State == "running";
            if (cancelling) Cancel();
            return Json(new { cancelling });
        }
        if (method == "get_runtime_info") {
            try {
                var info = await _call(method, args, token);
                var node = JsonNode.Parse(info.GetRawText())!.AsObject(); node["busy"] = IsBusy;
                node["activity"] = JsonSerializer.SerializeToNode(Activity.Snapshot, JsonOptions);
                return JsonSerializer.SerializeToElement(node);
            } catch (IOException) { if (!IsBusy) Activity.Disconnected(); return Json(new { busy = IsBusy, connected = false, reconnecting = IsBusy, loading = IsBusy, width = 0, height = 0, activity = Activity.Snapshot }); }
        }
        if (method is "list_state" or "list_addons" or "list_addon_imgui_capture") return await _call(method, args, token);
        var operation = Activity.Create(method, args, _current());
        if (method == "batch_start")
        {
            // Admission returns a job promptly; its operation remains active through restore.
            var accepted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = StartBatchAsync(operation, accepted, token);
            return await accepted.Task;
        }
        return await _operations.RunAsync(operation, ct => ExecuteMutationAsync(method, args, ct), token);
    }

    private async Task StartBatchAsync(OperationRequest operation, TaskCompletionSource<JsonElement> accepted, CancellationToken token)
    {
        try
        {
            await _operations.RunAsync(operation, async ct =>
            {
                var args = operation.Arguments;
                var request = args.Deserialize<BatchRequest>(JsonOptions) ?? throw new ArgumentException("Missing batch request.");
                if (request.Items == null || request.Items.Length == 0) throw new ArgumentException("Batch needs at least one item.");
                if (request.WarmupFrames is < 0 or > 360000 || request.Retries is < 0 or > 5) throw new ArgumentException("Invalid warmup frames or retry count.");
                var normalized = request.Items.Select(ValidateInput).Select(x => x with { WatchInput = false }).ToArray();
                if (normalized.Select(x => x.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
                    throw new ArgumentException("Batch output paths must be unique.");
                var sources = normalized.SelectMany(x => new[] { x.ColorPath, x.DepthPath }).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (normalized.Any(x => sources.Contains(x.OutputPath))) throw new ArgumentException("Batch output overlaps a source file.");
                _restoreAfterBatch = true;
                Job = new(Guid.NewGuid().ToString("N"), "running", 0, normalized.Length, null, null);
                Changed?.Invoke();
                accepted.TrySetResult(Json(Job));
                await RunBatchAsync(request with { Items = normalized }, ct);
                return Json(Job);
            }, token);
        }
        catch (Exception error) { accepted.TrySetException(error); }
    }

    private async Task<JsonElement> ExecuteMutationAsync(string method, JsonElement args, CancellationToken token)
    {
        if (method == "open_input") {
            var input = ValidateInput(args.Deserialize<RenderInput>(JsonOptions)!);
            if (string.IsNullOrEmpty(input.PresetPath) && !string.IsNullOrEmpty(_current().PresetPath)) input = input with { PresetPath = _current().PresetPath };
            await _open(input, token); return await _call("get_runtime_info", null, token);
        }
        if (method == "set_preset") {
            var preset = Path.GetFullPath(args.GetProperty("path").GetString()!);
            if (!File.Exists(preset)) throw new FileNotFoundException("Preset not found.", preset);
            if (_loadPreset != null) await _loadPreset(preset, token);
            else await _open(_current() with { PresetPath = preset }, token);
            return await _call("get_runtime_info", null, token);
        }
        if (method is "save_preset" or "export_preset") return await SavePresetAsync(method, args, token);
        if (method is "export_image" or "render_preview")
        {
            var input = _current();
            var destination = args.TryGetProperty("path", out var path) ? path.GetString()! : input.OutputPath;
            destination = RuntimeOperations.OutputPath(destination, input.ColorPath, input.DepthPath);
            var frames = args.TryGetProperty("warmupFrames", out var f) ? f.GetInt32() : 5;
            if (frames is < 0 or > 360000) throw new ArgumentException("Invalid warmup frame count.");
            await PrepareExportAsync(frames, token);
            var screenshot = method == "export_image" && (!args.TryGetProperty("reShadeShot", out var shot) || shot.GetBoolean());
            var maxWidth = args.TryGetProperty("maxWidth", out var w) ? w.GetInt32() : 1280;
            var saved = await _call(method == "render_preview" ? "save_preview" : screenshot ? "save_screenshot" : "save_output", new { path = destination, maxWidth }, token);
            if (method != "render_preview") return saved;
            // Return pixels through the broker: packaged WinUI may virtualize AppData,
            // so a path written by its child need not be readable by the MCP process.
            try { return Json(new { mimeType = "image/png", data = Convert.ToBase64String(await File.ReadAllBytesAsync(destination, token)),
                warning = saved.TryGetProperty("warning", out var warning) ? warning.GetString() : null }); }
            finally { if (File.Exists(destination)) File.Delete(destination); }
        }
        if (method == "set_parameters")
        {
            var updates = args.GetProperty("updates").EnumerateArray().Select(x => x.Clone()).ToArray();
            if (updates.Length is 0 or > 256) throw new ArgumentException("Provide 1 to 256 parameter updates.");
            foreach (var update in updates) await _call("set_uniform", update, token);
            return Json(new { updated = updates.Length });
        }
        if (method is not ("set_uniform" or "reset_uniform" or "set_technique_state" or "set_effects_state" or "reorder_techniques" or "reload_effects" or "set_preprocessor_definition"
            or "set_addon_imgui_value" or "open_native_addon_panel" or "set_native_addon_overlay" or "send_addon_overlay_input"))
            throw new ArgumentException("Unknown automation method: " + method);
        return await _call(method, args, token);
    }

    private async Task<JsonElement> SavePresetAsync(string method, JsonElement args, CancellationToken token)
    {
        var runtime = await _call("get_runtime_info", null, token);
        var path = Path.GetFullPath(method == "export_preset" ? args.GetProperty("path").GetString()! : runtime.GetProperty("presetPath").GetString()!);
        if (!path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Preset path must end with .ini.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".OfflineReShade-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            await RuntimeOperations.WaitForFramesAsync(_call, 0, token);
            await _call("export_preset", new { path = temporary }, token);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("The renderer did not write the preset. Check the destination and permissions.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            return Json(new { path });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task RunBatchAsync(BatchRequest request, CancellationToken token)
    {
        var original = _current();
        BatchStatus? finished = null;
        try
        {
            if (string.IsNullOrEmpty(original.ColorPath)) await _open(request.Items[0], token);
            var signature = "output-correctness-v3";
            var checkpointPath = Path.Combine(Path.GetDirectoryName(request.Items[0].OutputPath)!, ".OfflineReShade.batch.json");
            BatchCheckpoint checkpoint = new(signature, new());
            if (request.Resume && File.Exists(checkpointPath))
            {
                try { var saved = JsonSerializer.Deserialize<BatchCheckpoint>(await File.ReadAllTextAsync(checkpointPath, token), JsonOptions); if (saved?.Signature == signature) checkpoint = saved; }
                catch (JsonException) { }
            }
            for (var i = 0; i < request.Items.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = request.Items[i];
                Job = Job! with { Current = item.ColorPath }; Changed?.Invoke();
                await _open(item, token);
                await PrepareExportAsync(0, token);
                // Recompile from disk before fingerprinting: cached GPU code must match hashed files.
                await _call("save_preset", null, token);
                await _call("reload_effects", null, token);
                await PrepareExportAsync(request.WarmupFrames, token);
                var fingerprint = await RenderFingerprintAsync(item, request, token);
                var skip = checkpoint.Done.TryGetValue(item.OutputPath, out var done) && done.Source == fingerprint && File.Exists(item.OutputPath) && done.Hash == await FileHashAsync(item.OutputPath, token);
                if (!skip)
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await PrepareExportAsync(request.WarmupFrames, token);
                            var saved = await _call(request.ReShadeShot ? "save_screenshot" : "save_output", new { path = item.OutputPath }, token);
                            if (saved.TryGetProperty("warning", out var warning) && !string.IsNullOrEmpty(warning.GetString()))
                                Job = Job! with { Warning = "One or more exported images need review: " + warning.GetString() };
                            if (fingerprint != await RenderFingerprintAsync(item, request, token)) throw new IOException("Render inputs or dependencies changed during export; output was not checkpointed.");
                            break;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && attempt < request.Retries) { await Task.Delay(250, token); }
                    }
                    checkpoint.Done[item.OutputPath] = new(fingerprint, await FileHashAsync(item.OutputPath, token));
                    var temp = checkpointPath + ".tmp";
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(checkpoint, JsonOptions), token);
                    File.Move(temp, checkpointPath, true);
                }
                Job = Job! with { Completed = i + 1 }; Changed?.Invoke();
            }
            finished = Job! with { State = "completed", Current = null };
        }
        catch (OperationCanceledException) { finished = Job! with { State = "cancelled" }; }
        catch (Exception ex) { finished = Job! with { State = "failed", Error = ex.Message }; }
        finally
        {
            try { if (_restoreAfterBatch && !string.IsNullOrEmpty(original.ColorPath)) await _open(original, CancellationToken.None); }
            catch (Exception ex) { finished = finished! with { State = "failed", Error = (finished!.Error is null ? "" : finished.Error + "; ") + "Restore input: " + ex.Message }; }
            Job = finished;
            Changed?.Invoke();
        }
    }
    public Task StopAsync() => _operations.StopAsync();
    public Task StopSessionAsync(Func<Task> stop)
    {
        _restoreAfterBatch = false;
        return _operations.StopAsync(async () => {
            var request = Activity.Create("stop_session", Json(new { }), _current());
            Activity.Begin(request);
            try { await stop(); Activity.Disconnected(); Activity.Finish(request, "succeeded", "ok", "Renderer stopped."); }
            catch (Exception error) { Activity.Fail(request, error); throw; }
        });
    }
    public Task<JsonElement> RetryAsync(CancellationToken token = default)
    {
        var request = Activity.RetryRequest ?? throw new InvalidOperationException("There is no operation available to retry.");
        if (request.Method != "open_input" && request.SourcePath != _current().ColorPath)
            throw new InvalidOperationException("The input changed after this operation failed. Start a new operation for the current image.");
        return ExecuteAsync(request.Method, request.Arguments, token);
    }
    public void Cancel(bool restore = true) { if (!restore) _restoreAfterBatch = false; _operations.Cancel(); }
    public static RenderInput ValidateInput(RenderInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var color = Path.GetFullPath(input.ColorPath);
        if (!File.Exists(color)) throw new FileNotFoundException("Color input not found.", color);
        var depth = string.IsNullOrWhiteSpace(input.DepthPath) ? "" : Path.GetFullPath(input.DepthPath);
        if (depth.Length > 0 && !File.Exists(depth)) throw new FileNotFoundException("Depth input not found.", depth);
        if (input.Width is < 0 or > 16384 || input.Height is < 0 or > 16384 || ((input.Width == 0) != (input.Height == 0))) throw new ArgumentException("Width and height must both be zero (source size) or both 1–16384.");
        if ((long)input.Width * input.Height > PreviewGeometry.MaxRenderPixels) throw new ArgumentException("Output exceeds the 64 megapixel resource limit. Reduce output dimensions.");
        if (input.DepthFormat is not ("raw" or "rgba") || input.DepthProfile is not ("kk" or "kks") || input.DepthDownsample is not ("max2x2" or "box")) throw new ArgumentException("Invalid depth format, profile or downsampling mode.");
        return input with { ColorPath = color, DepthPath = depth, OutputPath = RuntimeOperations.OutputPath(input.OutputPath, color, depth) };
    }
    public static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private async Task PrepareExportAsync(int frames, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try {
                await RuntimeOperations.WaitForFramesAsync(_call, frames, token);
                await _call("validate_export", null, token);
                return;
            } catch (Exception ex) when ((ex is InputResizeException or DeviceLostException) && attempt < 3) { await _open(_current(), token); }
        }
    }

    private async Task<string> RenderFingerprintAsync(RenderInput item, BatchRequest request, CancellationToken token)
    {
        var state = await _call("list_state", null, token);
        var runtime = await _call("get_runtime_info", null, token);
        var health = await _call("validate_export", null, token);
        var parts = new List<string> { BuildContract.ProductVersion, JsonSerializer.Serialize(item, JsonOptions), request.WarmupFrames.ToString(), request.ReShadeShot.ToString() };
        foreach (var key in new[] { "techniques", "uniforms", "preprocessorDefinitions" })
            if (state.TryGetProperty(key, out var value)) parts.Add(key + value.GetRawText());
        foreach (var key in new[] { "effectsEnabled", "width", "height", "protocolVersion", "adapterLuid" })
            if (runtime.TryGetProperty(key, out var value)) parts.Add(key + value.GetRawText());
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { item.ColorPath };
        if (item.DepthPath.Length > 0) paths.Add(item.DepthPath);
        if (item.PresetPath.Length > 0) paths.Add(item.PresetPath);
        if (runtime.TryGetProperty("presetPath", out var preset) && !string.IsNullOrEmpty(preset.GetString())) paths.Add(preset.GetString()!);
        if (health.TryGetProperty("dependencies", out var dependencies))
            foreach (var path in dependencies.GetString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) paths.Add(path);
        // Include discovery changes (new/deleted shaders), not only already-loaded files.
        if (Directory.Exists(item.EffectDir))
            foreach (var path in Directory.EnumerateFiles(item.EffectDir, "*", SearchOption.AllDirectories))
                if (new[] { ".fx", ".fxh", ".addonfx", ".addon", ".addon64", ".dll" }.Contains(Path.GetExtension(path).ToLowerInvariant())) paths.Add(path);
        foreach (var path in paths) parts.Add(Path.GetFullPath(path) + ":" + await FileHashAsync(path, token));
        // Add-on private state has no stable serialization contract. Never reuse its checkpoint.
        if (state.TryGetProperty("addons", out var addons) && addons.TryGetProperty("addons", out var list) &&
            list.EnumerateArray().Any(x => x.TryGetProperty("loaded", out var loaded) && loaded.GetBoolean() &&
                ((x.TryGetProperty("file", out var file) && !string.IsNullOrEmpty(file.GetString())) ||
                 (x.TryGetProperty("external", out var external) && external.GetBoolean())))) parts.Add(Job!.Id);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
    }
    private static async Task<string> FileHashAsync(string path, CancellationToken token) { await using var s = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(s, token)); }
    private sealed record BatchCheckpoint(string Signature, Dictionary<string, BatchFile> Done);
    private sealed record BatchFile(string Source, string Hash);
}
