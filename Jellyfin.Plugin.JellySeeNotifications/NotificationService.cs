using System.Text.Json;
using System.Threading.Channels;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySeeNotifications;

public sealed class NotificationService : BackgroundService
{
    private readonly ILibraryManager library;
    private readonly IJellyfinCompat compat;
    private readonly IServerApplicationHost host;
    private readonly RelayClient relay;
    private readonly ILogger<NotificationService> logger;
    private readonly Channel<Signal> channel;
    private readonly Func<PluginConfiguration> configuration;
    private readonly Func<(string Directory, string Root)> statePaths;
    private readonly TimeSpan stateIoTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> stateRetryDelay;
    private long channelDropped;
    private bool enabled;
    private string setupHash = "";
    private string error = "none";
    private bool tooManyUsers;
    private sealed record Signal(string Op, string Id, string? Type, string EventId, long At, string Setup, string? ParentId = null);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public NotificationService(ILibraryManager library, IJellyfinCompat compat, IServerApplicationHost host, RelayClient relay, ILogger<NotificationService> logger)
        : this(library, compat, host, relay, logger,
            () => (Plugin.Instance ?? throw new InvalidOperationException()).Configuration,
            () => { var plugin = Plugin.Instance ?? throw new InvalidOperationException(); return (plugin.DataFolderPath, plugin.StateRootPath); },
            StateIoWorker.DefaultTimeout) { }

