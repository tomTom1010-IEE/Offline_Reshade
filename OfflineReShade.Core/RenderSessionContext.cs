namespace OfflineReShade.Core;

// Owned by the session that captured it. The private preset is independent of both
// the user's source INI and the host's mutable working INI.
public sealed class RenderSessionContext : IDisposable
{
    internal RenderSessionController Owner { get; }
    public RenderInput Input { get; }
    public EditorDocument Draft { get; }
    public bool Connected { get; }
    public string? PresetSnapshot { get; }
    public bool EffectsEnabled { get; }
    public bool PreviewOriginal { get; }
    internal bool Disposed { get; private set; }
    internal RenderSessionContext(RenderSessionController owner, RenderInput input, EditorDocument draft,
        bool connected, string? preset, bool enabled, bool original)
    { Owner = owner; Input = input; Draft = draft; Connected = connected; PresetSnapshot = preset; EffectsEnabled = enabled; PreviewOriginal = original; }
    public void Dispose()
    {
        if (Disposed) return;
        if (PresetSnapshot != null && File.Exists(PresetSnapshot)) File.Delete(PresetSnapshot);
        Disposed = true;
    }
}

public partial class RenderSessionController
{
    public async Task<RenderSessionContext> CaptureContextAsync(CancellationToken token = default)
    {
        await _changes.WaitAsync(token);
        string? snapshot = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var enabled = _lastEffectsEnabled;
            var original = false;
            var connected = IsConnected;
            if (connected)
            {
                var info = await CallAsync("get_runtime_info", cancellationToken: token);
                enabled = !info.TryGetProperty("effectsEnabled", out var flag) || flag.GetBoolean();
                original = Flag(info, "previewOriginal");
                snapshot = await CaptureAsync(token);
            }
            else
            {
                var source = _lastSnapshot ?? (Current.ColorPath.Length > 0 ? _host.PersistedPresetPath : null);
                if (source != null && File.Exists(source))
                {
                    Directory.CreateDirectory(_snapshotDirectory);
                    snapshot = Path.Combine(_snapshotDirectory, Guid.NewGuid().ToString("N") + ".ini");
                    File.Copy(source, snapshot);
                }
            }
            return new(this, Current, State.Draft, connected, snapshot, enabled, original);
        }
        catch { if (snapshot != null && File.Exists(snapshot)) File.Delete(snapshot); throw; }
        finally { _changes.Release(); }
    }

    public async Task RestoreContextAsync(RenderSessionContext context, CancellationToken token = default)
    {
        if (!ReferenceEquals(context.Owner, this)) throw new ArgumentException("The editing context belongs to a different session.");
        ObjectDisposedException.ThrowIf(context.Disposed, context);
        long request;
        lock (_requests) { ObjectDisposedException.ThrowIf(_disposed, this); request = ++_request; _activeChange?.Cancel(); }
        await _changes.WaitAsync(token);
        using var change = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            lock (_requests)
            {
                if (_disposed || request != _request) throw new OperationCanceledException("Context restore superseded.");
                _activeChange = change;
            }
            // Keep a recovery copy even if startup fails or the caller releases its context.
            if (context.PresetSnapshot != null)
            {
                Directory.CreateDirectory(_snapshotDirectory);
                var recovery = Path.Combine(_snapshotDirectory, Guid.NewGuid().ToString("N") + ".ini");
                File.Copy(context.PresetSnapshot, recovery);
                ReplaceSnapshot(recovery);
            }
            else ClearSnapshot();
            await ResetAsync();
            Current = context.Input;
            _restoreSnapshotPending = true;
            _lastEffectsEnabled = context.EffectsEnabled;
            State.Edit(context.Draft);
            if (!context.Connected) return;
            if (_lastSnapshot == null) throw new IOException("The original editing preset was not captured.");
            // Do not reload the source preset: it may have changed or been deleted.
            await _host.StartAsync(context.Input, _lastSnapshot, change.Token);
            await CallAsync("set_effects_state", new { enabled = context.EffectsEnabled }, change.Token);
            await CallAsync("set_input_watch_enabled", new { enabled = context.Input.WatchInput }, change.Token);
            await CallAsync("set_preview_comparison", new { original = context.PreviewOriginal }, change.Token);
            var info = await CallAsync("get_runtime_info", cancellationToken: change.Token);
            if (info.TryGetProperty("inputError", out var error) && !string.IsNullOrEmpty(error.GetString()))
                throw new IOException("Original input could not be restored: " + error.GetString());
            change.Token.ThrowIfCancellationRequested();
            lock (_requests)
            {
                if (_disposed || request != _request) throw new OperationCanceledException("Context restore superseded.");
                State.AppliedSuccessfully(State.BeginApply(context.Input));
                _restoreSnapshotPending = false;
            }
        }
        catch { await ResetAsync(); throw; }
        finally
        {
            lock (_requests) { if (ReferenceEquals(_activeChange, change)) _activeChange = null; }
            _changes.Release();
        }
    }
}
