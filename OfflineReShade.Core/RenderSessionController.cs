using System.Text.Json;

namespace OfflineReShade.Core;

public interface IRenderSessionHost : IAsyncDisposable
{
    bool IsConnected { get; }
    string? PersistedPresetPath => null;
    Task StartAsync(RenderInput input, string? snapshot, CancellationToken token);
    Task StopAsync();
    Task<JsonElement> CallAsync(string method, object? args, CancellationToken cancellationToken);
}

// Owns lifecycle decisions for both UI-attached and independent automation sessions.
public class RenderSessionController : IAsyncDisposable
{
    private readonly IRenderSessionHost _host;
    private readonly string _snapshotDirectory;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly object _requests = new();
    private CancellationTokenSource? _activeChange;
    private long _request;
    private bool _disposed;
    private readonly DeviceRecoveryBudget _recovery = new();
    private string? _lastSnapshot;
    private bool _lastEffectsEnabled = true;
    public EditingSession State { get; }
    public RenderInput Current { get; private set; } = new("");
    public bool IsConnected => _host.IsConnected;
    public event Action? RuntimeResetting;

    public RenderSessionController(IRenderSessionHost host, string snapshotDirectory, EditingSession? state = null)
    {
        _host = host; _snapshotDirectory = snapshotDirectory;
        State = state ?? new(new EditorDocument());
    }

    public async Task<JsonElement> CallAsync(string method, object? args = null, CancellationToken cancellationToken = default)
    {
        if (method == "reload_effects") return await ReloadAsync(args, cancellationToken);
        var result = await _host.CallAsync(method, args, cancellationToken);
        if (method == "set_effects_state") _lastEffectsEnabled = AutomationEngine.Json(args).GetProperty("enabled").GetBoolean();
        return result;
    }