    internal NotificationService(ILibraryManager library, IJellyfinCompat compat, IServerApplicationHost host, RelayClient relay, ILogger<NotificationService> logger,
        Func<PluginConfiguration> configuration, Func<(string Directory, string Root)> statePaths, TimeSpan stateIoTimeout,
        Func<TimeSpan, CancellationToken, Task>? stateRetryDelay = null)
    {
        this.library = library; this.compat = compat; this.host = host; this.relay = relay; this.logger = logger;
        this.configuration = configuration; this.statePaths = statePaths; this.stateIoTimeout = stateIoTimeout;
        this.stateRetryDelay = stateRetryDelay ?? Task.Delay;
        channel = Channel.CreateBounded<Signal>(new BoundedChannelOptions(State.Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest }, _ => Interlocked.Increment(ref channelDropped));
    }
    private void Added(object? sender, ItemChangeEventArgs e)
    {
        if (Volatile.Read(ref enabled) && compat.IsTitle(e.Item)) Enqueue("add", e.Item, compat.TitleType(e.Item));
    }
    private void Removed(object? sender, ItemChangeEventArgs e)
    {
        if (Volatile.Read(ref enabled))
        {
            // Series is both a title and a container, and must trigger the descendant reconcile.
            if (compat.TitleType(e.Item) is { } type) Enqueue("rm", e.Item, type, e.Parent?.Id);
            if (e.Item is Folder) Enqueue("dirty", e.Item, null);
        }
    }
    private void Updated(object? sender, ItemChangeEventArgs e)
    {
        if (Volatile.Read(ref enabled))
        {
            if (compat.IsTitle(e.Item)) Enqueue("update", e.Item, compat.TitleType(e.Item));
            if (e.Item is Folder) Enqueue("dirty", e.Item, null);
        }
    }
    private void Enqueue(string op, BaseItem item, string? type, Guid? parentId = null) => channel.Writer.TryWrite(new(op, item.Id.ToString("N"), type, Identity.EventId(), Now, Volatile.Read(ref setupHash), parentId?.ToString("N")));
    private void SetError(string code) { error = code; if (code == "tooManyUsers") tooManyUsers = true; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield(); // .NET 9 StartAsync invokes ExecuteAsync synchronously.
        // Reopen/replay after a completed sharing failure: a failed append or
        // fsync may already be durable, so never blindly repeat a transaction.
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Run(ct); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                Volatile.Write(ref enabled, false);
                SetError("stateUnavailable");
                var cause = StateFailure.Cause(ex);
                // Never pass the exception itself, its message, paths or IDs.
                logger.LogError("[JellySee Notifications] State unavailable (stateUnavailable); exceptionType={ExceptionType}; hResult={HResult:X8}",
                    cause.GetType().FullName, cause.HResult);
                await ReportStateUnavailable(ct);
                if (!StateFailure.IsSharing(ex)) return;
            }
            finally
            {
                Volatile.Write(ref enabled, false);
                library.ItemAdded -= Added; library.ItemRemoved -= Removed; library.ItemUpdated -= Updated;
            }
            failures = Math.Min(failures + 1, 7);
            try { await stateRetryDelay(TimeSpan.FromSeconds(Math.Min(60, 1 << (failures - 1))), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
    private async Task ReportStateUnavailable(CancellationToken ct)
    {
        // A timed-out worker may still own/mutate its state. Health must never
        // inspect it or retry its path; only completed workers can be reopened.
        try
        {
            var config = configuration();
            config.Status = "stateUnavailable";
            if (config.Enabled && !string.IsNullOrWhiteSpace(config.Secret) && !ct.IsCancellationRequested)
                await relay.Post(config.RelayOrigin, Identity.ServerKey(host.SystemId), config.Secret, "heartbeat",
                    Heartbeat.Encode(host.SystemId, new State(), Now, "stateUnavailable", false, 0), [], ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { /* Keep the fixed dashboard failure even if the one-shot heartbeat fails. */ }
    }
    private async Task Run(CancellationToken ct)
    {
        var paths = statePaths();
        using var journal = new Journal(paths.Directory, paths.Root, ct, stateIoTimeout);
        try { await RunJournal(journal, ct); }
        catch (Exception ex) when (StateFailure.IsSharing(ex))
        {
            journal.Dispose();
            if (!journal.CanRestart) throw new StatePersistenceException(new TimeoutException());
            throw;
        }
    }
    private async Task RunJournal(Journal journal, CancellationToken ct)
    {
        if (error == "stateUnavailable") SetError("none");
        string key = Identity.ServerKey(host.SystemId);
        var engine = new Engine(journal, compat, key);
        if (journal.RecoveredCorruption) SetError("stateRecovered");
        library.ItemAdded += Added; library.ItemRemoved += Removed; library.ItemUpdated += Updated;
        long nextInventory = 0, nextReconcile = 0, nextReady = 0, nextHeartbeat = 0, nextLibrary = 0, dirtyAt = 0;
        long lastQueued = Now;
        string configStamp = "";
        int emptyPolls = 0;
        Dictionary<string, SendSchedule> schedules = new(StringComparer.Ordinal);
        var reconciliation = new ReconcileQueue();
        Scan? scan = null;
        var sentAt = new Queue<long>();
        await IterationLoop.Run(async () =>
        {
            long now = Now;
            var config = configuration();
            bool configured = config.Enabled && !string.IsNullOrWhiteSpace(config.Secret);
            string stamp = Identity.Hash(config.Enabled + ":" + config.RelayOrigin + ":" + config.Secret);
            if (stamp != configStamp)
            {
                configStamp = stamp; Volatile.Write(ref setupHash, Identity.Hash(config.Secret));
                Volatile.Write(ref enabled, configured); nextHeartbeat = 0; nextInventory = 0; nextReconcile = 0;
                if (!schedules.ContainsKey(setupHash)) schedules[setupHash] = new();
                schedules[setupHash].Next = 0;
            }
            long lost = Interlocked.Exchange(ref channelDropped, 0);
            if (lost != 0) journal.Commit(new Change() { Kind = "health", Health = journal.State.Health with { Dropped = journal.State.Health.Dropped + lost } });
            for (int i = 0; i < 250 && channel.Reader.TryRead(out var signal); i++)
            {
                ct.ThrowIfCancellationRequested();
                switch (signal.Op)
                {
                    case "add": engine.QueueAdd(signal.Id, signal.Type!, signal.EventId, signal.At, signal.Setup); scan?.Known.Add(signal.Id); lastQueued = now; break;
                    case "rm": engine.Remove(signal.Id, signal.EventId, signal.At, signal.Setup); scan?.Known.Remove(signal.Id); lastQueued = now; break;
                    case "update":
                        try { if (compat.Get(signal.Id) is { } item) engine.Index(item); }
                        catch (StatePersistenceException) { throw; }
                        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException)) { SetError("itemEvaluationFailed"); }
                        break;
                    case "dirty": if (dirtyAt == 0) dirtyAt = now + 30; break;
                }
            }
            if (configured)
            {
                if (now >= nextInventory)
                {
                    try
                    {
                        var poll = InventoryRules.Evaluate(journal.State.Libraries.Values.ToArray(), compat.Inventory(), true, emptyPolls);
                        emptyPolls = poll.EmptyPolls;
                        if (poll.Complete)
                        {
                            bool changed = ReconcileInventory(journal, engine, poll, now, setupHash);
                            if (changed) { nextReconcile = 0; lastQueued = now; }
                        }
                        else SetError("inventoryIncomplete");
                    }
                    catch (IOException ex) when (ex is not StatePersistenceException) { emptyPolls = 0; SetError("inventoryIncomplete"); }
                    catch (UnauthorizedAccessException) { emptyPolls = 0; SetError("inventoryIncomplete"); }
                    catch (InvalidOperationException) { emptyPolls = 0; SetError("inventoryIncomplete"); }
                    nextInventory = now + 120;
                }
                if (dirtyAt != 0 && now >= dirtyAt || now >= nextReconcile)
                {
                    reconciliation.Request();
                    nextReconcile = now + 900; dirtyAt = 0;
                }
                if (scan is null && now >= nextLibrary && reconciliation.Next(journal.State.Libraries.Values) is { } folder)
                {
                    if (journal.State.Libraries.Values.Any(l => l.Id == folder.Id))
                    {
                        try { scan = new Scan(folder, compat.Ids(folder.Id), journal.State.Seq, journal.State); }
                        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException)) { SetError("reconcileFailed"); }
                    }
                    nextLibrary = now + 5;
                }
                if (scan is not null)
                {
                    // Pace metadata/index work; GetItemIds itself is one unpaged ids-only query.
                    List<BaseItem> toIndex = [];
                    for (int i = 0; i < 1000 && scan.Position < scan.Ids.Count; i++, scan.Position++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string id = scan.Ids[scan.Position].ToString("N");
                        try
                        {
                            var item = compat.Get(id);
                            if (item is null || !compat.IsTitle(item)) continue;
                            bool known = scan.Known.Contains(id);
                            if (scan.Library.BaselineComplete && !known && !journal.State.Health.IndexFull &&
                                !(journal.State.Tombstones.TryGetValue(id, out var tomb) && tomb.Seq > scan.StartSeq))
                            { engine.QueueAdd(id, compat.TitleType(item)!, Identity.EventId(), now, setupHash); scan.Known.Add(id); lastQueued = now; }
                            // Unknown recovered additions are indexed only when ready; baseline entries and
                            // known titles can be refreshed immediately, respecting the pass tombstone fence.
                            if (!scan.Library.BaselineComplete || journal.State.Index.ContainsKey(id)) toIndex.Add(item);
                        }
                        catch (StatePersistenceException) { throw; }
                        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException)) { scan.Failed = true; SetError("reconcileFailed"); }
                    }
                    engine.IndexMany(toIndex, scan.StartSeq, SetError);
                    if (scan.Position == scan.Ids.Count)
                    {
                        foreach (var title in journal.State.Index.Values.Where(t => t.Folders.Contains(scan.Library.Id) && !scan.Present.Contains(Guid.ParseExact(t.Id, "N"))).ToArray())
                        {
                            ct.ThrowIfCancellationRequested();
                            try { if (compat.Get(title.Id) is null) { engine.Remove(title.Id, Identity.EventId(), now, setupHash); lastQueued = now; } }
                            catch (StatePersistenceException) { throw; }
                            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException)) { scan.Failed = true; SetError("reconcileFailed"); }
                        }
                        if (!scan.Failed && !scan.Library.BaselineComplete && journal.State.Libraries.ContainsKey(scan.Library.DirectoryKey))
                            journal.Commit(new Change() { Kind = "library", Library = scan.Library with { BaselineComplete = true } });
                        scan = null;
                    }
                }
                if (now >= nextReady)
                {
                    tooManyUsers = false;
                    try { compat.Users(key); }
                    catch (TooManyUsersException) { SetError("tooManyUsers"); }
                    if (!tooManyUsers) { engine.AdvanceAdds(now, SetError); engine.AdvanceRemovals(now, SetError); }
                    engine.Sweep(now); nextReady = now + 30;
                }
                var credentials = Credentials.Available(config, now);
                foreach (string hash in credentials.Keys) if (!schedules.ContainsKey(hash)) schedules[hash] = new();
                foreach (string hash in schedules.Keys.Where(h => !credentials.ContainsKey(h)).ToArray()) schedules.Remove(hash);
                var group = journal.State.Notices.Values.Where(n => n.Stage == "outbox" && !Rules.Expired(n, now) && credentials.ContainsKey(n.SetupHash))
                    .GroupBy(n => n.SetupHash).Where(g => schedules[g.Key].Next <= now)
                    .OrderBy(g => schedules[g.Key].LastAttempt).FirstOrDefault();
                var pending = group?.OrderBy(n => n.QueuedAt).ToArray() ?? [];
                var schedule = group is null ? new SendSchedule() : schedules[group.Key];
                if (!tooManyUsers && pending.Length != 0 && (now - lastQueued >= 60 || now - pending[0].QueuedAt >= 300))
                {
                    while (sentAt.Count != 0 && now - sentAt.Peek() >= 60) sentAt.Dequeue();
                    if (sentAt.Count < 20)
                    {
                        try
                        {
                            var users = compat.Users(key);
                            // Freshness is checked immediately before each request, including retries.
                            List<Notice> fresh = [];
                            foreach (var notice in pending.Take(50))
                            {
                                try { if (engine.Freshen(notice, now) is { } reevaluated) fresh.Add(reevaluated); }
                                catch (TooManyUsersException) { throw; }
                                catch (Exception ex) when (ex is not (StatePersistenceException or OutOfMemoryException or OperationCanceledException)) { SetError("itemEvaluationFailed"); }
                            }
                            var batch = Wire.Split(host.SystemId, users, fresh).FirstOrDefault();
                            if (batch is not null)
                            {
                                sentAt.Enqueue(now); schedule.LastAttempt = now;
                                var result = await relay.Post(config.RelayOrigin, key, credentials[group!.Key], "events", batch.Body, batch.Events.Select(n => n.EventId), ct);
                                if (result.Success)
                                {
                                    engine.Acknowledge(result.Accepted, Now); error = "none"; schedule.Attempts = 0;
                                    // A valid partial/empty acknowledgement must not turn into a tight loop.
                                    schedule.Next = Now + (result.Accepted.Count == batch.Events.Length ? 0 : 30);
                                }
                                else
                                {
                                    SetError(result.ErrorCode);
                                    schedule.Next = result.Retryable ? Now + (long)Responses.RetryDelay(++schedule.Attempts, result.RetryAfter, Random.Shared.NextDouble()).TotalSeconds : long.MaxValue;
                                }
                            }
                            else schedule.Next = now + 30;
                        }
                        catch (TooManyUsersException) { SetError("tooManyUsers"); schedule.Next = now + 30; }
                        catch (InvalidDataException) { SetError("invalidConfiguration"); schedule.Next = now + 300; }
                    }
                }
                if (now >= nextHeartbeat)
                {
                    byte[] heartbeat = Heartbeat.Encode(host.SystemId, journal.State, now, error, tooManyUsers, journal.DiskBytes);
                    try
                    {
                        var result = await relay.Post(config.RelayOrigin, key, config.Secret, "heartbeat", heartbeat, [], ct);
                        if (result.Success)
                        {
                            journal.Commit(new Change() { Kind = "health", Health = journal.State.Health with { Dropped = 0 } });
                            nextHeartbeat = now + 21600;
                        }
                        else { SetError(result.ErrorCode); nextHeartbeat = now + Math.Max(300, (long)(result.RetryAfter?.TotalSeconds ?? 300)); }
                    }
                    catch (InvalidDataException) { SetError("invalidConfiguration"); nextHeartbeat = now + 300; }
                }
            }
            config.Status = configured ? error : "notConfigured";
            config.IndexFull = journal.State.Health.IndexFull; config.TooManyUsers = tooManyUsers;
            config.IndexCount = journal.State.Index.Count; config.StateBytes = journal.DiskBytes;
            config.OutboxDepth = journal.State.Notices.Values.Count(n => n.Stage == "outbox");
            config.BaselineLibrariesRemaining = journal.State.Libraries.Values.Count(l => !l.BaselineComplete);
            journal.MaybeCompact();
        }, code =>
        {
            if (code != "none" || error == "adapterUnavailable") SetError(code);
            configuration().Status = error;
        }, ct);
    }
    private static bool ReconcileInventory(Journal journal, Engine engine, InventoryResult poll, long now, string setup)
    {
        bool changed = false;
        foreach (var removed in poll.Removed)
        {
            changed = true;
            if (journal.State.Notices.Values.Count(n => n.Stage == "outbox") >= State.Capacity)
                engine.Drop(journal.State.Notices.Values.Where(n => n.Stage == "outbox").MinBy(n => n.QueuedAt)!.EventId);
            journal.Commit(new Change { Kind = "unlibrary", Key = removed.DirectoryKey },
                new() { Kind = "unroute", Key = removed.Id },
                new() { Kind = "notice", Notice = new()
                {
                    EventId = Identity.EventId(), Op = "rmlibrary", Stage = "outbox", Folders = [removed.Id],
                    At = now, QueuedAt = now, Life = engine.Life(removed.Id) + 1, SetupHash = setup
                } },
                new() { Kind = "remove", Tombstone = new(removed.Id, journal.State.Seq + 1, now, engine.Life(removed.Id) + 1) });
        }
        foreach (var current in poll.Current)
        {
            var old = journal.State.Libraries.GetValueOrDefault(current.DirectoryKey) ??
                journal.State.Libraries.Values.FirstOrDefault(l => !poll.Current.Any(n => n.DirectoryKey == l.DirectoryKey) && l.Locations.Length != 0 && l.Locations.SequenceEqual(current.Locations));
            if (old is null || old.DirectoryKey != current.DirectoryKey || old.Enabled != current.Enabled || !old.Locations.SequenceEqual(current.Locations) || old.Id != current.Id)
            {
                List<Change> changes = [];
                if (old is not null && old.DirectoryKey != current.DirectoryKey) changes.Add(new() { Kind = "unlibrary", Key = old.DirectoryKey });
                if (old is not null && old.Id != current.Id) changes.Add(new() { Kind = "reroute", Key = old.Id + ":" + current.Id });
                changes.Add(new() { Kind = "library", Library = current }); journal.Commit(changes.ToArray()); changed = true;
            }
        }
        return changed;
    }
    private sealed class SendSchedule
    {
        public long Next { get; set; }
        public long LastAttempt { get; set; }
        public int Attempts { get; set; }
    }
    private sealed class Scan(Library library, IReadOnlyList<Guid> ids, long seq, State state)
    {
        public Library Library { get; } = library;
        public IReadOnlyList<Guid> Ids { get; } = ids;
        public HashSet<Guid> Present { get; } = ids.ToHashSet();
        public HashSet<string> Known { get; } = state.DiscoveryKnowledge();
        public long StartSeq { get; } = seq;
        public int Position { get; set; }
        public bool Failed { get; set; }
    }
}

