using System.Text.Json;

namespace OfflineReShade.Core;

public sealed record PendingInput(RenderInput Input, string State, string? Error = null);

// Waits for an initial stable pair. After a successful open, the native runtime
// owns watching subsequent changes, including generation checks before exporting.
public sealed class InputArrivalMonitor
{
    private readonly Func<RenderInput, CancellationToken, Task<JsonElement>> _apply;
    private readonly Action<JsonElement> _applied;
    private readonly Func<bool> _busy;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private Task _task = Task.CompletedTask;
    private volatile PendingInput? _pending;
    public PendingInput? Pending => _pending;
    public event Action? Changed;
    public InputArrivalMonitor(Func<RenderInput, CancellationToken, Task<JsonElement>> apply, Action<JsonElement> applied, Func<bool> busy)
        => (_apply, _applied, _busy) = (apply, applied, busy);

    public void Start(RenderInput input)
    {
        lock (_gate)
        {
            if (_cancel != null) throw new InvalidOperationException("Stop the previous input listener first.");
            _cancel = new();
            _pending = new(input, "waiting");
            _task = RunAsync(input, _cancel);
        }
        Changed?.Invoke();
    }
    public void Cancel() { lock (_gate) _cancel?.Cancel(); }
    public async Task StopAsync() { Task task; lock (_gate) { _cancel?.Cancel(); task = _task; } await task; }

    private void Update(PendingInput state)
    {
        lock (_gate) { if (_pending == state) return; _pending = state; }
        Changed?.Invoke();
    }
    private async Task RunAsync(RenderInput input, CancellationTokenSource cancel)
    {
        string? previous = null, failed = null;
        try
        {
            while (true)
            {
                await Task.Delay(250, cancel.Token);
                var (signature, readError) = Stamp(input);
                if (signature == null) { previous = null; failed = null; Update(new(input, readError == null ? "waiting" : "unreadable", readError)); continue; }
                if (signature != previous) { previous = signature; continue; }
                if (signature == failed || _busy()) continue;
                Update(new(input, "opening"));
                try
                {
                    var result = await _apply(input, cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                    lock (_gate) _pending = null;
                    Changed?.Invoke();
                    _applied(result);
                    return;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (error.Data[nameof(OperationOutcome)] is OperationOutcome { Status: "rejected" }) continue;
                    failed = signature;
                    Update(new(input, "invalid", error.Message));
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally
        {
            lock (_gate) { _pending = null; _cancel = null; }
            cancel.Dispose();
            Changed?.Invoke();
        }
    }
    private static (string? Signature, string? Error) Stamp(RenderInput input)
    {
        var result = new List<string>();
        foreach (var path in new[] { input.ColorPath, input.DepthPath }.Where(x => x.Length > 0))
        {
            try
            {
                using var readable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (readable.Length == 0) return (null, null);
                result.Add(path + ":" + readable.Length + ":" + File.GetLastWriteTimeUtc(path).Ticks);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return (null, null); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return (null, $"Cannot read input '{path}': {error.Message}"); }
        }
        return (string.Join("|", result), null);
    }
}