    private async Task<JsonElement> ReloadAsync(object? args, CancellationToken token)
    {
        long request;
        lock (_requests)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            request = ++_request; _activeChange?.Cancel();
        }
        await _changes.WaitAsync(token);
        using var change = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            lock (_requests)
            {
                if (_disposed || request != _request) throw new OperationCanceledException("Reload request superseded.");
                _activeChange = change;
            }
            var result = await _host.CallAsync("reload_effects", args, change.Token);
            await RuntimeOperations.WaitForFramesAsync(CallAsync, 0, change.Token);
            change.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            lock (_requests) { if (ReferenceEquals(_activeChange, change)) _activeChange = null; }
            _changes.Release();
        }
    }

    public async Task OpenAsync(RenderInput requested, CancellationToken token = default)
    {
        var input = AutomationEngine.ValidateInput(requested);
        if (string.IsNullOrWhiteSpace(input.EffectDir) || !Directory.Exists(input.EffectDir))
            throw new ArgumentException("EffectDir must be an existing shader directory (it may be empty).");
        if (input.PresetPath.Length > 0 && !File.Exists(input.PresetPath)) throw new FileNotFoundException("Preset not found.", input.PresetPath);
        long request;
        lock (_requests)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            request = ++_request;
            _activeChange?.Cancel();
        }
        await _changes.WaitAsync(token);
        using var change = CancellationTokenSource.CreateLinkedTokenSource(token);
        RenderInput previousInput = Current;
        var snapshot = _lastSnapshot;
        var effectsEnabled = _lastEffectsEnabled;
        var touchedRuntime = false;
        try
        {
            lock (_requests)
            {
                if (_disposed || request != _request) throw new OperationCanceledException("Input request superseded.");
                _activeChange = change;
            }
            var ct = change.Token;
            var previous = IsConnected ? await CallAsync("get_runtime_info", cancellationToken: ct) : default;
            var recovery = Flag(previous, "recoveryRequired");
            if (recovery && !_recovery.TryAcquire(DateTimeOffset.UtcNow)) throw new DeviceLostException("D3D recovery failed repeatedly. Wait 30 seconds and restart the session.");
            if (IsConnected && previousInput.ColorPath.Length > 0)
            {
                if (Flag(previous, "loading") && !recovery)
                {
                    try { await RuntimeOperations.WaitForFramesAsync(CallAsync, 0, ct); }
                    catch (InputResizeException) { /* The existing parameter values can still be captured. */ }
                }
                effectsEnabled = !previous.TryGetProperty("effectsEnabled", out var enabled) || enabled.GetBoolean();
                snapshot = await CaptureAsync(ct);
                _lastEffectsEnabled = effectsEnabled;
                ReplaceSnapshot(snapshot);
            }
            else if (previousInput.ColorPath.Length > 0 && _host.PersistedPresetPath is { } persisted && File.Exists(persisted))
            {
                // The host flushes successful parameter mutations into its private working preset.
                // This also recovers edits after the process died before an RPC snapshot was possible.
                Directory.CreateDirectory(_snapshotDirectory);
                snapshot = Path.Combine(_snapshotDirectory, Guid.NewGuid().ToString("N") + ".ini");
                File.Copy(persisted, snapshot);
                ReplaceSnapshot(snapshot);
            }
            var restart = !IsConnected || recovery || Flag(previous, "restartRequired") ||
                input.EffectDir != previousInput.EffectDir || input.PresetPath != previousInput.PresetPath ||
                input.Width != previousInput.Width || input.Height != previousInput.Height;
            touchedRuntime = true;
            if (!restart)
            {
                var switched = await CallAsync("set_input_paths", input, ct);
                restart = Flag(switched, "restartRequired");
            }
            if (restart)
            {
                await ResetAsync();
                await _host.StartAsync(input, input.PresetPath == previousInput.PresetPath ? snapshot : null, ct);
            }
            await CallAsync("set_effects_state", new { enabled = effectsEnabled }, ct);
            await CallAsync("set_input_watch_enabled", new { enabled = input.WatchInput }, ct);
            // A connected process can still reject its input after startup. Do not publish it yet.
            var info = await CallAsync("get_runtime_info", cancellationToken: ct);
            if (info.TryGetProperty("inputError", out var error) && !string.IsNullOrEmpty(error.GetString())) throw new InvalidOperationException(error.GetString());
            ct.ThrowIfCancellationRequested();
            lock (_requests)
            {
                if (request != _request || _disposed) throw new OperationCanceledException("Input request superseded.");
                Current = input;
                State.AppliedSuccessfully(State.BeginApply(input));
                if (input.PresetPath != previousInput.PresetPath) ClearSnapshot();
            }
        }
        catch (Exception original)
        {
            // Roll back only the current request; Stop/newer Open owns the next lifecycle decision.
            bool restore;
            lock (_requests) restore = request == _request && !_disposed;
            if (touchedRuntime)
            {
                await ResetAsync();
                if (restore && previousInput.ColorPath.Length > 0 && snapshot != null)
                {
                    try
                    {
                        using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await _host.StartAsync(previousInput, snapshot, rollback.Token);
                        await CallAsync("set_effects_state", new { enabled = effectsEnabled }, rollback.Token);
                        await CallAsync("set_input_watch_enabled", new { enabled = previousInput.WatchInput }, rollback.Token);
                        lock (_requests)
                        {
                            if (request == _request && !_disposed) State.AppliedSuccessfully(State.BeginApply(previousInput));
                        }
                    }
                    catch (Exception recoveryError)
                    {
                        await ResetAsync();
                        throw new IOException(original.Message + " Previous session could not be restored: " + recoveryError.Message, original);
                    }
                }
            }
            throw;
        }
        finally
        {
            lock (_requests) { if (ReferenceEquals(_activeChange, change)) _activeChange = null; }
            _changes.Release();
        }
    }

    private static bool Flag(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var flag) && flag.GetBoolean();
    private async Task<string> CaptureAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_snapshotDirectory);
        var path = Path.Combine(_snapshotDirectory, Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            await CallAsync("export_preset", new { path }, token);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("Current effect parameters could not be captured; the session was not replaced.");
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    private void ReplaceSnapshot(string path)
    {
        var previous = _lastSnapshot; _lastSnapshot = path;
        if (previous != null && previous != path && File.Exists(previous)) File.Delete(previous);
    }
    private void ClearSnapshot()
    {
        var previous = _lastSnapshot; _lastSnapshot = null;
        if (previous != null && File.Exists(previous)) File.Delete(previous);
    }
    private async Task ResetAsync()
    {
        RuntimeResetting?.Invoke();
        await _host.StopAsync();
        State.Disconnected();
    }
    public async Task StopAsync()
    {
        lock (_requests) { _request++; _activeChange?.Cancel(); }
        await _changes.WaitAsync();
        try
        {
            try
            {
                if (IsConnected && State.Applied != null)
                {
                    var info = await CallAsync("get_runtime_info");
                    _lastEffectsEnabled = !info.TryGetProperty("effectsEnabled", out var enabled) || enabled.GetBoolean();
                    ReplaceSnapshot(await CaptureAsync(CancellationToken.None));
                }
            }
            finally { await ResetAsync(); }
        }
        finally { _changes.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_requests) { if (_disposed) return; _disposed = true; }
        try { await StopAsync(); }
        finally
        {
            try { await _host.DisposeAsync(); }
            finally { ClearSnapshot(); }
        }
    }
}
