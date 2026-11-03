using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellySeeNotifications;

// Called exclusively by NotificationService's one loop. No locks, tasks, timers or network here.
public sealed class Engine
{
    private readonly Journal journal;
    private readonly INotificationLibrary source;
    private readonly string serverKey;
    public Engine(Journal journal, IJellyfinCompat compat, string serverKey) : this(journal, new DirectLibrary(compat, serverKey), serverKey) { }
    internal Engine(Journal journal, INotificationLibrary source, string serverKey) { this.journal = journal; this.source = source; this.serverKey = serverKey; }
    internal Func<bool> BudgetAvailable { get; set; } = () => true;
    internal bool WorkPending { get; private set; }
    internal void ResetPendingWork() => WorkPending = false;
    private readonly Dictionary<string, AudienceCursor> summaryAudiences = new(StringComparer.Ordinal);
    private sealed class AudienceCursor(string[] items)
    {
        internal string[] Items = items;
        internal int Next;
        internal readonly Queue<string> Pending = new();
        internal readonly Dictionary<string, long> Proofs = new(StringComparer.Ordinal);
    }
    public State State => journal.State;
    private void CancelNotice(string eventId)
    {
        if (!State.Notices.TryGetValue(eventId, out var n)) return;
        if (n.Op == "summary")
        {
            if (n.OutageId is { } outage && summaryAudiences.Remove(outage, out var cursor))
                foreach (string id in cursor.Pending.Distinct(StringComparer.Ordinal)) source.CancelAudience(id, []);
        }
        else
        {
            if (n.Op == "add") source.CancelRead(n.Id!);
            source.CancelAudience(n.Op == "add" ? n.Id : null, n.Folders);
        }
    }
    private void Retire(string eventId) { CancelNotice(eventId); journal.Commit(new Change { Kind = "retire", Key = eventId }); }
    public void Drop(string eventId)
    {
        CancelNotice(eventId);
        journal.Commit(new Change() { Kind = "retire", Key = eventId }, new() { Kind = "health", Health = State.Health with { Dropped = State.Health.Dropped + 1 } });
    }
    public void Put(Notice notice)
    {
        if (!State.Notices.ContainsKey(notice.EventId)) notice = notice with { EnqueuedSeq = State.Seq + 1 };
        bool outbox = notice.Stage == "outbox";
        if (!State.Notices.ContainsKey(notice.EventId) && State.Notices.Values.Count(n => (n.Stage == "outbox") == outbox) >= State.Capacity)
            Drop(Oldest(outbox).EventId);
        // Moving between queues can also fill the destination.
        else if (State.Notices.TryGetValue(notice.EventId, out var old) && old.Stage != notice.Stage &&
            State.Notices.Values.Count(n => (n.Stage == "outbox") == outbox) >= State.Capacity)
            Drop(Oldest(outbox).EventId);
        journal.Commit(new Change() { Kind = "notice", Notice = notice });
    }
    public int Life(string id) => Math.Max(State.Lifecycles.GetValueOrDefault(id), State.Index.TryGetValue(id, out var title) ? title.Life : 0);
    public void QueueAdd(string id, string type, string eventId, long now, string setup, string origin = "live", bool folded = false)
    {
        if (State.Notices.Values.Any(n => n.Op == "add" && n.Id == id && n.Life == Life(id))) return;
        var notice = new Notice { EventId = eventId, Id = id, Type = type, QueuedAt = now, Life = Life(id), SetupHash = setup, Origin = origin, FirstSeenAt = now, Quiet = folded, Folded = folded };
        Put(notice);
        if (origin == "recovery" || folded)
        {
            try
            {
                if (source.Read(id, now, now) is { } item && !PotentialMove(notice, item.Title)) RecoverAddition(notice, now);
            }
            catch (Exception ex) when (ex is not (StatePersistenceException or OutOfMemoryException or OperationCanceledException)) { /* AdvanceAdds retries unknown metadata. */ }
        }
    }
    private bool PotentialMove(Notice notice, Title title)
    {
        if (string.IsNullOrEmpty(title.ProviderDigest)) return false;
        var addition = new Addition(notice.Id!, title.Type, title.Folders, title.ProviderDigest, notice.FirstSeenAt, notice.SetupHash);
        return State.Index.Values.Any(t => t.Id != notice.Id && t.Type == title.Type && t.ProviderDigest == title.ProviderDigest) || State.Notices.Values.Any(n => n.Op == "rm" && Rules.Upgrade(n, addition));
    }
    public void Index(BaseItem item, long passSeq = long.MaxValue) => IndexMany([item], passSeq);
    public void IndexMany(IEnumerable<BaseItem> items, long passSeq, Action<string>? error = null)
    {
        List<Change> batch = [];
        foreach (var item in items)
        {
            try
            {
                var snapshot = source.Read(item.Id.ToString("N"), 0, 0);
                if (snapshot is null || State.Tombstones.TryGetValue(snapshot.Title.Id, out var dead) && dead.Seq > passSeq) continue;
                if (!State.Index.ContainsKey(snapshot.Title.Id) && State.Index.Count + batch.Count >= State.IndexCapacity) continue;
                batch.Add(new Change { Kind = "title", Title = snapshot.Title with { Seq = State.Seq + 1, Life = Life(snapshot.Title.Id) } });
                if (batch.Count == 100) { journal.Commit(batch.ToArray()); batch.Clear(); }
            }
            catch (LibraryPendingException) { WorkPending = true; }
            catch (Exception ex) when (ex is not (StatePersistenceException or OutOfMemoryException or OperationCanceledException)) { error?.Invoke("itemEvaluationFailed"); }
        }
        if (batch.Count != 0) journal.Commit(batch.ToArray());
    }
    internal void Index(Title title, long passSeq = long.MaxValue)
    {
        if (State.Tombstones.TryGetValue(title.Id, out var dead) && dead.Seq > passSeq) return;
        if (!State.Index.ContainsKey(title.Id) && State.Index.Count >= State.IndexCapacity)
        { if (!State.Health.IndexFull) journal.Commit(new Change { Kind = "health", Health = State.Health with { IndexFull = true } }); return; }
        var described = title with { Seq = State.Seq + 1, Life = Life(title.Id) };
        if (State.Index.TryGetValue(title.Id, out var existing) && existing.Type == described.Type && existing.ProviderDigest == described.ProviderDigest && existing.Folders.SequenceEqual(described.Folders)) return;
        journal.Commit(new Change { Kind = "title", Title = described });
    }
    public void Remove(string id, string eventId, long now, string setup, string origin = "live", bool quiet = false)
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
            Folders = title.Folders, ProviderDigest = title.ProviderDigest, Life = life, QueuedAt = now, SetupHash = setup, Origin = origin, FirstSeenAt = now, Quiet = quiet, EnqueuedSeq = State.Seq + 1
        };
        if (removal is not null && State.Notices.Values.Count(n => n.Stage != "outbox") >= State.Capacity)
            Drop(Oldest(false).EventId);
        List<Change> changes = [new() { Kind = "remove", Tombstone = tomb }];
        if (removal is not null) changes.Add(new() { Kind = "notice", Notice = removal });
        foreach (var pending in State.Notices.Values.Where(n => n.Id == id && n.Op == "add" && n.Life < life).ToArray())
        { CancelNotice(pending.EventId); changes.Add(new() { Kind = "retire", Key = pending.EventId }); }
        // Tombstone, occurrence and retirement of the old pending addition are one durable transition.
        journal.Commit(changes.ToArray());
    }
    public void AdvanceAdds(long now, Action<string> error, string? onlyId = null)
    {
        foreach (var pending in State.Notices.Values.Where(n => n.Stage == "pendingAdd" && (onlyId == null || n.Id == onlyId)).ToArray())
        {
            var n = ClockSafe(pending);
            if (!BudgetAvailable()) { WorkPending = true; break; }
            try
            {
                var item = source.Read(n.Id!, n.FirstSeenAt, now);
                if (item is null) { Retire(n.EventId); continue; }
                // Fingerprint a discovered replacement before readiness can delay it.
                var description = item.Title;
                if (n.ProviderDigest != description.ProviderDigest || !n.Folders.SequenceEqual(description.Folders))
                    Put(n with { ProviderDigest = description.ProviderDigest, Folders = description.Folders });
                if (!State.Additions.ContainsKey(n.Id!) && State.Additions.Count >= State.Capacity)
                    journal.Commit(new Change() { Kind = "unaddition", Key = State.Additions.Values.MinBy(a => a.At)!.Id });
                // Discovery time is the occurrence time, even when metadata took minutes.
                var fingerprint = new Addition(n.Id!, description.Type, description.Folders, description.ProviderDigest, n.FirstSeenAt, n.SetupHash);
                if (!State.Additions.TryGetValue(n.Id!, out var previous) || previous.At != fingerprint.At || previous.SetupHash != fingerprint.SetupHash ||
                    previous.Type != fingerprint.Type || previous.ProviderDigest != fingerprint.ProviderDigest || !previous.Folders.SequenceEqual(fingerprint.Folders))
                    journal.Commit(new Change() { Kind = "addition", Addition = fingerprint });
                var removal = State.Notices.Values.FirstOrDefault(r => r.Op == "rm" && Rules.Upgrade(r, fingerprint));
                bool quiet = n.Quiet || removal is not null;
                if (removal is not null)
                {
                    n = n with { Quiet = true, Folded = false, ProviderDigest = description.ProviderDigest, Folders = description.Folders };
                    List<Change> quietChanges = [new() { Kind = "notice", Notice = removal with { Quiet = true } }, new() { Kind = "notice", Notice = n }];
                    // Metadata may acquire its digest after discovery. Retract this occurrence's provisional
                    // recovery candidate when it becomes a proven move, before any summary is emitted.
                    if (n.OutageId is not null && State.Recoveries.TryGetValue(n.SetupHash, out var recovery) && recovery.Id == n.OutageId && !recovery.Emitted &&
                        State.RecoveryItems.TryGetValue(n.OutageId + ":" + n.Id, out var candidate) && candidate.EventId == n.EventId)
                    {
                        quietChanges.Add(new() { Kind = "unrecoveryItem", Key = candidate.Key });
                        quietChanges.Add(new() { Kind = "recovery", Recovery = recovery with { HasAddition = State.RecoveryItems.Values.Any(r => r.OutageId == recovery.Id && r.Key != candidate.Key) } });
                    }
                    journal.Commit(quietChanges.ToArray());
                }
                bool duplicate = description.ProviderDigest is not null && State.Index.Values.Any(t => t.Id != n.Id && t.Type == description.Type && t.ProviderDigest == description.ProviderDigest);
                if (!n.Folded && !quiet && duplicate && n.HoldUntil == 0)
                { Put(n with { HoldUntil = n.FirstSeenAt + 1800, HoldForScan = source.ScanRunning }); continue; }
                if (!n.Folded && !quiet && duplicate && (n.HoldForScan ? source.ScanRunning && now < n.HoldUntil : now < n.FirstSeenAt + Rules.MoveWindow)) continue;
                if ((!quiet || n.Folded) && !Rules.Audible(n, now))
                { RecoverAddition(n, now); n = State.Notices[n.EventId]; }
                if (!item.Ready && !n.Folded && now - n.FirstSeenAt < 900) continue;
                Index(description);
                var audience = source.Audience(n.Id, description.Folders, now);
                if (audience.Length == 0)
                {
                    if (Rules.AudienceExpired(n, now)) Drop(n.EventId);
                    continue;
                }
                Put(n with { Stage = "outbox", ReadyAt = now, Folders = description.Folders, ProviderDigest = description.ProviderDigest, Series = item.Series, Audience = audience, At = source.AudienceEvaluatedAt(n.Id, description.Folders, now), Quiet = quiet });
            }
            catch (LibraryPendingException) { WorkPending = true; }
            catch (LibraryCallException) { error("requiredCallTimeout"); }
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
            .Select(n => new Addition(n.Id!, n.Type!, n.Folders, n.ProviderDigest, n.FirstSeenAt, n.SetupHash))).ToArray();
        foreach (var original in pending)
        {
            if (!BudgetAvailable()) { WorkPending = true; return; }
            var n = ClockSafe(original);
            if (!n.Quiet && (!Rules.Audible(n, now) || replacements.Any(a => Rules.Upgrade(n, a)))) Put(n with { Quiet = true });
        }
        // Keep a whole ten-minute window until every member can be upgrade-checked. This avoids
        // splitting a burst at the exact ten-minute boundary and missing the >200 threshold.
        foreach (var route in State.Notices.Values.Where(n => n.Stage == "pendingRemove").SelectMany(n => n.Folders.Select(f => (Folder: f, Setup: n.SetupHash, n.Origin, n.Quiet))).Distinct().ToArray())
        {
            string folder = route.Folder;
            if (!BudgetAvailable()) { WorkPending = true; break; }
            var candidates = State.Notices.Values.Where(n => n.Stage == "pendingRemove" && n.SetupHash == route.Setup && n.Origin == route.Origin && n.Quiet == route.Quiet && n.Folders.Contains(folder)).OrderBy(n => n.QueuedAt).ToArray();
            if (candidates.Length == 0) continue;
            long start = candidates[0].QueuedAt;
            var window = candidates.Where(n => n.QueuedAt <= start + 600).ToArray();
            if (now < window[^1].QueuedAt + 600 || !Rules.Bulk(window, folder)) continue;
            try
            {
                var audience = source.Audience(null, [folder], now);
                if (audience.Length == 0 && now - start < 1800) continue;
                Notice bulk = new()
                {
                    EventId = window[0].EventId, Op = "rmbulk", Stage = "outbox", Folders = [folder], Count = window.Length,
                    QueuedAt = start, ReadyAt = now, EnqueuedSeq = window.Min(n => n.EnqueuedSeq), FirstSeenAt = window.Min(n => n.FirstSeenAt), Origin = route.Origin, Quiet = route.Quiet, At = source.AudienceEvaluatedAt(null, [folder], now), Life = checked(Life(folder) + 1), Audience = audience, SetupHash = window[0].SetupHash
                };
                // A compact, deterministic rewrite consumes the complete window atomically.
                EnsureOutboxSlot();
                journal.Commit(new Change() { Kind = "bulk", Notice = bulk, Key = (start + 600).ToString(System.Globalization.CultureInfo.InvariantCulture) }, new() { Kind = "life", Lifecycle = new(folder, bulk.Life) });
                if (audience.Length == 0) Drop(bulk.EventId);
            }
            catch (LibraryPendingException) { WorkPending = true; }
            catch (LibraryCallException) { error("requiredCallTimeout"); }
            catch (TooManyUsersException) { error("tooManyUsers"); return; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or StatePersistenceException)) { error("itemEvaluationFailed"); }
        }
        foreach (var n in State.Notices.Values.Where(n => n.Stage == "pendingRemove" && now >= n.QueuedAt + 600).ToArray())
        {
            if (!BudgetAvailable()) { WorkPending = true; break; }
            // If still accumulating toward a bulk window, wait until its newest member's hold ends.
            if (n.Folders.Any(f =>
            {
                var peers = State.Notices.Values.Where(p => p.Stage == "pendingRemove" && p.SetupHash == n.SetupHash && p.Origin == n.Origin && p.Quiet == n.Quiet && p.Folders.Contains(f) && p.QueuedAt >= n.QueuedAt && p.QueuedAt <= n.QueuedAt + 600).ToArray();
                return peers.Length > 200 && peers.Any(p => now < p.QueuedAt + 600);
            })) continue;
            try
            {
                var audience = source.Audience(null, n.Folders, now);
                if (audience.Length == 0) { if (Rules.AudienceExpired(n, now)) Drop(n.EventId); continue; }
                Put(n with { Stage = "outbox", ReadyAt = now, Audience = audience, At = source.AudienceEvaluatedAt(null, n.Folders, now) });
            }
            catch (LibraryPendingException) { WorkPending = true; }
            catch (LibraryCallException) { error("requiredCallTimeout"); }
            catch (TooManyUsersException) { error("tooManyUsers"); return; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or StatePersistenceException)) { error("itemEvaluationFailed"); }
        }
    }
    private Notice Oldest(bool outbox) => State.Notices.Values.Where(n => (n.Stage == "outbox") == outbox)
        .OrderBy(n => n.Op != "summary" && (n.Folded || n.EnqueuedSeq <= State.Health.QuietThroughSeq) ? 0 : 1).ThenBy(n => n.QueuedAt).First();
    internal void EvictOutbox() => Drop(Oldest(true).EventId);
    private void EnsureOutboxSlot()
    {
        if (State.Notices.Values.Count(n => n.Stage == "outbox") >= State.Capacity)
            EvictOutbox();
    }
    private Notice ClockSafe(Notice n)
    {
        if (n.Op != "summary" && !n.Quiet && n.EnqueuedSeq <= State.Health.QuietThroughSeq)
        { var safe = n with { Quiet = true, Folded = n.Op == "add" }; Put(safe); return safe; }
        return n;
    }
    internal void InvalidateAudienceCursors() => summaryAudiences.Clear();
    public Notice? Freshen(Notice n, long now)
    {
        if (!State.Notices.TryGetValue(n.EventId, out var current)) return null;
        n = ClockSafe(current);
        if (n.Op == "summary")
        {
            // Always re-evaluate at submission, including retries; unknown is omitted.
            var audience = RecoveryAudience(n.OutageId!, now);
            var freshSummary = n with { At = audience.At, Audience = audience.Users }; Put(freshSummary);
            return freshSummary;
        }
        if (n.Op == "add" && n.Folded) { RecoverAddition(n, now); Retire(n.EventId); return null; }
        if (!Rules.Audible(n, now) && !n.Quiet && n.Op == "add")
        {
            RecoverAddition(n, now);
            Retire(n.EventId); return null;
        }
        if (!Rules.Audible(n, now)) n = n with { Quiet = true };
        if (!Rules.Stale(n.At, now)) return n;
        if (n.Op == "rmlibrary") { var freshLibrary = n with { At = now }; Put(freshLibrary); return freshLibrary; }
        var item = n.Op == "add" ? source.Read(n.Id!, n.FirstSeenAt, now) : null;
        if (n.Op == "add" && item is null) { Drop(n.EventId); return null; }
        string[] audienceNow = source.Audience(n.Op == "add" ? n.Id : null, n.Folders, now);
        if (audienceNow.Length == 0) { if (Rules.AudienceExpired(n, now)) Drop(n.EventId); return null; }
        var fresh = n with { At = source.AudienceEvaluatedAt(n.Op == "add" ? n.Id : null, n.Folders, now), Audience = audienceNow }; Put(fresh); return fresh;
    }
    internal void PruneSetups(IReadOnlySet<string> available)
    {
        var retired = State.Recoveries.Keys.Concat(State.Bindings.Values.Select(b => b.SetupHash)).Concat(State.Notices.Values.Select(n => n.SetupHash))
            .Distinct(StringComparer.Ordinal).Where(s => !available.Contains(s)).ToArray();
        foreach (string setup in retired)
        {
            foreach (var n in State.Notices.Values.Where(n => n.SetupHash == setup)) CancelNotice(n.EventId);
            long dropped = State.Notices.Values.Count(n => n.SetupHash == setup);
            journal.Commit(new Change { Kind = "unsetup", Key = setup }, new() { Kind = "health", Health = State.Health with { Dropped = State.Health.Dropped + dropped } });
        }
    }
    internal Recovery BeginRecovery(string setup, long now)
    {
        if (State.Recoveries.TryGetValue(setup, out var ongoing) && !ongoing.Complete) return ongoing;
        // Keep the preceding outage until its summary has drained. Reopening cannot create a second envelope.
        if (ongoing is not null && State.Notices.Values.Any(n => n.Op == "summary" && n.OutageId == ongoing.Id)) return ongoing;
        var recovery = new Recovery(Identity.EventId(), setup, now);
        if (ongoing is null) journal.Commit(new Change { Kind = "recovery", Recovery = recovery });
        else journal.Commit(new Change { Kind = "unoutage", Key = ongoing.Id }, new() { Kind = "recovery", Recovery = recovery });
        return recovery;
    }
    internal void RecoverAddition(Notice n, long now)
    {
        if (State.Notices.TryGetValue(n.EventId, out var current)) n = current;
        var recovery = State.Recoveries.GetValueOrDefault(n.SetupHash) ?? BeginRecovery(n.SetupHash, now);
        // Once assigned, delayed metadata/sender work cannot turn the same discovery into another outage.
        if (n.OutageId is not null && n.OutageId != recovery.Id) return;
        if (n.OutageId is null && recovery.Complete) recovery = BeginRecovery(n.SetupHash, now);

        var item = new RecoveryItem(n.Id!, recovery.Id, n.SetupHash, n.EventId);
        List<Change> changes = [];
        if (n.OutageId is null) changes.Add(new() { Kind = "notice", Notice = n with { OutageId = recovery.Id } });
        if (!recovery.HasAddition) changes.Add(new() { Kind = "recovery", Recovery = recovery with { HasAddition = true } });
        if (!State.RecoveryItems.ContainsKey(item.Key) && State.RecoveryItems.Count < State.IndexCapacity) changes.Add(new() { Kind = "recoveryItem", RecoveryItem = item });
        if (changes.Count != 0) journal.Commit(changes.ToArray());
    }
    private (string[] Users, long At) RecoveryAudience(string outage, long now)
    {
        if (!summaryAudiences.TryGetValue(outage, out var cursor))
        {
            if (summaryAudiences.Count >= 20) summaryAudiences.Remove(summaryAudiences.Keys.First());
            cursor = new(State.RecoveryItems.Values.Where(i => i.OutageId == outage).Select(i => i.Id).ToArray());
            summaryAudiences[outage] = cursor;
        }
        void Evaluate(string id)
        {
            try
            {
                var audience = source.Audience(id, [], now);
                long at = source.AudienceEvaluatedAt(id, now);
                foreach (var user in audience) cursor.Proofs[user] = Math.Max(cursor.Proofs.GetValueOrDefault(user), at);
            }
            catch (LibraryPendingException) { cursor.Pending.Enqueue(id); }
            catch (Exception ex) when (ex is not (StatePersistenceException or OutOfMemoryException or OperationCanceledException)) { /* Unknown entitlement grants nothing. */ }
        }
        int queued = cursor.Pending.Count;
        for (int i = 0; i < queued && BudgetAvailable(); i++) Evaluate(cursor.Pending.Dequeue());
        while (cursor.Next < cursor.Items.Length && cursor.Pending.Count < 16 && BudgetAvailable()) Evaluate(cursor.Items[cursor.Next++]);
        if (cursor.Pending.Count != 0 || cursor.Next != cursor.Items.Length) throw new LibraryPendingException();
        var latest = State.RecoveryItems.Values.Where(i => i.OutageId == outage).Select(i => i.Id).ToArray();
        if (latest.Length > cursor.Items.Length) { cursor.Items = latest; throw new LibraryPendingException(); }
        summaryAudiences.Remove(outage);
        // A long census cannot refresh old positive grants merely by finishing later.
        var valid = cursor.Proofs.Where(p => !Rules.Stale(p.Value, now)).ToArray();
        return (valid.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray(), valid.Length == 0 ? now : Math.Min(now, valid.Min(p => p.Value)));
    }
    internal void AdvanceRecovery(string setup, long now, bool complete)
    {
        if (!State.Recoveries.TryGetValue(setup, out var recovery)) return;
        if (complete && !recovery.Complete) { recovery = recovery with { Complete = true }; journal.Commit(new Change { Kind = "recovery", Recovery = recovery }); }
        if (!recovery.HasAddition || recovery.Emitted || !complete && now - recovery.CutoffAt < 600) return;
        var profiles = State.Bindings.Values.Where(b => b.SetupHash == setup).Select(b => b.Profile).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (profiles.Length == 0) return; // Retain until a bound profile exists; never invent a recipient.
        // Persist the complete transport envelope set and emission marker atomically in bounded chunks.
        foreach (string profile in profiles)
        {
            if (!BudgetAvailable()) return;
            string eventId = Identity.Hash(recovery.Id + ":" + profile)[..32];
            if (State.Notices.ContainsKey(eventId)) continue;
            Put(new Notice { EventId = eventId, Op = "summary", Stage = "outbox", ReadyAt = now, SetupHash = setup, QueuedAt = now, FirstSeenAt = recovery.StartedAt, Origin = "recovery", OutageStart = recovery.StartedAt, OutageId = recovery.Id, Profile = profile, PresentationKey = Identity.PresentationKey(serverKey, profile), At = now });
        }
        journal.Commit(new Change { Kind = "recovery", Recovery = recovery with { Emitted = true } });
    }
    public void Sweep(long now)
    {
        foreach (var n in State.Notices.Values.Where(n => Rules.Expired(n, now)).ToArray()) { if (!BudgetAvailable()) { WorkPending = true; break; } Drop(n.EventId); }
        foreach (var t in State.Tombstones.Values.Where(t => now - t.At >= 86400).ToArray()) { if (!BudgetAvailable()) { WorkPending = true; break; } journal.Commit(new Change() { Kind = "untombstone", Key = t.Id }); }
        // Allow delayed ready additions to match older removals; keep 20 minutes of history.
        foreach (var a in State.Additions.Values.Where(a => now - a.At > 1800).ToArray()) { if (!BudgetAvailable()) { WorkPending = true; break; } journal.Commit(new Change() { Kind = "unaddition", Key = a.Id }); }
    }
    public void Acknowledge(HashSet<string> ids, long now)
    {
        foreach (var chunk in ids.Chunk(200))
        {
            foreach (string id in chunk) CancelNotice(id);
            journal.Commit(chunk.Select(id => new Change { Kind = "retire", Key = id }).Append(new() { Kind = "health", Health = State.Health with { LastAckAt = now } }).ToArray());
        }
    }
}
