using System.Text.Json;
using System.Text.Json.Nodes;

namespace OfflineReShade.Core;

// One queue for UI and MCP mutations. Editing may queue; an exclusive operation
// closes admission until it finishes, while draining edits already accepted.
public sealed class OperationCoordinator
{
    private sealed record Work(CancellationTokenSource Cancellation, TaskCompletionSource Completion);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1), _stopGate = new(1, 1);
    private readonly Dictionary<long, Work> _pending = new();
    private int _exclusive;
    private bool _stopping;
    public ApplicationActivity Activity { get; } = new();
    public bool IsBusy { get { lock (_gate) return _stopping || _pending.Count > 0; } }
    public bool IsExclusive { get { lock (_gate) return _stopping || _exclusive > 0; } }
    public event Action? Changed;

    public async Task<JsonElement> RunAsync(OperationRequest request, Func<CancellationToken, Task<JsonElement>> action, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var work = new Work(cancellation, new(TaskCreationOptions.RunContinuationsAsynchronously));
        var exclusive = ApplicationActivity.IsExclusive(request.Method);
        lock (_gate)
        {
            if (_stopping || _exclusive > 0)
            {
                var error = new InvalidOperationException("Another operation owns the session. Wait or cancel it before editing.");
                Activity.Fail(request, error, rejected: true);
                throw error;
            }
            _pending.Add(request.Id, work);
            if (exclusive) _exclusive++;
        }
        var acquired = false;
        try
        {
            Changed?.Invoke();
            await _serial.WaitAsync(cancellation.Token);
            acquired = true;
            cancellation.Token.ThrowIfCancellationRequested();
            Activity.Begin(request);
            var result = await action(cancellation.Token);
            string? Text(string name) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var warning = Text("warning");
            if (Text("path") is { Length: > 0 } actualPath) request = request with { TargetPath = actualPath };
            var state = request.Method is "batch_start" or "batch_resume" or "batch_retry" ? Text("state") : null;
            var status = state == "failed" ? "failed" : state == "cancelled" ? "cancelled" : string.IsNullOrEmpty(warning) ? "succeeded" : "warning";
            var outcome = Activity.Finish(request, status, status == "warning" ? "output_warning" : status == "failed" ? "batch_failed" : status == "cancelled" ? "cancelled" : "ok",
                Text("error") ?? warning ?? (status == "cancelled" ? "Operation cancelled." : "Operation completed."));
            return WithOutcome(result, outcome);
        }
        catch (Exception error) { Activity.Fail(request, error); throw; }
        finally
        {
            if (acquired) _serial.Release();
            lock (_gate) { _pending.Remove(request.Id); if (exclusive) _exclusive--; }
            work.Completion.TrySetResult();
            Changed?.Invoke();
        }
    }

    public void Cancel()
    {
        lock (_gate) foreach (var work in _pending.Values.ToArray()) work.Cancellation.Cancel();
    }

    public async Task StopAsync(Func<Task>? stop = null)
    {
        await _stopGate.WaitAsync();
        try
        {
            Task[] tasks;
            lock (_gate)
            {
                _stopping = true;
                foreach (var work in _pending.Values.ToArray()) work.Cancellation.Cancel();
                tasks = _pending.Values.Select(x => x.Completion.Task).ToArray();
            }
            Changed?.Invoke();
            await Task.WhenAll(tasks);
            if (stop != null) await stop();
        }
        finally { lock (_gate) _stopping = false; _stopGate.Release(); Changed?.Invoke(); }
    }

    public static JsonElement WithOutcome(JsonElement result, OperationOutcome outcome)
    {
        var node = result.ValueKind == JsonValueKind.Object ? JsonNode.Parse(result.GetRawText())!.AsObject() : new JsonObject { ["result"] = JsonNode.Parse(result.GetRawText()) };
        node["operation"] = JsonSerializer.SerializeToNode(outcome, AutomationEngine.JsonOptions);
        return AutomationEngine.Json(node);
    }
}
