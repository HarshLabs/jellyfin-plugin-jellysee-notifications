namespace Jellyfin.Plugin.JellySeeNotifications.Core;

public static class IterationLoop
{
    // The caller retains its journal, subscriptions, queues and scan across retries.
    public static async Task Run(Func<Task> iteration, Action<string> error, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan pause;
            try
            {
                await iteration();
                if (failures != 0) error("none");
                failures = 0; pause = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not (StatePersistenceException or OutOfMemoryException))
            {
                error("adapterUnavailable");
                failures = Math.Min(failures + 1, 7);
                pause = TimeSpan.FromSeconds(Math.Min(60, 1 << (failures - 1)));
            }
            await delay(pause, ct);
        }
    }
}

public sealed class ReconcileQueue
{
    private readonly Queue<Library> remaining = new();
    private bool requested;
    public void Request() => requested = true;
    // Called only when the current library scan finishes. Requests coalesce into
    // one follow-up pass; neither its cursor nor the unvisited tail is discarded.
    public Library? Next(IEnumerable<Library> current)
    {
        if (remaining.Count == 0 && requested)
        {
            requested = false;
            foreach (var library in current.OrderBy(l => l.Id)) remaining.Enqueue(library);
        }
        return remaining.TryDequeue(out var next) ? next : null;
    }
}
