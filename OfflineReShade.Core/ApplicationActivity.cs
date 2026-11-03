using System.Text.Json;

namespace OfflineReShade.Core;

public sealed record OperationRequest(long Id, string Method, JsonElement Arguments, string SourcePath, string? TargetPath);
public sealed record OperationOutcome(long Id, string Operation, string Status, string Code, string Message,
    string SourcePath, string? TargetPath, string[] Actions);
public sealed record RuntimeObservation(bool Connected = false, bool HasFrame = false, bool Compiling = false,
    bool WaitingForInput = false, string InputPath = "", string? Error = null, string? PreviewWarning = null);
public sealed record ActivitySnapshot(RuntimeObservation Runtime, OperationOutcome? Active, OperationOutcome? Last, OperationOutcome? Notice)
{
    public bool IsExclusive => Active != null && ApplicationActivity.IsExclusive(Active.Operation);
    public bool ShowEmpty => !Runtime.HasFrame;
    public string StatusText => IsExclusive ? ApplicationActivity.Label(Active!.Operation) : Runtime.Error != null ? "Input or renderer needs attention"
        : Runtime.WaitingForInput ? "Waiting for game output" : Runtime.Compiling ? "Compiling effects"
        : Runtime.PreviewWarning != null ? "Preview needs attention" : Runtime.HasFrame ? "Preview ready" : Runtime.Connected ? "Preparing preview" : "No image opened";
    public string EmptyTitle => Runtime.Error != null ? "Input unavailable" : Runtime.WaitingForInput ? "Waiting for game output" : Runtime.Connected || Active?.Operation == "open_input" ? "Preparing preview" : "Open an image to begin";
    public string EmptyMessage => Runtime.Error ?? (Runtime.WaitingForInput ? "The selected image and depth files have not arrived yet."
        : Runtime.Connected ? "The renderer is loading the image and effects." : "Choose a color image and associate its depth file, or listen for game exports.");
}

