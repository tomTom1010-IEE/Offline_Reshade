using System.Text.Json;
using OfflineReShade.Core;

namespace OfflineReShade.Mcp;

public sealed class McpBridge : IAsyncDisposable
{
    private readonly string _nativePath;
    private NativeSession? _native;
    private AutomationEngine? _engine;
    private RuntimeConnection? _attached;
    private SessionDescriptor? _session;
    private string? _sessionId;
    public string Mode => _attached == null ? "independent" : "attached";
    public McpBridge(string nativePath) => _nativePath = nativePath;
    public async Task AttachAsync(string id, CancellationToken token)
    {
        if (_engine?.IsBusy == true) throw new InvalidOperationException("Cancel and finish the active job before attaching another session.");
        var descriptor = AutomationBroker.Discover().SingleOrDefault(x => x.Id == id);
        if (descriptor == null && !Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid session ID. Call list_sessions or copy the ID from WinUI Settings.");
        var connection = new RuntimeConnection();
        try {
            await connection.ConnectAsync(descriptor?.PipeName ?? "OfflineReShade.Automation." + id, token);
            await Compatibility.VerifyAsync(connection, "automation", token);
        }
        catch { connection.Dispose(); throw; }
        await DisposeOwnedAsync();
        _attached?.Dispose(); _attached = connection; _session = descriptor; _sessionId = id;
    }
    public async Task<JsonElement> CallAsync(string method, object? arguments = null, CancellationToken token = default)
    {
        var json = AutomationEngine.Json(arguments ?? new { });
        if (_attached != null)
        {
            if (method is "cancel_operation" or "batch_cancel" or "get_operation_status" or "batch_status")
            {
                // A second connection allows cancellation/status while the first awaits a mutation.
                using var control = new RuntimeConnection();
                await control.ConnectAsync(_session?.PipeName ?? "OfflineReShade.Automation." + _sessionId, token);
                await Compatibility.VerifyAsync(control, "automation", token);
                return await control.CallAsync(method, json, token);
            }
            return await _attached.CallAsync(method, json, token);
        }
        _native ??= new NativeSession(_nativePath);
        _engine ??= new AutomationEngine(_native.CallAsync, _native.OpenAsync, () => _native.Current, _native.LoadPresetAsync);
        return await _engine.ExecuteAsync(method, json, token);
    }
    public object ConnectionInfo() => new { mode = Mode, session = _sessionId, name = _session?.Name };
    private async Task DisposeOwnedAsync() { if (_engine != null) await _engine.StopAsync(); if (_native != null) await _native.DisposeAsync(); _native = null; _engine = null; }
    public async ValueTask DisposeAsync() { await DisposeOwnedAsync(); _attached?.Dispose(); }
}