public static class Heartbeat
{
    public static byte[] Encode(string server, State state, long now, string error, bool tooManyUsers, long bytes)
    {
        string[] allowed = ["none", "relayUnreachable", "unauthorized", "tooManyUsers", "indexFull", "stateRecovered", "inventoryIncomplete", "itemEvaluationFailed", "reconcileFailed", "invalidConfiguration", "adapterUnavailable", "stateUnavailable"];
        if (!allowed.Contains(error)) throw new InvalidDataException("Invalid health code");
        if (tooManyUsers) error = "tooManyUsers"; else if (state.Health.IndexFull) error = "indexFull";
        using var output = new MemoryStream();
        using (var w = new Utf8JsonWriter(output))
        {
            w.WriteStartObject(); w.WriteNumber("v", 2); w.WriteString("server", server); w.WriteString("version", "1.0.1");
            w.WriteNumber("outboxDepth", state.Notices.Values.Count(n => n.Stage == "outbox"));
            w.WriteNumber("oldestPendingAgeS", state.Notices.Count == 0 ? 0 : Math.Max(0, now - state.Notices.Values.Min(n => n.QueuedAt)));
            w.WriteNumber("droppedSinceLast", state.Health.Dropped);
            if (state.Health.LastAckAt is { } ack) w.WriteNumber("lastAckAt", ack); else w.WriteNull("lastAckAt");
            w.WriteString("errorCode", error); w.WriteBoolean("indexFull", state.Health.IndexFull); w.WriteBoolean("tooManyUsers", tooManyUsers);
            w.WriteNumber("indexCount", state.Index.Count); w.WriteNumber("stateBytes", bytes);
            w.WriteNumber("baselineLibrariesRemaining", state.Libraries.Values.Count(l => !l.BaselineComplete));
            w.WriteEndObject();
        }
        return output.ToArray();
    }
}
