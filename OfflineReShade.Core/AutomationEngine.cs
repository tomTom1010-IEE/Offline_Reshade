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
    private readonly Func<bool, CancellationToken, Task>? _setInputWatch;
    private readonly Func<Task>? _submitPendingEdits;
    private readonly Func<RenderInput?>? _pendingInput;
    private readonly OperationCoordinator _operations = new();
    private readonly InputArrivalMonitor _listener;
    private bool _restoreAfterBatch;
    public PendingInput? WaitingInput => _listener.Pending;
    public bool IsBusy => _operations.IsBusy || WaitingInput != null;
    public bool IsExclusive => _operations.IsExclusive || WaitingInput != null;
    public bool IsOperationExclusive => _operations.IsExclusive;
    public ApplicationActivity Activity => _operations.Activity;
    public BatchStatus? Job { get; private set; }
    public event Action? Changed;
    public AutomationEngine(Func<string, object?, CancellationToken, Task<JsonElement>> call, Func<RenderInput, CancellationToken, Task> open, Func<RenderInput> current,
        Func<string, CancellationToken, Task>? loadPreset = null, Func<bool, CancellationToken, Task>? setInputWatch = null,
        Func<Task>? submitPendingEdits = null, Func<RenderInput?>? pendingInput = null)
    {
        _call = async (m, a, t) => { var result = await call(m, a, t); if (m == "get_runtime_info") ObserveRuntime(result); return result; };
        _open = open; _current = current; _loadPreset = loadPreset; _setInputWatch = setInputWatch;
        _submitPendingEdits = submitPendingEdits; _pendingInput = pendingInput;
        _listener = new InputArrivalMonitor((input, token) => _operations.RunAsync(Activity.Create("open_input", Json(input), _current()),
            ct => ExecuteMutationAsync("open_input", Json(input), ct), token), ObserveRuntime, () => _operations.IsBusy);
        _listener.Changed += () => { if (WaitingInput is { } waiting) Activity.WaitForInput(waiting.Input.ColorPath, waiting.Error); else Activity.ClearWaiting(); Changed?.Invoke(); };
        _operations.Changed += () => Changed?.Invoke();
        Activity.Changed += () => Changed?.Invoke();
    }
    public void ObserveRuntime(JsonElement info)
    {
        var input = _current();
        Activity.Observe(info, input);
        if (WaitingInput != null) return;
        if (input.WatchInput && (!File.Exists(input.ColorPath) || (input.DepthPath.Length > 0 && !File.Exists(input.DepthPath))))
            Activity.WaitForInput(input.ColorPath);
        else Activity.ClearWaiting();
    }

    public Task<JsonElement> ExecuteAsync(string method, JsonElement args, CancellationToken token = default) => ExecuteCoreAsync(method, args, true, token);
    // Only the pending-edit owner uses this entry to avoid recursively flushing itself.
    public Task<JsonElement> ExecuteSubmittedEditAsync(string method, JsonElement args, CancellationToken token = default)
    {
        if (method is not ("set_uniform" or "set_addon_imgui_value" or "send_addon_overlay_input")) throw new ArgumentException("Not a submitted edit.");
        return ExecuteCoreAsync(method, args, false, token);
    }
    private async Task<JsonElement> ExecuteCoreAsync(string method, JsonElement args, bool submitPending, CancellationToken token)
    {
        if (method == "get_diagnostics") return await Diagnostics.CollectAsync(_call, token);
        if (method == "get_operation_status") {
            var node = JsonSerializer.SerializeToNode(Activity.Snapshot, JsonOptions)!.AsObject();
            node["waitingInput"] = JsonSerializer.SerializeToNode(WaitingInput, JsonOptions);
            node["appliedInput"] = JsonSerializer.SerializeToNode(Activity.Snapshot.Runtime.Connected ? _current() : null, JsonOptions);
            node["pendingConfiguration"] = PendingConfiguration();
            return Json(node);
        }
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
                node["waitingInput"] = JsonSerializer.SerializeToNode(WaitingInput, JsonOptions);
                node["appliedInput"] = JsonSerializer.SerializeToNode(_current(), JsonOptions);
                node["pendingConfiguration"] = PendingConfiguration();
                return JsonSerializer.SerializeToElement(node);
            } catch (IOException) { if (!_operations.IsBusy) Activity.Disconnected(); return Json(new { busy = IsBusy, connected = false, reconnecting = _operations.IsBusy,
                loading = _operations.IsBusy, width = 0, height = 0, activity = Activity.Snapshot, waitingInput = WaitingInput,
                appliedInput = (RenderInput?)null, pendingConfiguration = PendingConfiguration() }); }
        }
        if (method is "list_state" or "list_addons" or "list_addon_imgui_capture") return await _call(method, args, token);
        if (method is "open_input" or "listen_input" or "stop_listening") { token.ThrowIfCancellationRequested(); await _listener.StopAsync(); }
        var operation = Activity.Create(method, args, _current());
        if (WaitingInput != null)
        {
            var error = new InvalidOperationException("Waiting for the requested game input. Stop listening or wait for a valid pair before editing or exporting.");
            Activity.Fail(operation, error, rejected: true); throw error;
        }
        // The UI submits its debounced edits synchronously before the next mutation's admission.
        // The returned task also propagates failed submissions instead of exporting old values.
        Task edits = Task.CompletedTask;
        if (submitPending && !_operations.IsExclusive && _submitPendingEdits != null)
        {
            try { edits = _submitPendingEdits(); } catch (Exception error) { edits = Task.FromException(error); }
        }
        if (method == "batch_start")
        {
            // Admission returns a job promptly; its operation remains active through restore.
            var accepted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = StartBatchAsync(operation, accepted, edits, token);
            return await accepted.Task;
        }
        return await _operations.RunAsync(operation, async ct => { await edits.WaitAsync(ct); return await ExecuteMutationAsync(method, args, ct); }, token);
    }

    private JsonNode? PendingConfiguration()
    {
        try { return JsonSerializer.SerializeToNode(new { input = _pendingInput?.Invoke(), error = (string?)null }, JsonOptions); }
        catch (ArgumentException error) { return JsonSerializer.SerializeToNode(new { input = (RenderInput?)null, error = error.Message }, JsonOptions); }
    }

    private async Task StartBatchAsync(OperationRequest operation, TaskCompletionSource<JsonElement> accepted, Task edits, CancellationToken token)
    {
        try
        {
            await _operations.RunAsync(operation, async ct =>
            {
                await edits.WaitAsync(ct);
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
        if (method == "listen_input")
        {
            var input = NormalizeInput(args.Deserialize<RenderInput>(JsonOptions)!, requireFiles: false) with { WatchInput = true };
            if (!Directory.Exists(input.EffectDir)) throw new DirectoryNotFoundException("Select an existing effects directory before listening.");
            if (input.PresetPath.Length > 0 && !File.Exists(input.PresetPath)) throw new FileNotFoundException("Preset not found.", input.PresetPath);
            _listener.Start(input);
            return Json(new { listening = true, waiting = true, input });
        }
        if (method == "stop_listening")
        {
            if (_setInputWatch != null) await _setInputWatch(false, token);
            else if (Activity.Snapshot.Runtime.Connected) await _call("set_input_watch_enabled", new { enabled = false }, token);
            Activity.ClearWaiting();
            return Json(new { listening = false });
        }
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
        if (method == "export_image")
        {
            var request = args.Deserialize<ImageExportRequest>(JsonOptions) ?? throw new ArgumentException("Missing export request.");
            ImageExport.Validate(request);
            if (request.ExpectedInput != null && request.ExpectedInput != _current())
                throw new InvalidOperationException("The applied input changed. Review the current image before exporting.");
            if (request.ApplyInput == null && _pendingInput?.Invoke() != null)
                throw new InvalidOperationException("Configuration changes are pending. Apply them before exporting, or provide applyInput explicitly.");
            var intended = request.ApplyInput == null ? _current() : ValidateInput(request.ApplyInput);
            var destination = RuntimeOperations.OutputPath(request.Path, intended.ColorPath, intended.DepthPath);
            if (_current().ColorPath.Length > 0) RuntimeOperations.OutputPath(destination, _current().ColorPath, _current().DepthPath);
            if (request.ConflictPolicy == "fail" && File.Exists(destination))
                throw new IOException("The output file already exists. Choose another path or conflict policy.");
            if (request.ApplyInput != null) await ExecuteMutationAsync("open_input", Json(intended), token);
            await PrepareExportAsync(request.WarmupFrames, token);
            return await ImageExport.WriteAsync(destination, request.ConflictPolicy,
                (temporary, ct) => _call(request.ReShadeShot ? "save_screenshot" : "save_output", new { path = temporary }, ct), token);
        }
        if (method == "render_preview")
        {
            var input = _current();
            var destination = args.TryGetProperty("path", out var path) ? path.GetString()! : input.OutputPath;
            destination = RuntimeOperations.OutputPath(destination, input.ColorPath, input.DepthPath);
            var frames = args.TryGetProperty("warmupFrames", out var f) ? f.GetInt32() : 5;
            if (frames is < 0 or > 360000) throw new ArgumentException("Invalid warmup frame count.");
            await PrepareExportAsync(frames, token);
            var maxWidth = args.TryGetProperty("maxWidth", out var w) ? w.GetInt32() : 1280;
            var saved = await _call("save_preview", new { path = destination, maxWidth }, token);
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
    public async Task StopAsync() { _listener.Cancel(); await _operations.StopAsync(); await _listener.StopAsync(); }
    public async Task StopSessionAsync(Func<Task> stop)
    {
        _restoreAfterBatch = false;
        _listener.Cancel();
        await _operations.StopAsync(async () => {
            await _listener.StopAsync();
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
    public void Cancel(bool restore = true)
    {
        if (!restore) _restoreAfterBatch = false;
        var pending = WaitingInput;
        if (pending != null && !_operations.IsBusy) Activity.Finish(Activity.Create("listen_input", Json(pending.Input), _current()), "cancelled", "cancelled", "Stopped waiting for game output.");
        _listener.Cancel(); _operations.Cancel();
    }
    public static RenderInput ValidateInput(RenderInput input) => NormalizeInput(input, requireFiles: true);
    private static RenderInput NormalizeInput(RenderInput input, bool requireFiles)
    {
        ArgumentNullException.ThrowIfNull(input);
        var color = Path.GetFullPath(input.ColorPath);
        if (requireFiles && !File.Exists(color)) throw new FileNotFoundException("Color input not found.", color);
        var depth = string.IsNullOrWhiteSpace(input.DepthPath) ? "" : Path.GetFullPath(input.DepthPath);
        if (requireFiles && depth.Length > 0 && !File.Exists(depth)) throw new FileNotFoundException("Depth input not found.", depth);
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
