using System.Collections.Concurrent;
using System.Collections.Immutable;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellySeeNotifications;

internal sealed class LibraryPendingException : Exception;
internal sealed class LibraryCallException : Exception;
internal sealed record ItemSnapshot(Title Title, bool Ready, string? Series);
internal interface INotificationLibrary
{
    ItemSnapshot? Read(string id, long queuedAt, long now);
    string[] Audience(string? id, string[] folders, long now);
    long AudienceEvaluatedAt(string id, long now) => now;
    long AudienceEvaluatedAt(string? id, string[] folders, long now) => id is null ? now : AudienceEvaluatedAt(id, now);
    void CancelRead(string id) { }
    void CancelAudience(string? id, string[] folders) { }
    bool ScanRunning { get; }
}
internal sealed class DirectLibrary(IJellyfinCompat compat, string key) : INotificationLibrary
{
    public ItemSnapshot? Read(string id, long queuedAt, long now)
    {
        var item = compat.Get(id);
        return item is null || !compat.IsTitle(item) ? null : new(compat.Describe(item, 0, 0), compat.Ready(item, queuedAt, now), compat.Series(item));
    }
    public string[] Audience(string? id, string[] folders, long now) => compat.Audience(id is null ? null : compat.Get(id), folders, key);
    public bool ScanRunning => false;
}

