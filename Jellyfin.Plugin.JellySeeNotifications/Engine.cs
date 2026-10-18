using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellySeeNotifications;

// Called exclusively by NotificationService's one loop. No locks, tasks, timers or network here.
public sealed class Engine(Journal journal, IJellyfinCompat compat, string serverKey)
{
    public State State => journal.State;
    public void Drop(string eventId)
    {
        journal.Commit(new Change() { Kind = "retire", Key = eventId }, new() { Kind = "health", Health = State.Health with { Dropped = State.Health.Dropped + 1 } });
    }
    public void Put(Notice notice)
    {
        bool outbox = notice.Stage == "outbox";
        if (!State.Notices.ContainsKey(notice.EventId) && State.Notices.Values.Count(n => (n.Stage == "outbox") == outbox) >= State.Capacity)
            Drop(State.Notices.Values.Where(n => (n.Stage == "outbox") == outbox).MinBy(n => n.QueuedAt)!.EventId);
        // Moving between queues can also fill the destination.
        else if (State.Notices.TryGetValue(notice.EventId, out var old) && old.Stage != notice.Stage &&
            State.Notices.Values.Count(n => (n.Stage == "outbox") == outbox) >= State.Capacity)
            Drop(State.Notices.Values.Where(n => (n.Stage == "outbox") == outbox).MinBy(n => n.QueuedAt)!.EventId);
        journal.Commit(new Change() { Kind = "notice", Notice = notice });
    }
    public int Life(string id) => Math.Max(State.Lifecycles.GetValueOrDefault(id), State.Index.TryGetValue(id, out var title) ? title.Life : 0);
    public void QueueAdd(string id, string type, string eventId, long now, string setup)
    {
        if (State.Notices.Values.Any(n => n.Op == "add" && n.Id == id && n.Life == Life(id))) return;
        Put(new() { EventId = eventId, Id = id, Type = type, QueuedAt = now, Life = Life(id), SetupHash = setup });
    }
    public void Index(BaseItem item, long passSeq = long.MaxValue) => IndexMany([item], passSeq);
    public void IndexMany(IEnumerable<BaseItem> items, long passSeq, Action<string>? error = null)
    {
        List<Change> changes = [];
        int added = 0;
        foreach (var item in items)
        {
            string id = item.Id.ToString("N");
            if (!compat.IsTitle(item) || State.Tombstones.TryGetValue(id, out var dead) && dead.Seq > passSeq) continue;
            bool exists = State.Index.TryGetValue(id, out var existing);
            if (!exists && State.Index.Count + added >= State.IndexCapacity)
            {
                if (!State.Health.IndexFull && !changes.Any(c => c.Kind == "health")) changes.Add(new() { Kind = "health", Health = State.Health with { IndexFull = true } });
                continue;
            }
            Title described;
            try { described = compat.Describe(item, State.Seq + 1, Life(id)); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException)) { error?.Invoke("itemEvaluationFailed"); continue; }
            if (exists && existing!.Type == described.Type && existing.ProviderDigest == described.ProviderDigest && existing.Folders.SequenceEqual(described.Folders)) continue;
            changes.Add(new() { Kind = "title", Title = described });
            if (!exists) added++;
            if (State.Index.Count + added == State.IndexCapacity && !State.Health.IndexFull && !changes.Any(c => c.Kind == "health"))
                changes.Add(new() { Kind = "health", Health = State.Health with { IndexFull = true } });
            if (changes.Count >= 100) { journal.Commit(changes.ToArray()); changes.Clear(); added = 0; }
        }
        if (changes.Count != 0) journal.Commit(changes.ToArray());
    }
    public void Remove(string id, string eventId, long now, string setup)
    {
        // Duplicate descendant/root-reconcile removals do not advance a lifecycle twice.
        if (!State.Index.TryGetValue(id, out var title) && State.Tombstones.ContainsKey(id) && !State.Notices.Values.Any(n => n.Id == id && n.Op == "add" && n.Life == Life(id))) return;
        int life = checked(Life(id) + 1);
        if (State.Tombstones.Count >= State.IndexCapacity && !State.Tombstones.ContainsKey(id))
            journal.Commit(new Change() { Kind = "untombstone", Key = State.Tombstones.Values.MinBy(t => t.At)!.Id });
        var tomb = new Tombstone(id, State.Seq + 1, now, life);
        Notice? removal = title is null ? null : new()
        {
            EventId = eventId, Op = "rm", Stage = "pendingRemove", Id = id, Type = title.Type,
            Folders = title.Folders, ProviderDigest = title.ProviderDigest, Life = life, QueuedAt = now, SetupHash = setup
        };
        if (removal is not null && State.Notices.Values.Count(n => n.Stage != "outbox") >= State.Capacity)
            Drop(State.Notices.Values.Where(n => n.Stage != "outbox").MinBy(n => n.QueuedAt)!.EventId);
        List<Change> changes = [new() { Kind = "remove", Tombstone = tomb }];
        if (removal is not null) changes.Add(new() { Kind = "notice", Notice = removal });
        foreach (var pending in State.Notices.Values.Where(n => n.Id == id && n.Stage == "pendingAdd").ToArray())
            changes.Add(new() { Kind = "retire", Key = pending.EventId });
        // Tombstone, occurrence and retirement of the old pending addition are one durable transition.
        journal.Commit(changes.ToArray());
    }
    public void AdvanceAdds(long now, Action<string> error)
    {
        foreach (var n in State.Notices.Values.Where(n => n.Stage == "pendingAdd").ToArray())
        {
            try
            {
                var item = compat.Get(n.Id!);
                if (item is null || !compat.IsTitle(item)) { journal.Commit(new Change() { Kind = "retire", Key = n.EventId }); continue; }
                // Fingerprint a discovered replacement before readiness can delay it.
                var description = compat.Describe(item, State.Seq, n.Life);
                if (n.ProviderDigest != description.ProviderDigest || !n.Folders.SequenceEqual(description.Folders))
                    Put(n with { ProviderDigest = description.ProviderDigest, Folders = description.Folders });
                if (!State.Additions.ContainsKey(n.Id!) && State.Additions.Count >= State.Capacity)
                    journal.Commit(new Change() { Kind = "unaddition", Key = State.Additions.Values.MinBy(a => a.At)!.Id });
                // Discovery time is the occurrence time, even when metadata took minutes.
                var fingerprint = new Addition(n.Id!, description.Type, description.Folders, description.ProviderDigest, n.QueuedAt, n.SetupHash);
                if (!State.Additions.TryGetValue(n.Id!, out var previous) || previous.At != fingerprint.At || previous.SetupHash != fingerprint.SetupHash ||
                    previous.Type != fingerprint.Type || previous.ProviderDigest != fingerprint.ProviderDigest || !previous.Folders.SequenceEqual(fingerprint.Folders))
                    journal.Commit(new Change() { Kind = "addition", Addition = fingerprint });
                if (!compat.Ready(item, n.QueuedAt, now)) continue;
                Index(item);
                var audience = compat.Audience(item, description.Folders, serverKey);
                if (audience.Length == 0)
                {
                    if (Rules.AudienceExpired(n, now)) Drop(n.EventId);
                    continue;
                }
                Put(n with { Stage = "outbox", Folders = description.Folders, ProviderDigest = description.ProviderDigest, Series = compat.Series(item), Audience = audience, At = now });
            }
            catch (TooManyUsersException) { error("tooManyUsers"); return; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or StatePersistenceException)) { error("itemEvaluationFailed"); }
        }
    }
    public void AdvanceRemovals(long now, Action<string> error)
    {
        var pending = State.Notices.Values.Where(n => n.Stage == "pendingRemove").OrderBy(n => n.QueuedAt).ToArray();
        // Pending fingerprints also survive history eviction while readiness is
        // delayed; they have their own bounded queue and persisted occurrence time.
        var replacements = State.Additions.Values.Concat(State.Notices.Values.Where(n => n.Stage == "pendingAdd")
            .Select(n => new Addition(n.Id!, n.Type!, n.Folders, n.ProviderDigest, n.QueuedAt, n.SetupHash))).ToArray();
        foreach (var n in pending)
            if (replacements.Any(a => Rules.Upgrade(n, a))) journal.Commit(new Change() { Kind = "retire", Key = n.EventId });
        // Keep a whole ten-minute window until every member can be upgrade-checked. This avoids
        // splitting a burst at the exact ten-minute boundary and missing the >200 threshold.
        foreach (var route in State.Notices.Values.Where(n => n.Stage == "pendingRemove").SelectMany(n => n.Folders.Select(f => (Folder: f, Setup: n.SetupHash))).Distinct().ToArray())
        {
            string folder = route.Folder;
            var candidates = State.Notices.Values.Where(n => n.Stage == "pendingRemove" && n.SetupHash == route.Setup && n.Folders.Contains(folder)).OrderBy(n => n.QueuedAt).ToArray();
            if (candidates.Length == 0) continue;
            long start = candidates[0].QueuedAt;
            var window = candidates.Where(n => n.QueuedAt <= start + 600).ToArray();
            if (now < window[^1].QueuedAt + 600 || !Rules.Bulk(window, folder)) continue;
            try
            {
                var audience = compat.Audience(null, [folder], serverKey);
                if (audience.Length == 0 && now - start < 1800) continue;
                Notice bulk = new()
                {
                    EventId = window[0].EventId, Op = "rmbulk", Stage = "outbox", Folders = [folder], Count = window.Length,
                    QueuedAt = start, At = now, Life = checked(Life(folder) + 1), Audience = audience, SetupHash = window[0].SetupHash
                };
                // A compact, deterministic rewrite consumes the complete window atomically.
                EnsureOutboxSlot();
                journal.Commit(new Change() { Kind = "bulk", Notice = bulk, Key = (start + 600).ToString(System.Globalization.CultureInfo.InvariantCulture) }, new() { Kind = "life", Lifecycle = new(folder, bulk.Life) });
                if (audience.Length == 0) Drop(bulk.EventId);
            }
            catch (TooManyUsersException) { error("tooManyUsers"); return; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or StatePersistenceException)) { error("itemEvaluationFailed"); }
        }
        foreach (var n in State.Notices.Values.Where(n => n.Stage == "pendingRemove" && now >= n.QueuedAt + 600).ToArray())
        {
            // If still accumulating toward a bulk window, wait until its newest member's hold ends.
            if (n.Folders.Any(f =>
            {
                var peers = State.Notices.Values.Where(p => p.Stage == "pendingRemove" && p.SetupHash == n.SetupHash && p.Folders.Contains(f) && p.QueuedAt >= n.QueuedAt && p.QueuedAt <= n.QueuedAt + 600).ToArray();
                return peers.Length > 200 && peers.Any(p => now < p.QueuedAt + 600);
            })) continue;
            try
            {
                var audience = compat.Audience(null, n.Folders, serverKey);
                if (audience.Length == 0) { if (Rules.AudienceExpired(n, now)) Drop(n.EventId); continue; }
                Put(n with { Stage = "outbox", Audience = audience, At = now });
            }
            catch (TooManyUsersException) { error("tooManyUsers"); return; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or StatePersistenceException)) { error("itemEvaluationFailed"); }
        }
    }
    private void EnsureOutboxSlot()
    {
        if (State.Notices.Values.Count(n => n.Stage == "outbox") >= State.Capacity)
            Drop(State.Notices.Values.Where(n => n.Stage == "outbox").MinBy(n => n.QueuedAt)!.EventId);
    }
    public Notice? Freshen(Notice n, long now)
    {
        if (!Rules.Stale(n.At, now)) return n;
        if (n.Op == "rmlibrary") { var freshLibrary = n with { At = now }; Put(freshLibrary); return freshLibrary; }
        BaseItem? item = n.Op == "add" ? compat.Get(n.Id!) : null;
        if (n.Op == "add" && (item is null || !compat.IsTitle(item))) { Drop(n.EventId); return null; }
        string[] audience = compat.Audience(item, n.Folders, serverKey);
        if (audience.Length == 0) { if (Rules.AudienceExpired(n, now)) Drop(n.EventId); return null; }
        var fresh = n with { At = now, Audience = audience }; Put(fresh); return fresh;
    }
    public void Sweep(long now)
    {
        foreach (var n in State.Notices.Values.Where(n => Rules.Expired(n, now)).ToArray()) Drop(n.EventId);
        foreach (var t in State.Tombstones.Values.Where(t => now - t.At >= 86400).ToArray()) journal.Commit(new Change() { Kind = "untombstone", Key = t.Id });
        // Allow delayed ready additions to match older removals; keep 20 minutes of history.
        foreach (var a in State.Additions.Values.Where(a => now - a.At > 1800).ToArray()) journal.Commit(new Change() { Kind = "unaddition", Key = a.Id });
    }
    public void Acknowledge(HashSet<string> ids, long now)
    {
        foreach (var chunk in ids.Chunk(200)) journal.Commit(chunk.Select(id => new Change { Kind = "retire", Key = id }).Append(new() { Kind = "health", Health = State.Health with { LastAckAt = now } }).ToArray());
    }
}
