using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OfflineReShade.Core;

public sealed record RenderInput(string ColorPath, string DepthPath = "", string DepthFormat = "raw", string DepthProfile = "kks",
    string DepthDownsample = "max2x2", string OutputPath = "", string EffectDir = "", string PresetPath = "", int Width = 0, int Height = 0, bool WatchInput = false);

public sealed partial class AutomationEngine
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
    private readonly RenderSessionController? _renderSession;
    private readonly Func<Task>? _batchRestored;
    private bool _batchActive;
    public PendingInput? WaitingInput => _listener.Pending;
    public bool IsBusy => _operations.IsBusy || WaitingInput != null || History.TransactionId != null;
    public bool IsExclusive => _operations.IsExclusive || WaitingInput != null;
    public bool IsOperationExclusive => _operations.IsExclusive;
    public ApplicationActivity Activity => _operations.Activity;
    public BatchStatus? Job { get; private set; }
    public event Action? Changed;
    public AutomationEngine(Func<string, object?, CancellationToken, Task<JsonElement>> call, Func<RenderInput, CancellationToken, Task> open, Func<RenderInput> current,
        Func<string, CancellationToken, Task>? loadPreset = null, Func<bool, CancellationToken, Task>? setInputWatch = null,
        Func<Task>? submitPendingEdits = null, Func<RenderInput?>? pendingInput = null,
        Func<string, CancellationToken, Task>? setPresetSource = null,
        RenderSessionController? renderSession = null, Func<Task>? batchRestored = null)
    {
        _call = async (m, a, t) => { var result = await call(m, a, t); if (m == "get_runtime_info") ObserveRuntime(result); return result; };
        _open = open; _current = current; _loadPreset = loadPreset; _setInputWatch = setInputWatch;
        _submitPendingEdits = submitPendingEdits; _pendingInput = pendingInput;
        _setPresetSource = setPresetSource;
        _renderSession = renderSession; _batchRestored = batchRestored;
        _history = new(_call, WaitForEditStateAsync, ct => RuntimeOperations.WaitForFramesAsync(_call, 2, ct));
        _history.Changed += () => Changed?.Invoke();
        _listener = new InputArrivalMonitor((input, token) => _operations.RunAsync(Activity.Create("open_input", Json(input), _current()),
            ct => ExecuteGuardedMutationAsync("open_input", ListenerArguments(input), ct), token), ObserveRuntime, () => _operations.IsBusy);
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
        if (method is not ("set_uniform" or "reset_uniform" or "begin_edit" or "end_edit" or "set_addon_imgui_value" or "send_addon_overlay_input" or "mutate_addon_service")) throw new ArgumentException("Not a submitted edit.");
        return ExecuteCoreAsync(method, args, false, token);
    }
    private async Task<JsonElement> ExecuteCoreAsync(string method, JsonElement args, bool submitPending, CancellationToken token)
    {
        // Display selection neither submits pending edits nor enters preset history.
        if (method == "set_preview_comparison") return await _call(method, args, token);
        if (method == "get_diagnostics") return await Diagnostics.CollectAsync(_call, token);
        if (method == "list_addon_services") return await _operations.RunAsync(Activity.Create(method, args, _current()), ListAddonServicesAsync, token);
        if (method == "get_operation_status") {
            var node = JsonSerializer.SerializeToNode(Activity.Snapshot, JsonOptions)!.AsObject();
            node["waitingInput"] = JsonSerializer.SerializeToNode(WaitingInput, JsonOptions);
            node["appliedInput"] = JsonSerializer.SerializeToNode(Activity.Snapshot.Runtime.Connected ? _current() : null, JsonOptions);
            node["pendingConfiguration"] = PendingConfiguration();
            node["editHistory"] = JsonSerializer.SerializeToNode(History, JsonOptions);
            return Json(node);
        }
        if (method == "batch_status") return Json(Job ?? new BatchStatus("", "idle", 0, 0, null, null));
        if (method == "batch_preflight") return Json(PreflightBatch(args.Deserialize<BatchRequest>(JsonOptions) ?? throw new ArgumentException("Missing batch request.")));
        if (method is "batch_cancel" or "cancel_operation") {
            var cancelling = method == "cancel_operation" ? IsBusy : Job?.State == "running";
            if (cancelling) Cancel();
            if (cancelling) await _cancelTransaction;
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
                node["editHistory"] = JsonSerializer.SerializeToNode(History, JsonOptions);
                return JsonSerializer.SerializeToElement(node);
            } catch (IOException) { if (!_operations.IsBusy) Activity.Disconnected(); return Json(new { busy = IsBusy, connected = false, reconnecting = _operations.IsBusy,
                loading = _operations.IsBusy, width = 0, height = 0, activity = Activity.Snapshot, waitingInput = WaitingInput,
                appliedInput = (RenderInput?)null, pendingConfiguration = PendingConfiguration(), editHistory = History }); }
        }
        if (method == "get_edit_history" && (_operations.IsBusy || WaitingInput != null || _current().ColorPath.Length == 0)) return Json(History);
        if (method == "get_edit_history") submitPending = false; // A read observes applied state; it does not commit unfinished UI text.
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
        if (method is "batch_start" or "batch_resume" or "batch_retry")
        {
            // Admission returns a job promptly; its operation remains active through restore.
            var accepted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = StartBatchAsync(operation, accepted, edits, token);
            return await accepted.Task;
        }
        return await _operations.RunAsync(operation, async ct => { await edits.WaitAsync(ct); return await ExecuteGuardedMutationAsync(method, args, ct); }, token);
    }

    private JsonNode? PendingConfiguration()
    {
        try { return JsonSerializer.SerializeToNode(new { input = _pendingInput?.Invoke(), error = (string?)null }, JsonOptions); }
        catch (ArgumentException error) { return JsonSerializer.SerializeToNode(new { input = (RenderInput?)null, error = error.Message }, JsonOptions); }
    }

    private async Task<JsonElement> ExecuteMutationAsync(string method, JsonElement args, CancellationToken token)
    {
        if (method == "mutate_addon_service") return await MutateAddonServiceAsync(args, token);
        if (method == "press_uniform")
        {
            var id = args.GetProperty("id").GetString();
            var state = await _call("list_state", null, token);
            var control = state.GetProperty("uniforms").EnumerateArray().FirstOrDefault(u => u.GetProperty("id").GetString() == id);
            if (control.ValueKind == JsonValueKind.Undefined || !control.TryGetProperty("uiType", out var uiType) || uiType.GetString() != "button" || control.GetProperty("type").GetString() != "bool") throw new ArgumentException("Not a boolean action button.");
            try {
                await _call("set_uniform", new { id, value = true }, token);
                var before = (await _call("get_runtime_info", null, token)).GetProperty("frameCount").GetInt64();
                for (var i = 0; i < 100; i++) {
                    await Task.Delay(20, token);
                    if ((await _call("get_runtime_info", null, token)).GetProperty("frameCount").GetInt64() > before) return Json(new { status = "applied" });
                }
                throw new TimeoutException("Renderer did not process the button action.");
            } finally { await _call("set_uniform", new { id, value = false }, CancellationToken.None); }
        }
        if (method == "set_addon_imgui_value")
        {
            var id = args.GetProperty("id").GetString();
            try {
            await _call(method, args, token);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(20, token);
                var snapshot = await _call("list_addon_imgui_capture", null, token);
                if (!snapshot.TryGetProperty("receipts", out var receipts)) throw new InvalidOperationException("Renderer does not support confirmed Add-on writes.");
                foreach (var receipt in receipts.EnumerateArray())
                {
                    if (receipt.GetProperty("id").GetString() != id) continue;
                    var status = receipt.GetProperty("status").GetString();
                    if (status == "applied") return receipt.Clone();
                    if (status is "expired" or "rejected") throw new InvalidOperationException("Add-on edit " + status + ". Refresh the controls and retry.");
                }
            }
            throw new TimeoutException("Add-on edit was queued but application was not confirmed.");
            } finally { await _call("cancel_addon_imgui_value", new { id }, CancellationToken.None); }
        }
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
        if (method is "save_preset" or "save_preset_as" or "export_preset") return await SavePresetAsync(method, args, token);
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
            if (request.ApplyInput != null) await ExecuteGuardedMutationAsync("open_input", CopyPresetDecision(Json(intended), args), token);
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
        _history.RequireAccess(null);
        var state = await _history.SynchronizeAsync(token);
        if (!History.Available) throw new InvalidOperationException("The current FX state is unverified. Reload a known preset before saving.");
        var source = method == "save_preset" ? _current().PresetPath : args.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("This session has no source preset. Use Save As and choose an INI file.");
        var path = Path.GetFullPath(source);
        if (method == "save_preset_as" && _setPresetSource == null) throw new InvalidOperationException("This session cannot change its source preset.");
        if (!path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Preset path must end with .ini.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".OfflineReShade-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            await _call("export_preset", new { path = temporary }, token);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("The renderer did not write the preset. Check the destination and permissions.");
            await PresetMetadata.WriteEffectsEnabledAsync(temporary, state.EffectsEnabled, token);
            if (!state.SameAs(EffectSnapshot.Read(await _call("get_edit_state", null, token))))
                throw new IOException("FX state changed during preset export. Review it and save again.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            // Publication is the commit point. Cancellation afterwards must not pretend the file was not saved.
            if (method == "save_preset_as") await _setPresetSource!(path, CancellationToken.None);
            if (method != "export_preset") _history.MarkSaved();
            return Json(new { path });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task StopAsync() { _listener.Cancel(); await _operations.StopAsync(() => { _history.CloseTransaction(); return Task.CompletedTask; }); await _listener.StopAsync(); }
    public async Task StopSessionAsync(Func<Task> stop, JsonElement? unsavedDecision = null)
    {
        _listener.Cancel();
        await _operations.StopAsync(async () => {
            _history.CloseTransaction();
            if (unsavedDecision.HasValue) await CheckUnsavedAsync(unsavedDecision.Value, CancellationToken.None);
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
        // Even stop/close must restore the user's context before its save/discard decision.
        var pending = WaitingInput;
        if (pending != null && !_operations.IsBusy) Activity.Finish(Activity.Create("listen_input", Json(pending.Input), _current()), "cancelled", "cancelled", "Stopped waiting for game output.");
        _listener.Cancel(); _operations.Cancel();
        _cancelTransaction = _operations.StopAsync(() => { _history.CloseTransaction(); return Task.CompletedTask; });
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
        return input with { ColorPath = color, DepthPath = depth, OutputPath = RuntimeOperations.OutputPath(input.OutputPath, color, depth),
            EffectDir = string.IsNullOrWhiteSpace(input.EffectDir) ? "" : Path.GetFullPath(input.EffectDir),
            PresetPath = string.IsNullOrWhiteSpace(input.PresetPath) ? "" : Path.GetFullPath(input.PresetPath) };
    }
    public static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private async Task PrepareExportAsync(int frames, CancellationToken token)
    {
        if (_martyRecoveryError != null) throw new InvalidOperationException(_martyRecoveryError);
        for (var attempt = 0; ; attempt++)
        {
            try {
                await RuntimeOperations.WaitForFramesAsync(_call, frames, token);
                await _call("validate_export", null, token);
                return;
            } catch (Exception ex) when ((ex is InputResizeException or DeviceLostException) && attempt < 3) {
                if (_batchActive) await _renderSession!.OpenAsync(_current(), token);
                else await _open(_current(), token);
            }
        }
    }

}