// Exactly one thread per lane for the process lifetime. A timed-out call retains
// its slot until it really returns; a restart never replaces either lane.
internal sealed class LibraryLane : IDisposable
{
    private readonly BlockingCollection<Action> queue = new(16);
    private readonly Dictionary<string, Request> requests = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private Request? active;
    private int disposed;
    private int completionPulse;
    private sealed class Request(long started, TimeSpan deadline)
    {
        internal readonly long Started = started;
        internal readonly TimeSpan Deadline = deadline;
        internal long Finished;
        internal int TimedOut;
        internal readonly TaskCompletionSource<object?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal LibraryLane(string name, TimeProvider clock)
    {
        this.clock = clock;
        new Thread(() =>
        {
            foreach (var action in queue.GetConsumingEnumerable()) action();
            queue.Dispose();
        }) { IsBackground = true, Name = "JellySee " + name }.Start();
    }
    internal bool Stuck => active is { } r && !r.Result.Task.IsCompleted && clock.GetElapsedTime(r.Started) >= r.Deadline;
    internal bool Stalled => active is { } r && !r.Result.Task.IsCompleted && clock.GetElapsedTime(r.Started) > TimeSpan.FromMinutes(30);
    internal int Count => requests.Count;
    // Called only by the processing loop. The old physical call retains its lane slot,
    // but its eventual result cannot be adopted by a new request for this key.
    internal void Forget(string key) => requests.Remove(key);
    internal void ForgetAll() => requests.Clear();
    internal void ForgetOther(string prefix, string keep)
    {
        foreach (string key in requests.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k != keep).ToArray()) requests.Remove(key);
    }
    internal bool Pending => !Stuck && (Interlocked.Exchange(ref completionPulse, 0) != 0 || requests.Values.Any(r => !r.Result.Task.IsCompleted && clock.GetElapsedTime(r.Started) < TimeSpan.FromMilliseconds(100)));
    internal T Poll<T>(string key, Func<T> call, TimeSpan? deadline = null)
    {
        if (Volatile.Read(ref disposed) != 0) throw new LibraryCallException();
        if (!requests.TryGetValue(key, out var r))
        {
            if (Stuck) throw new LibraryCallException();
            // Completed results still belong to their waiting callers. Backpressure preserves
            // them until consumption or explicit cancellation, even across large callback bursts.
            if (requests.Count >= 64) throw new LibraryPendingException();
            r = new(clock.GetTimestamp(), deadline ?? TimeSpan.FromSeconds(15));
            var request = r;
            if (!queue.TryAdd(() =>
            {
                Volatile.Write(ref active, request);
                try { var value = call(); request.Finished = clock.GetTimestamp(); Interlocked.Exchange(ref completionPulse, 1); request.Result.TrySetResult(value); }
                catch (Exception ex) { request.Finished = clock.GetTimestamp(); Interlocked.Exchange(ref completionPulse, 1); request.Result.TrySetException(ex); _ = request.Result.Task.Exception; }
                finally { Volatile.Write(ref active, null); }
            })) throw new LibraryPendingException();
            requests.Add(key, request);
        }
        if (!r.Result.Task.IsCompleted)
        {
            if (clock.GetElapsedTime(r.Started) >= r.Deadline) { r.TimedOut = 1; throw new LibraryCallException(); }
            throw new LibraryPendingException();
        }
        requests.Remove(key);
        if (r.TimedOut != 0 || clock.GetElapsedTime(r.Started, r.Finished) >= r.Deadline) throw new LibraryCallException();
        return (T)r.Result.Task.GetAwaiter().GetResult()!;
    }
    internal async Task<T> CallAsync<T>(Func<T> call, CancellationToken ct)
    {
        if (Volatile.Read(ref disposed) != 0 || Stuck) throw new LibraryCallException();
        var request = new Request(clock.GetTimestamp(), TimeSpan.FromSeconds(15));
        if (!queue.TryAdd(() =>
        {
            Volatile.Write(ref active, request);
            try { var value = call(); request.Finished = clock.GetTimestamp(); Interlocked.Exchange(ref completionPulse, 1); request.Result.TrySetResult(value); }
            catch (Exception ex) { request.Finished = clock.GetTimestamp(); Interlocked.Exchange(ref completionPulse, 1); request.Result.TrySetException(ex); _ = request.Result.Task.Exception; }
            finally { Volatile.Write(ref active, null); }
        })) throw new LibraryPendingException();
        try
        {
            var result = await request.Result.Task.WaitAsync(request.Deadline, clock, ct);
            if (clock.GetElapsedTime(request.Started, request.Finished) >= request.Deadline) throw new LibraryCallException();
            return (T)result!;
        }
        catch (TimeoutException) { throw new LibraryCallException(); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; queue.CompleteAdding(); }
}

internal sealed class LibraryLanes : INotificationLibrary, IDisposable
{
    private readonly IJellyfinCompat compat;
    private readonly string key;
    private readonly ITaskManager? tasks;
    private readonly TimeProvider clock;
    private long readEpoch, censusEpoch;
    internal readonly LibraryLane Census;
    internal readonly LibraryLane Live;
    // Only the processing loop accesses these bounded, short-lived immutable results.
    private readonly Dictionary<string, (long At, ItemSnapshot? Item)> items = new(StringComparer.Ordinal);
    private (long At, string[] Values)? users;
    private readonly Dictionary<string, (long At, long EvaluatedAt, string[] Audience)> audiences = new(StringComparer.Ordinal);
    internal LibraryLanes(IJellyfinCompat compat, string key, TimeProvider clock, ITaskManager? tasks = null)
    {
        this.compat = compat; this.key = key; this.clock = clock; this.tasks = tasks;
        Census = new("census", clock); Live = new("live items", clock);
    }
    public bool ScanRunning => tasks?.ScheduledTasks.Any(t => t.ScheduledTask.Key == "RefreshLibrary" && t.State == TaskState.Running) == true;
    internal bool Stuck => Census.Stuck || Live.Stuck;
    internal bool Pending => Census.Pending || Live.Pending;
    internal bool Stalled => Census.Stalled || Live.Stalled;
    internal InventoryView Inventory() => Census.Poll("inventory:" + censusEpoch, () => { var view = compat.ReadInventory(); return new InventoryView(view.Libraries.Select(l => l with { Locations = l.Locations.ToArray() }).ToArray(), view.Roots.ToArray()); });
    internal IReadOnlyList<Guid> Ids(string folder, string pass = "")
    {
        string request = "ids:" + folder + ":" + censusEpoch + ":" + pass;
        // NotificationService owns one census pass at a time. A new pass cancels abandoned results.
        Census.ForgetOther("ids:", request);
        return Census.Poll(request, () => compat.Ids(folder).ToImmutableArray(), TimeSpan.FromSeconds(120));
    }
    internal string[] Users()
    {
        if (users is { } cached && clock.GetElapsedTime(cached.At) < TimeSpan.FromSeconds(20)) return cached.Values;
        var result = Live.Poll("users", () => compat.Users(key).ToArray()); users = (clock.GetTimestamp(), result); return result;
    }
    internal Task<ImmutableArray<string>> Admins(CancellationToken ct) => Live.CallAsync(() => compat.AdminUserHashes(key).ToImmutableArray(), ct);
    internal void NewSession() { censusEpoch++; readEpoch++; Census.ForgetAll(); Live.ForgetAll(); items.Clear(); audiences.Clear(); users = null; }
    public ItemSnapshot? Read(string id, long queuedAt, long now)
    {
        if (items.TryGetValue(id, out var cached) && clock.GetElapsedTime(cached.At) < TimeSpan.FromSeconds(20)) return cached.Item;
        var result = Live.Poll("item:" + id + ":" + readEpoch, () =>
        {
            var item = compat.Get(id);
            if (item is null || !compat.IsTitle(item)) return null;
            var t = compat.Describe(item, 0, 0);
            return new ItemSnapshot(t with { Folders = t.Folders.ToArray() }, compat.Ready(item, now, now), compat.Series(item));
        });
        if (items.Count >= 2048) items.Remove(items.MinBy(p => p.Value.At).Key);
        items[id] = (clock.GetTimestamp(), result); return result;
    }
    internal void Invalidate(string id)
    {
        items.Remove(id); audiences.Remove("id:" + id);
        CancelRead(id); CancelAudience(id, []);
    }
    public void CancelRead(string id) => Live.Forget("item:" + id + ":" + readEpoch);
    public void CancelAudience(string? id, string[] folders)
    {
        string route = id is not null ? "id:" + id : "folders:" + string.Join(':', folders);
        Live.Forget("audience:" + route + ":" + readEpoch);
    }
    public string[] Audience(string? id, string[] folders, long now)
    {
        string route = id is not null ? "id:" + id : "folders:" + string.Join(':', folders);
        if (audiences.TryGetValue(route, out var cached) && clock.GetElapsedTime(cached.At) < TimeSpan.FromMinutes(20) && !Rules.Stale(cached.EvaluatedAt, now)) return cached.Audience;
        long queued = clock.GetTimestamp();
        var result = Live.Poll("audience:" + route + ":" + readEpoch, () =>
        {
            var item = id is null ? null : compat.Get(id);
            string[] usersNow = id is not null && (item is null || !compat.IsTitle(item)) ? [] : compat.Audience(item, folders, key).ToArray();
            return (Users: usersNow, At: now + (long)clock.GetElapsedTime(queued).TotalSeconds);
        });
        if (Rules.Stale(result.At, now)) throw new LibraryPendingException();
        if (audiences.Count >= 2048) audiences.Remove(audiences.MinBy(p => p.Value.At).Key);
        audiences[route] = (clock.GetTimestamp(), result.At, result.Users); return result.Users;
    }
    public long AudienceEvaluatedAt(string id, long now) => AudienceEvaluatedAt(id, [], now);
    public long AudienceEvaluatedAt(string? id, string[] folders, long now)
    {
        string route = id is not null ? "id:" + id : "folders:" + string.Join(':', folders);
        return audiences.TryGetValue(route, out var sample) ? sample.EvaluatedAt : now;
    }
    public void Dispose() { Census.Dispose(); Live.Dispose(); }
}