public sealed class ApplicationActivity
{
    private readonly object _gate = new();
    private long _sequence;
    private RuntimeObservation _runtime = new();
    private string? _waitingPath, _waitingError;
    private OperationOutcome? _active, _last, _notice;
    public event Action? Changed;
    public ActivitySnapshot Snapshot { get { lock (_gate) return new(_waitingPath == null ? _runtime : _runtime with {
        WaitingForInput = true, HasFrame = false, InputPath = _waitingPath, Error = _waitingError }, _active, _last, _notice); } }
    public OperationRequest? RetryRequest { get; private set; }
    public static bool CanRetry(string method) => method is "open_input" or "set_preset" or "reload_effects" or "export_image" or "save_preset" or "save_preset_as" or "export_preset";
    public static bool IsExclusive(string method) => CanRetry(method) || method is "listen_input" or "stop_listening" or "render_preview" or "batch_start" or "batch_resume" or "batch_retry" or "stop_session" or "set_preprocessor_definition" or "undo" or "redo" or "check_unsaved_changes";
    public static string Label(string method) => method switch
    {
        "open_input" => "Opening input", "set_preset" => "Loading preset", "reload_effects" => "Reloading effects",
        "listen_input" => "Starting input listener", "stop_listening" => "Stopping input listener",
        "export_image" => "Exporting image", "render_preview" => "Rendering preview", "batch_start" or "batch_resume" or "batch_retry" => "Exporting batch",
        "save_preset" or "save_preset_as" or "export_preset" => "Saving preset", "stop_session" or "close_session" => "Stopping renderer",
        "undo" => "Undoing edit", "redo" => "Redoing edit",
        "save_settings" => "Saving settings", "load_settings" => "Loading settings", "preview_transport" => "Displaying preview",
        "recover_session" => "Recovering renderer", "runtime_exit" or "observe_runtime" => "Monitoring renderer", _ => "Updating parameters"
    };
    public OperationRequest Create(string method, JsonElement args, RenderInput input)
    {
        string? Text(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var source = Text("colorPath") ?? input.ColorPath;
        if (method == "export_image" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("applyInput", out var applied) &&
            applied.ValueKind == JsonValueKind.Object && applied.TryGetProperty("colorPath", out var color) && color.ValueKind == JsonValueKind.String)
            source = color.GetString() ?? source;
        return new(Interlocked.Increment(ref _sequence), method, args.Clone(), source, Text("path") ?? Text("outputPath"));
    }
    public void Begin(OperationRequest request)
    {
        lock (_gate) _active = new(request.Id, request.Method, "running", "", Label(request.Method), request.SourcePath, request.TargetPath, ["cancel"]);
        Changed?.Invoke();
    }
    public OperationOutcome Finish(OperationRequest request, string status, string code, string message)
    {
        var actions = new List<string>();
        if (status is "failed" or "rejected" or "warning") actions.Add("details");
        if (actions.Count > 0 && (!string.IsNullOrEmpty(request.TargetPath) || request.SourcePath.Length > 0)) actions.Add("locate");
        if ((status is "failed" or "rejected") && CanRetry(request.Method)) actions.Add("retry");
        var result = new OperationOutcome(request.Id, request.Method, status, code, message, request.SourcePath, request.TargetPath, actions.ToArray());
        lock (_gate)
        {
            if (_active?.Id == request.Id) _active = null;
            _last = result;
            if (status is "failed" or "rejected" or "warning" or "cancelled") { _notice = result; RetryRequest = actions.Contains("retry") ? request : null; }
            else if (_notice?.Operation == request.Method) { _notice = null; RetryRequest = null; }
        }
        Changed?.Invoke(); return result;
    }
    public OperationOutcome Fail(OperationRequest request, Exception error, bool rejected = false)
    {
        var code = error switch { OperationCanceledException => "cancelled", DeviceLostException => "device_lost", FileNotFoundException => "file_missing",
            UnauthorizedAccessException => "access_denied", ArgumentException or JsonException or KeyNotFoundException => "invalid_input", TimeoutException => "timeout",
            IOException => "io_failure", _ => "operation_failed" };
        if (error is FileNotFoundException missing && missing.FileName != null) request = request with { TargetPath = missing.FileName };
        var result = Finish(request, error is OperationCanceledException ? "cancelled" : rejected ? "rejected" : "failed", rejected ? "busy" : code, error.Message);
        error.Data[nameof(OperationOutcome)] = result; return result;
    }
    public void Report(Exception error, string method, RenderInput input)
    {
        if (error.Data[nameof(OperationOutcome)] is OperationOutcome outcome)
        {
            lock (_gate) { if (_notice == outcome || _notice?.Id > outcome.Id) return; _notice = outcome; }
            Changed?.Invoke();
        }
        else Fail(Create(method, AutomationEngine.Json(new { }), input), error);
    }
    public void Dismiss() { lock (_gate) { _notice = null; RetryRequest = null; } Changed?.Invoke(); }
    public void Disconnected()
    {
        lock (_gate) _runtime = new(InputPath: _runtime.InputPath);
        Changed?.Invoke();
    }
    public void WaitForInput(string path, string? error = null)
    {
        lock (_gate) { if (_waitingPath == path && _waitingError == error) return; _waitingPath = path; _waitingError = error; }
        Changed?.Invoke();
    }
    public void ClearWaiting() { lock (_gate) { if (_waitingPath == null) return; _waitingPath = null; _waitingError = null; } Changed?.Invoke(); }
    public void Observe(JsonElement info, RenderInput input)
    {
        if (info.ValueKind != JsonValueKind.Object) return;
        bool Flag(string key) => info.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
        string? Text(string key) => info.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()) ? v.GetString() : null;
        var error = Text("inputError") ?? (Flag("recoveryRequired") ? Text("deviceError") ?? "Renderer recovery required." : null);
        var frame = info.TryGetProperty("frameCount", out var f) && f.TryGetUInt64(out var frames) && frames > 0 && error == null;
        var next = new RuntimeObservation(true, frame, Flag("loading"), false, input.ColorPath, error, Text("previewError"));
        lock (_gate) { if (_runtime == next) return; _runtime = next; }
        Changed?.Invoke();
    }
}
