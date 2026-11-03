namespace OfflineReShade.Core;

// UI debounce must remain visible to operations that capture or export editor state.
// Flush submits every latest edit synchronously, then returns their completion barrier.
public sealed class DeferredEditQueue(Action<Exception> reportError, int delayMilliseconds = 16) : IDisposable
{
    private sealed class Entry(Func<Task> action)
    {
        public Func<Task> Action = action;
        public bool Submitted;
        public CancellationTokenSource Delay = new();
        public TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _latest = new(StringComparer.Ordinal);
    private bool _disposed;

    public void Enqueue(string key, Func<Task> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_latest.TryGetValue(key, out var pending) && !pending.Submitted) { pending.Action = action; return; }
            var entry = new Entry(action);
            _latest[key] = entry;
            _ = AfterDelayAsync(entry);
        }
    }
    private async Task AfterDelayAsync(Entry entry)
    {
        try { await Task.Delay(delayMilliseconds, entry.Delay.Token); Submit(entry); }
        catch (OperationCanceledException) { }
        finally { entry.Delay.Dispose(); }
    }
    private void Submit(Entry entry)
    {
        Func<Task> action;
        lock (_gate) { if (entry.Submitted) return; entry.Submitted = true; action = entry.Action; }
        // Invoke before returning: the shared coordinator must receive edits before export admission.
        Task task;
        try { task = action(); } catch (Exception error) { task = Task.FromException(error); }
        _ = ObserveAsync(entry, task);
    }
    private async Task ObserveAsync(Entry entry, Task task)
    {
        try { await task; entry.Completion.TrySetResult(); }
        catch (Exception error)
        {
            entry.Completion.TrySetException(error);
            _ = entry.Completion.Task.Exception; // Errors are reported even if no export follows.
            reportError(error);
        }
    }
    public Task FlushAsync()
    {
        Entry[] entries;
        lock (_gate) entries = _latest.Values.ToArray();
        foreach (var entry in entries) Submit(entry);
        return Task.WhenAll(entries.Select(x => x.Completion.Task));
    }
    public Task SubmitNowAsync(string key, Func<Task> action)
    {
        Enqueue(key, action);
        Entry entry;
        lock (_gate) entry = _latest[key];
        Submit(entry);
        return entry.Completion.Task;
    }
    // Call after draining/stopping operations or after explicitly replacing editor contents.
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _latest.Values.Where(x => !x.Submitted))
            {
                entry.Submitted = true;
                entry.Delay.Cancel();
                entry.Completion.TrySetCanceled();
            }
            _latest.Clear();
        }
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; Clear(); } }
}
