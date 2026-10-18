using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellySeeNotifications.Core;

// One dedicated background thread owns every journal handle. A stalled managed
// open/read cannot be interrupted; abandon its worker rather than joining it.
internal sealed class StateIoWorker : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly BlockingCollection<Action> work = new(1);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken token;
    private readonly object stopGate = new();
    private readonly TimeSpan timeout;
    private int abandoned;
    private int disposed;
    internal CancellationToken Token => token;

    internal StateIoWorker(CancellationToken ct, TimeSpan timeout, Action cleanup)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout));
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        token = lifetime.Token;
        this.timeout = timeout;
        new Thread(() =>
        {
            try
            {
                foreach (var action in work.GetConsumingEnumerable()) action();
            }
            finally
            {
                try { cleanup(); }
                catch (Exception) { /* No exception text or unhandled background-thread failure. */ }
                // CompleteAdding and resource disposal must not race.
                lock (stopGate) { lifetime.Dispose(); work.Dispose(); }
                stopped.TrySetResult();
            }
        }) { IsBackground = true, Name = "JellySee state I/O" }.Start();
    }

    internal void Invoke(Action action) => Invoke(() => { action(); return true; });
    internal T Invoke<T>(Func<T> action)
    {
        if (Volatile.Read(ref abandoned) != 0 || Volatile.Read(ref disposed) != 0)
            throw new StatePersistenceException(new IOException("State worker unavailable"));
        Token.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(() =>
        {
            try { Token.ThrowIfCancellationRequested(); result.TrySetResult(action()); }
            catch (Exception ex)
            {
                result.TrySetException(ex);
                _ = result.Task.Exception; // Observe late failures after abandonment too.
            }
        });
        try { return result.Task.WaitAsync(timeout, Token).GetAwaiter().GetResult(); }
        catch (TimeoutException ex)
        {
            Abandon();
            throw new StatePersistenceException(ex);
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested)
        {
            Abandon();
            throw;
        }
    }
    private void Abandon()
    {
        lock (stopGate)
        {
            Interlocked.Exchange(ref abandoned, 1);
            lifetime.Cancel();
            work.CompleteAdding();
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // CompleteAdding may already have been called on timeout/cancellation.
        bool cancelled;
        lock (stopGate)
        {
            if (Volatile.Read(ref abandoned) != 0) return;
            cancelled = Token.IsCancellationRequested;
            work.CompleteAdding();
        }
        if (cancelled) return;
        // Normal disposal closes handles before returning. Shutdown/abandonment
        // never waits for the blocked thread, including a blocked final close.
        try { stopped.Task.WaitAsync(timeout, Token).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
    }
}
