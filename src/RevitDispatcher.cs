using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace OpenStrateBridge;

/// <summary>
/// The ExternalEvent bridge: the ONLY path from background threads into the
/// Revit API. HTTP threads enqueue work and Raise() the event; Revit executes
/// the handler on its UI thread when idle; results flow back via
/// TaskCompletionSource. Never touch the Revit API off this handler.
/// </summary>
public sealed class RevitDispatcher : IExternalEventHandler
{
    public sealed record WorkItem(
        Func<UIApplication, object> Work,
        TaskCompletionSource<object> Result);

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private ExternalEvent? _event;

    /// <summary>Must be called on the Revit UI thread (OnStartup).</summary>
    public void Attach() => _event = ExternalEvent.Create(this);

    /// <summary>Called from ANY thread: enqueue, raise, await.</summary>
    public Task<object> Run(Func<UIApplication, object> work, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new WorkItem(work, tcs));
        var request = _event?.Raise()
            ?? throw new InvalidOperationException("Dispatcher not attached.");
        if (request == ExternalEventRequest.Denied)
            tcs.TrySetException(new InvalidOperationException("Revit denied the external event (modal state?)."));

        // Guard against Revit sitting in a modal dialog forever.
        var t = timeout ?? TimeSpan.FromSeconds(60);
        _ = Task.Delay(t).ContinueWith(_ =>
            tcs.TrySetException(new TimeoutException($"Revit did not become idle within {t.TotalSeconds:0}s.")));
        return tcs.Task;
    }

    /// <summary>Runs ON the Revit UI thread. Drain everything queued.</summary>
    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var item))
        {
            try { item.Result.TrySetResult(item.Work(app)); }
            catch (Exception ex) { item.Result.TrySetException(ex); }
        }
    }

    public string GetName() => "OpenStrate Bridge Dispatcher";
}
