using Microsoft.UI.Dispatching;

namespace OfflineReShade.WinUI.Services;

public sealed class UiSynchronizationContext(DispatcherQueue queue) : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (!queue.TryEnqueue(() => {
            var previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        })) throw new ObjectDisposedException("UI dispatcher");
    }
    public override SynchronizationContext CreateCopy() => this;
}
