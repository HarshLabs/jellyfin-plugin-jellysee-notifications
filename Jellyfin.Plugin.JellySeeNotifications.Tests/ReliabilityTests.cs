using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;
using static Jellyfin.Plugin.JellySeeNotifications.Tests.ProtocolTests;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class ReliabilityTests
{
    private const string Setup = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string UserHash = "1234567890abcdef";
    private sealed class Model : INotificationLibrary
    {
        internal readonly Dictionary<string, ItemSnapshot> Items = new(StringComparer.Ordinal);
        internal bool Allowed = true;
        internal bool Unknown;
        internal bool ReadPending;
        internal int ReadCalls;
        internal bool AsyncAudience;
        internal long? SampleAt;
        public long AudienceEvaluatedAt(string id, long now) => SampleAt ?? now;
        internal readonly HashSet<string> PendingAudiences = [];
        internal readonly Queue<string> ReadyAudiences = new();
        internal void CompleteAudienceCalls()
        {
            foreach (string id in PendingAudiences.Take(16).ToArray()) { PendingAudiences.Remove(id); ReadyAudiences.Enqueue(id); }
            while (ReadyAudiences.Count > 32) ReadyAudiences.Dequeue();
        }
        public bool ScanRunning { get; set; }
        internal void Add(int id, string? digest = null, string? folder = null) => Items[Id(id)] = new(new(Id(id), "Movie", [folder ?? Id(8)], digest, 0, 0), true, null);
        public ItemSnapshot? Read(string id, long queuedAt, long now) { ReadCalls++; return ReadPending ? throw new LibraryPendingException() : Items.GetValueOrDefault(id); }
        public string[] Audience(string? id, string[] folders, long now)
        {
            if (Unknown) throw new LibraryCallException();
            if (AsyncAudience && id is not null && !ReadyAudiences.Contains(id)) { PendingAudiences.Add(id); throw new LibraryPendingException(); }
            return Allowed && (id is null || Items.ContainsKey(id)) ? [UserHash] : [];
        }
    }
    private static void Bound(Journal j, string profile = "profile") => j.Commit(new Change { Kind = "binding", Binding = new(Id(999), profile, UserHash, Setup) });

    [Theory] [InlineData(null)] [InlineData("aaaaaaaaaaaaaaaa")]
    public void SameIdScanFlapAlertsOnceAndCancelsRemovalRegardlessOfDigest(string? digest)
    {
        using var temp = new TempDirectory();
        var m = new Model(); m.Add(1, digest); long now = 1000;
        using (var j = new Journal(temp.Path))
        {
            var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), now, Setup); e.AdvanceAdds(now, _ => { });
            Assert.False(Assert.Single(j.State.Notices.Values).Quiet); e.Acknowledge([Id(100)], now);
        }
        // Durable history must survive a worker/process restart.
        using var reopened = new Journal(temp.Path); var engine = new Engine(reopened, m, "key");
        for (int scan = 1; scan <= 3; scan++)
        {
            now += 300; engine.Remove(Id(1), Id(200 + scan), now, Setup);
            m.Add(1, scan % 2 == 0 ? digest : null);
            engine.QueueAdd(Id(1), "Movie", Id(300 + scan), now + 1, Setup); engine.AdvanceAdds(now + 1, _ => { });
            engine.AdvanceRemovals(now + 601, _ => { });
            Assert.DoesNotContain(reopened.State.Notices.Values, n => n.Op == "rm");
            Assert.True(Assert.Single(reopened.State.Notices.Values).Quiet);
            engine.Acknowledge([Id(300 + scan)], now + 601);
        }
        Assert.Empty(reopened.State.Recoveries); // Flaps must not create a recovery summary either.
        engine.Remove(Id(1), Id(500), now + 700, Setup); m.Add(2);
        engine.QueueAdd(Id(2), "Movie", Id(501), now + 701, Setup); engine.AdvanceAdds(now + 701, _ => { });
        Assert.False(reopened.State.Notices[Id(501)].Quiet);
    }

    [Theory] [InlineData(false, null)] [InlineData(true, null)]
    [InlineData(false, "aaaaaaaaaaaaaaaa")] [InlineData(true, "aaaaaaaaaaaaaaaa")]
    public void FlapBeforeOriginalAcknowledgementPreservesOneOriginalAlertAcrossRestart(bool ready, string? digest)
    {
        using var temp = new TempDirectory(); var m = new Model(); m.Add(1, digest);
        m.Items[Id(1)] = m.Items[Id(1)] with { Ready = ready };
        using (var j = new Journal(temp.Path))
        {
            var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup);
            e.AdvanceAdds(1001, _ => { }); e.Remove(Id(1), Id(101), 1002, Setup); j.Compact();
        }
        using var reopened = new Journal(temp.Path); var resumed = new Engine(reopened, m, "key");
        resumed.QueueAdd(Id(1), "Movie", Id(102), 1003, Setup); resumed.AdvanceAdds(1004, _ => { }); resumed.AdvanceRemovals(1700, _ => { });
        var original = Assert.Single(reopened.State.Notices.Values, n => n.EventId == Id(100));
        Assert.Equal(2, reopened.State.Notices.Count); Assert.True(reopened.State.Notices[Id(102)].Quiet);
        Assert.Equal(1, original.Life); Assert.Equal(1, reopened.State.Notices[Id(102)].Life);
        Assert.Equal(Id(100), original.EventId); Assert.Equal(1000, original.FirstSeenAt); Assert.Equal("outbox", original.Stage); Assert.False(original.Quiet);
        resumed.Acknowledge([Id(100)], 1701);
        resumed.Remove(Id(1), Id(103), 1702, Setup); resumed.QueueAdd(Id(1), "Movie", Id(104), 1703, Setup); resumed.AdvanceAdds(1704, _ => { });
        Assert.True(Assert.Single(reopened.State.Notices.Values).Quiet);
    }

    [Fact] public void RepeatedRemovalBeforeFirstReaddReadPreservesOriginalAcrossRestart()
    {
        using var temp = new TempDirectory(); var m = new Model(); m.Add(1);
        using (var j = new Journal(temp.Path))
        {
            var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1001, _ => { });
            e.Remove(Id(1), Id(101), 1002, Setup); e.QueueAdd(Id(1), "Movie", Id(199), 1100, Setup); e.Remove(Id(1), Id(102), 1101, Setup); j.Compact();
        }
        using var restored = new Journal(temp.Path); var resumed = new Engine(restored, m, "key");
        resumed.QueueAdd(Id(1), "Movie", Id(103), 1102, Setup); resumed.AdvanceAdds(1103, _ => { });
        Assert.False(restored.State.Notices[Id(100)].Quiet); Assert.Equal(1000, restored.State.Notices[Id(100)].FirstSeenAt);
        Assert.True(restored.State.Notices[Id(103)].Quiet);
    }

    [Fact] public void InterruptedRestoredPairReplaysBothOriginalAndQuietHint()
    {
        using var temp = new TempDirectory(); using var crash = new TempDirectory(); var m = new Model(); m.Add(1);
        using (var j = new Journal(temp.Path))
        {
            var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1001, _ => { });
            e.Remove(Id(1), Id(101), 1002, Setup); e.AdvanceRemovals(1602, _ => { }); e.Acknowledge([Id(101)], 1603);
        }
        using (var j = new Journal(temp.Path)) new Engine(j, m, "key").QueueAdd(Id(1), "Movie", Id(102), 1604, Setup);
        byte[] log = File.ReadAllBytes(Path.Combine(temp.Path, "journal.ndjson"));
        int previousNewline = Array.LastIndexOf(log, (byte)'\n', log.Length - 2);
        File.WriteAllBytes(Path.Combine(crash.Path, "journal.ndjson"), log[..(previousNewline + 1 + (log.Length - previousNewline - 1) / 2)]);
        using var resumed = new Journal(crash.Path); var engine = new Engine(resumed, m, "key");
        engine.QueueAdd(Id(1), "Movie", Id(102), 1604, Setup); engine.AdvanceAdds(1605, _ => { });
        Assert.Equal(2, resumed.State.Notices.Count); Assert.False(resumed.State.Notices[Id(100)].Quiet); Assert.True(resumed.State.Notices[Id(102)].Quiet);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RestoredAddKeepsClockFenceAndHonorsFoldedCallback(bool folded)
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1); Bound(j);
        var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1001, _ => { });
        long sequence = j.State.Notices[Id(100)].EnqueuedSeq; e.Remove(Id(1), Id(101), 1002, Setup);
        if (!folded) j.Commit(new Change { Kind = "health", Health = j.State.Health with { QuietThroughSeq = j.State.Seq } });
        e.QueueAdd(Id(1), "Movie", Id(102), 1100, Setup, folded: folded); e.AdvanceAdds(1101, _ => { });
        var restored = j.State.Notices[Id(100)]; Assert.Equal(sequence, restored.EnqueuedSeq); Assert.True(restored.Quiet); Assert.True(restored.Folded);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void PendingCallbackHoldsRemovalBeforeItsFirstMetadataRead(bool quiet)
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.Remove(Id(1), Id(100), 1000, Setup, quiet: quiet);
        e.QueueAdd(Id(1), "Movie", Id(199), 1500, Setup); m.ReadPending = true;
        e.AdvanceRemovals(1601, _ => { }); Assert.Equal("pendingRemove", j.State.Notices[Id(100)].Stage);
        e.Remove(Id(1), Id(101), 1602, Setup); e.AdvanceRemovals(2203, _ => { });
        var final = Assert.Single(j.State.Notices.Values); Assert.Equal(Id(101), final.EventId); Assert.Equal("outbox", final.Stage); Assert.False(final.Quiet);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RestartResumesPendingCallbackBeforeItsFirstMetadataRead(bool present)
    {
        using var temp = new TempDirectory(); var m = new Model(); m.Add(1);
        using (var j = new Journal(temp.Path))
        {
            var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.Remove(Id(1), Id(100), 1000, Setup);
            e.QueueAdd(Id(1), "Movie", Id(199), 1001, Setup); j.Compact();
        }
        if (!present) m.Items.Remove(Id(1));
        using var resumed = new Journal(temp.Path); var engine = new Engine(resumed, m, "key"); engine.AdvanceAdds(1601, _ => { }); engine.AdvanceRemovals(1601, _ => { });
        var notice = Assert.Single(resumed.State.Notices.Values); Assert.Equal("outbox", notice.Stage);
        Assert.Equal(present ? "add" : "rm", notice.Op); if (present) Assert.True(notice.Quiet);
    }

    [Theory] [InlineData("live", false)] [InlineData("recovery", false)] [InlineData("live", true)]
    public void CallbackAdmissionDoesNotReadMetadata(string origin, bool folded)
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup, origin, folded, [Id(8)]);
        Assert.Equal(0, m.ReadCalls);
        Assert.Equal("pendingAdd", j.State.Notices[Id(100)].Stage);
        e.AdvanceAdds(1001, _ => { }); Assert.Equal(1, m.ReadCalls);
    }

    [Fact] public void MaximumReceiptCoverageFitsAtomicOriginalAndRepairAndSnapshot()
    {
        using var temp = new TempDirectory(); var m = new Model();
        string[] coverage = Enumerable.Range(1, 2000).Select(Id).ToArray();
        using (var j = new Journal(temp.Path))
        {
            var e = new Engine(j, m, "key");
            e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup, receiptCoverage: coverage);
            e.Remove(Id(1), Id(101), 1001, Setup);
            e.QueueAdd(Id(1), "Movie", Id(102), 1002, Setup, receiptCoverage: coverage); j.Compact();
        }
        using var resumed = new Journal(temp.Path);
        Assert.False(resumed.RecoveredCorruption); Assert.Equal(2, resumed.State.Notices.Count);
        Assert.Single(resumed.State.Coverages);
        Assert.All(resumed.State.Notices.Values, n => Assert.Equal(coverage, resumed.State.Coverages[n.CoverageId!].Folders));
    }

    [Fact] public void MaximumPendingAndTombstoneWorkSharesCoverageAndCompactsAcrossRestart()
    {
        using var temp = new TempDirectory(); var m = new Model(); string proof;
        using (var j = new Journal(temp.Path))
        {
            var e = new Engine(j, m, "key");
            e.QueueAdd(Id(1), "Movie", Id(10001), 1000, Setup, receiptCoverage: Enumerable.Range(1, 2000).Select(Id).ToArray());
            var template = j.State.Notices[Id(10001)]; proof = template.CoverageId!;
            foreach (var chunk in Enumerable.Range(2, State.Capacity - 1).Chunk(100))
                j.Commit(chunk.Select(i => new Change { Kind = "notice", Notice = template with { Id = Id(i), EventId = Id(10000 + i) } }).ToArray());
            j.Compact();
        }
        using (var j = new Journal(temp.Path))
        {
            Assert.False(j.RecoveredCorruption); Assert.Equal(State.Capacity, j.State.Notices.Count); Assert.Single(j.State.Coverages);
            Assert.True(j.DiskBytes < 10 * 1024 * 1024);
            foreach (var chunk in j.State.Notices.Values.ToArray().Chunk(100))
                j.Commit(chunk.SelectMany(n => new Change[] {
                    new() { Kind = "remove", Tombstone = new(n.Id!, j.State.Seq + 1, 1001, 1, new(n.EventId, n.FirstSeenAt, n.SetupHash, n.OutageId, n.EnqueuedSeq, n.CoverageId)) },
                    new() { Kind = "retire", Key = n.EventId } }).ToArray());
            j.Compact();
        }
        using var resumed = new Journal(temp.Path);
        Assert.False(resumed.RecoveredCorruption); Assert.Empty(resumed.State.Notices); Assert.Equal(State.Capacity, resumed.State.Tombstones.Count);
        Assert.Single(resumed.State.Coverages); Assert.True(resumed.DiskBytes < 10 * 1024 * 1024);
        Assert.All(resumed.State.Tombstones.Values, t => Assert.Equal(proof, t.UnannouncedAdd!.CoverageId));
    }

    [Theory]
    [InlineData("live", false, false)] [InlineData("live", false, true)] [InlineData("live", true, false)] [InlineData("live", true, true)]
    [InlineData("recovery", false, false)] [InlineData("recovery", false, true)] [InlineData("recovery", true, false)] [InlineData("recovery", true, true)]
    public void UncoveredCallbacksResolveMovesBeforeQualifyingAnElapsedOutage(string origin, bool scanning, bool removed)
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model { ScanRunning = scanning };
        m.Add(1, "aaaaaaaaaaaaaaaa"); m.Add(2, "aaaaaaaaaaaaaaaa"); Bound(j);
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.BeginRecovery(Setup, 1000);
        e.QueueAdd(Id(2), "Movie", Id(100), 1800, Setup, origin, receiptCoverage: []);
        e.AdvanceAdds(1801, _ => { }); e.AdvanceRecovery(Setup, 1801, false);
        Assert.False(j.State.Recoveries[Setup].HasAddition);
        Assert.DoesNotContain(j.State.Notices.Values, n => n.Op == "summary");
        if (removed) e.Remove(Id(1), Id(101), 1802, Setup, "recovery");
        m.ScanRunning = false;
        e.AdvanceAdds(2401, _ => { }); e.AdvanceRecovery(Setup, 2401, false);
        Assert.Equal(!removed, j.State.Recoveries[Setup].HasAddition);
        Assert.Equal(removed ? 0 : 1, j.State.Notices.Values.Count(n => n.Op == "summary"));
        if (removed) Assert.All(j.State.Notices.Values, n => { Assert.True(n.Quiet); Assert.False(n.Folded); });
    }

    [Fact] public void CoverageBudgetRetainsConservativeWorkAndReclaimsUnreferencedProofs()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var e = new Engine(j, new Model(), "key");
        for (int i = 0; i < State.CoverageFolderCapacity / 2000; i++)
            e.QueueAdd(Id(i + 1), "Movie", Id(10001 + i), 1000, Setup, receiptCoverage: Enumerable.Range(2000 * i + 1, 2000).Select(Id).ToArray());
        string[] next = Enumerable.Range(State.CoverageFolderCapacity + 1, 2000).Select(Id).ToArray();
        e.QueueAdd(Id(999), "Movie", Id(19999), 1001, Setup, receiptCoverage: next);
        Assert.Equal("", j.State.Notices[Id(19999)].CoverageId); Assert.Equal(1, j.State.Health.Dropped);
        var invalid = new CoverageProof(Identity.Hash(string.Concat(next)), next);
        Assert.Throws<InvalidDataException>(() => j.State.Apply(new Change { Kind = "coverage", Coverage = invalid }));
        Assert.Equal(State.CoverageFolderCapacity, j.State.Coverages.Values.Sum(p => p.Folders.Length));
        j.Commit(new Change { Kind = "retire", Key = Id(10001) }, new() { Kind = "retire", Key = Id(19999) });
        e.QueueAdd(Id(999), "Movie", Id(19999), 1002, Setup, receiptCoverage: next);
        Assert.True(j.State.Notices.ContainsKey(Id(19999))); Assert.Equal(1, j.State.Health.Dropped); j.Compact();
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void UnreadOriginalRetainsReceiptCoverageThroughFlapAndRestart(bool covered)
    {
        using var temp = new TempDirectory(); var m = new Model { ReadPending = true }; m.Add(1, folder: covered ? Id(8) : Id(9));
        long sequence;
        using (var j = new Journal(temp.Path))
        {
            var e = new Engine(j, m, "key");
            e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup, receiptCoverage: [Id(8)]);
            var pending = j.State.Notices[Id(100)]; sequence = pending.EnqueuedSeq;
            Assert.Equal("live", pending.Origin); Assert.Empty(pending.Folders);
            e.Remove(Id(1), Id(101), 1001, Setup); j.Compact();
        }
        using var resumed = new Journal(temp.Path); var engine = new Engine(resumed, m, "key");
        // C has different coverage: it must not replace A's receipt proof.
        engine.QueueAdd(Id(1), "Movie", Id(102), 1002, Setup, receiptCoverage: [Id(8), Id(9)]);
        var original = resumed.State.Notices[Id(100)];
        Assert.Equal(new[] { Id(8) }, resumed.State.Coverages[original.CoverageId!].Folders); Assert.Equal(1000, original.FirstSeenAt);
        Assert.Equal(sequence, original.EnqueuedSeq); Assert.Equal(Setup, original.SetupHash);
        m.ReadPending = false; engine.AdvanceAdds(1003, _ => { });
        original = resumed.State.Notices[Id(100)];
        Assert.Equal("live", original.Origin); Assert.Equal(!covered, original.Folded);
        Assert.Equal(covered, Rules.Audible(original, 1003));
        Assert.True(resumed.State.Notices[Id(102)].Quiet);
    }

    [Fact] public void MissingReaddReadReleasesItsHeldRemoval()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.Remove(Id(1), Id(100), 1000, Setup);
        e.QueueAdd(Id(1), "Movie", Id(101), 1001, Setup); m.Items.Remove(Id(1));
        e.AdvanceAdds(1002, _ => { }); e.AdvanceRemovals(1601, _ => { });
        var final = Assert.Single(j.State.Notices.Values); Assert.Equal("rm", final.Op); Assert.Equal("outbox", final.Stage);
    }

    [Fact] public void DelayedCallbackReceivedBeforeHoldStillPublishesFreshQuietHint()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1001, _ => { });
        e.Remove(Id(1), Id(101), 1002, Setup); e.AdvanceRemovals(1602, _ => { }); e.Acknowledge([Id(101)], 1603);
        // The callback was received at 1500, but its first read completed only after removal delivery.
        e.QueueAdd(Id(1), "Movie", Id(102), 1500, Setup); e.AdvanceAdds(1604, _ => { });
        Assert.True(j.State.Notices[Id(102)].Quiet); Assert.Equal("outbox", j.State.Notices[Id(102)].Stage);
    }

    [Fact] public void ReadyRepairWaitsForOriginalAcknowledgementRegardlessOfReadinessOrder()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1001, _ => { });
        e.Remove(Id(1), Id(101), 1002, Setup); e.QueueAdd(Id(1), "Movie", Id(102), 1003, Setup);
        e.Put(j.State.Notices[Id(102)] with { Stage = "outbox", ReadyAt = 1004 });
        Assert.DoesNotContain(e.SubmittableNotices(), n => n.EventId == Id(102));
        e.AdvanceAdds(1005, _ => { }); Assert.DoesNotContain(e.SubmittableNotices(), n => n.EventId == Id(102));
        e.Acknowledge([Id(100)], 1006); Assert.Contains(e.SubmittableNotices(), n => n.EventId == Id(102));
    }

    [Fact] public void LostAddAcknowledgementAndDeliveredRemovalGetFreshQuietMembershipIdentity()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1001, _ => { });
        // Relay accepted e=100, but its acknowledgement was lost. The held removal later drains.
        e.Remove(Id(1), Id(101), 1002, Setup); e.AdvanceRemovals(1602, _ => { }); e.Acknowledge([Id(101)], 1603);
        e.QueueAdd(Id(1), "Movie", Id(102), 1604, Setup); e.AdvanceAdds(1605, _ => { });
        Assert.Equal(2, j.State.Notices.Count);
        Assert.False(j.State.Notices[Id(100)].Quiet); Assert.Equal(1000, j.State.Notices[Id(100)].FirstSeenAt);
        Assert.True(j.State.Notices[Id(102)].Quiet); Assert.Equal("outbox", j.State.Notices[Id(102)].Stage);
    }

    [Fact] public void RestoredOriginalKeepsOutageAssignmentAfterSummaryAcknowledgement()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1); Bound(j);
        var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(2801, _ => { });
        string outage = j.State.Notices[Id(100)].OutageId!; e.AdvanceRecovery(Setup, 2802, true);
        string summary = Assert.Single(j.State.Notices.Values, n => n.Op == "summary").EventId;
        e.Remove(Id(1), Id(101), 2803, Setup); e.Acknowledge([summary], 2804);
        e.QueueAdd(Id(1), "Movie", Id(102), 2805, Setup); e.AdvanceAdds(2806, _ => { }); e.AdvanceRecovery(Setup, 2807, true);
        Assert.Equal(outage, j.State.Notices[Id(100)].OutageId); Assert.Equal(outage, j.State.Recoveries[Setup].Id);
        Assert.DoesNotContain(j.State.Notices.Values, n => n.Op == "summary");
    }

    [Fact] public void SameIdReaddDoesNotWaitForMetadataPastRemovalHold()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.Remove(Id(1), Id(100), 1000, Setup);
        m.Items[Id(1)] = m.Items[Id(1)] with { Ready = false };
        e.QueueAdd(Id(1), "Movie", Id(101), 1001, Setup); e.AdvanceAdds(1002, _ => { }); e.AdvanceRemovals(1601, _ => { });
        var added = Assert.Single(j.State.Notices.Values); Assert.Equal("add", added.Op); Assert.True(added.Quiet);
    }

    [Fact] public void BulkWindowExcludesPendingSameIdReadWithoutDelayingUnrelatedRemovals()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); var e = new Engine(j, m, "key");
        for (int i = 1; i <= 202; i++) { m.Add(i); e.Index(m.Items[Id(i)].Title); e.Remove(Id(i), Id(1000 + i), 1000, Setup); }
        e.QueueAdd(Id(1), "Movie", Id(2000), 1001, Setup); m.ReadPending = true;
        e.AdvanceAdds(1002, _ => { }); e.AdvanceRemovals(1601, _ => { });
        var bulk = Assert.Single(j.State.Notices.Values, n => n.Op == "rmbulk"); Assert.Equal(201, bulk.Count); Assert.False(bulk.Quiet);
        e.AdvanceRemovals(2801, _ => { });
        Assert.Equal(bulk.EventId, Assert.Single(j.State.Notices.Values, n => n.Op == "rmbulk").EventId);
        m.ReadPending = false; e.AdvanceAdds(2802, _ => { }); e.AdvanceRemovals(2803, _ => { });
        Assert.Contains(j.State.Notices.Values, n => n.Op == "add" && n.Quiet);
    }

    [Fact] public void PendingSameIdReadHoldsRemovalButFinalDeleteStillDelivers()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.Remove(Id(1), Id(100), 1000, Setup);
        e.QueueAdd(Id(1), "Movie", Id(101), 1001, Setup); m.ReadPending = true;
        e.AdvanceAdds(1002, _ => { }); e.AdvanceRemovals(1601, _ => { });
        Assert.DoesNotContain(j.State.Notices.Values, n => n.Op == "rm" && n.Stage == "outbox");
        e.Remove(Id(1), Id(102), 1602, Setup); m.Items.Remove(Id(1)); e.AdvanceRemovals(2203, _ => { });
        var final = Assert.Single(j.State.Notices.Values); Assert.Equal("rm", final.Op); Assert.Equal("outbox", final.Stage); Assert.False(final.Quiet);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RemoveAddRemovePreservesFinalRemovalWhileReplacementIsPending(bool unready)
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.Remove(Id(1), Id(100), 1000, Setup);
        e.QueueAdd(Id(1), "Movie", Id(101), 1001, Setup);
        if (unready)
        {
            m.Items[Id(1)] = m.Items[Id(1)] with { Ready = false }; e.AdvanceAdds(1002, _ => { });
        }
        e.Remove(Id(1), Id(102), 1003, Setup); m.Items.Remove(Id(1)); e.AdvanceRemovals(1700, _ => { });
        var final = Assert.Single(j.State.Notices.Values); Assert.Equal("rm", final.Op); Assert.Equal("outbox", final.Stage);
    }

    [Fact] public void SameIdReaddAfter24HoursMayAlertEvenWithoutSweep()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.Remove(Id(1), Id(100), 1000, Setup);
        e.AdvanceRemovals(1600, _ => { }); e.Acknowledge([Id(100)], 1600);
        e.QueueAdd(Id(1), "Movie", Id(101), 1000 + 86400, Setup); e.AdvanceAdds(1000 + 86400, _ => { });
        Assert.False(Assert.Single(j.State.Notices.Values).Quiet);
    }

    [Fact] public void AddCallbackForAnIndexedIdIsQuiet()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1);
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title);
        e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1000, _ => { });
        Assert.True(Assert.Single(j.State.Notices.Values).Quiet);
    }

    [Fact] public void EightDayGap237AdditionsHasOneSummaryAndZeroIndividualAddsIncludingAfterRestart()
    {
        using var temp = new TempDirectory(); var model = new Model();
        string eventId;
        long now = 1000 + 8 * 86400;
        using (var j = new Journal(temp.Path))
        {
            Bound(j); var e = new Engine(j, model, Identity.ServerKey("fixture")); e.BeginRecovery(Setup, now);
            for (int i = 1; i <= 237; i++) { model.Add(i); e.QueueAdd(Id(i), "Movie", Id(1000 + i), now, Setup, "recovery"); }
            e.AdvanceAdds(now + 1, _ => { });
            foreach (var n in j.State.Notices.Values.ToArray()) Assert.Null(e.Freshen(n, now + 2));
            e.AdvanceRecovery(Setup, now + 2, true);
            var summary = Assert.Single(j.State.Notices.Values); Assert.Equal("summary", summary.Op);
            var fresh = e.Freshen(summary, now + 2)!; Assert.Equal(new[] { UserHash }, fresh.Audience);
            using var json = JsonDocument.Parse(Wire.Encode("fixture", [UserHash], [fresh]));
            Assert.Equal(3, json.RootElement.GetProperty("v").GetInt32());
            Assert.Equal(new[] { "a", "at", "e", "firstSeenAt", "life", "op", "origin", "outageStart", "presentationKey", "profile" }, json.RootElement.GetProperty("events")[0].EnumerateObject().Select(p => p.Name).Order());
            eventId = summary.EventId; j.Compact();
        }
        using var reopened = new Journal(temp.Path); var resumed = new Engine(reopened, model, Identity.ServerKey("fixture"));
        resumed.BeginRecovery(Setup, now + 300); resumed.AdvanceRecovery(Setup, now + 900, true);
        Assert.Equal(eventId, Assert.Single(reopened.State.Notices.Values).EventId);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void EmptyAndRemovalsOnlyRecoveryStaysSilent(bool removal)
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1); Bound(j);
        var e = new Engine(j, m, "key"); e.BeginRecovery(Setup, 1000);
        if (removal) { e.Index(m.Items[Id(1)].Title); e.Remove(Id(1), Id(100), 1001, Setup, "recovery"); e.AdvanceRemovals(1700, _ => { }); }
        e.AdvanceRecovery(Setup, 1701, true);
        Assert.DoesNotContain(j.State.Notices.Values, n => n.Op == "summary");
        Assert.All(j.State.Notices.Values, n => Assert.True(e.Freshen(n, 1701)!.Quiet));
    }

    [Fact] public void FirstAdditionAfterCutoffEmitsOnceAcrossLibrariesAndCrash()
    {
        using var temp = new TempDirectory(); var m = new Model(); m.Add(1); m.Add(2, folder: Id(9)); string outage;
        using (var j = new Journal(temp.Path))
        {
            Bound(j); var e = new Engine(j, m, "key"); outage = e.BeginRecovery(Setup, 1000).Id;
            e.AdvanceRecovery(Setup, 1700, false); Assert.Empty(j.State.Notices);
            e.QueueAdd(Id(1), "Movie", Id(100), 1800, Setup, "recovery"); e.AdvanceAdds(1801, _ => { }); e.AdvanceRecovery(Setup, 1801, false);
            Assert.Single(j.State.Notices.Values, n => n.Op == "summary");
        }
        using var restored = new Journal(temp.Path); var engine = new Engine(restored, m, "key");
        Assert.Equal(outage, engine.BeginRecovery(Setup, 1802).Id);
        engine.QueueAdd(Id(2), "Movie", Id(101), 1900, Setup, "recovery"); engine.AdvanceAdds(2000, _ => { }); engine.AdvanceRecovery(Setup, 2000, true);
        Assert.Single(restored.State.Notices.Values, n => n.Op == "summary");
    }

    [Fact] public void LateRecoveryWorkStaysInItsOutageButNewRecoveryAfterCompletionStartsAnother()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model();
        m.Add(1); m.Add(2); m.Add(3); Bound(j); var e = new Engine(j, m, "key");
        string old = e.BeginRecovery(Setup, 1000).Id;
        e.QueueAdd(Id(1), "Movie", Id(100), 1001, Setup, "recovery"); e.AdvanceAdds(1700, _ => { }); e.AdvanceRecovery(Setup, 1700, false);
        e.Acknowledge(j.State.Notices.Values.Where(n => n.Op == "summary").Select(n => n.EventId).ToHashSet(), 1701);
        e.QueueAdd(Id(2), "Movie", Id(101), 1800, Setup, "recovery"); e.AdvanceAdds(1801, _ => { }); e.AdvanceRecovery(Setup, 1801, true);
        e.AdvanceAdds(1802, _ => { });
        foreach (var n in j.State.Notices.Values.ToArray()) Assert.Null(e.Freshen(n, 1803));
        e.AdvanceRecovery(Setup, 1804, true); Assert.Empty(j.State.Notices); Assert.Equal(old, j.State.Recoveries[Setup].Id);
        e.QueueAdd(Id(3), "Movie", Id(102), 1900, Setup, "recovery"); e.AdvanceAdds(1901, _ => { });
        Assert.Null(e.Freshen(j.State.Notices[Id(102)], 1902)); e.AdvanceRecovery(Setup, 1903, true);
        Assert.NotEqual(old, Assert.Single(j.State.Notices.Values).OutageId);
    }

    [Fact] public void SummaryAudienceIsCurrentOnEveryRetryAndUnknownIsOmitted()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1); Bound(j);
        var e = new Engine(j, m, "key"); e.BeginRecovery(Setup, 1000); e.QueueAdd(Id(1), "Movie", Id(100), 1001, Setup, "recovery"); e.AdvanceAdds(1002, _ => { }); e.AdvanceRecovery(Setup, 1002, true);
        var n = Assert.Single(j.State.Notices.Values, n => n.Op == "summary"); Assert.NotEmpty(e.Freshen(n, 1003)!.Audience);
        m.Allowed = false; Assert.Empty(e.Freshen(n, 1004)!.Audience); Assert.Empty(j.State.Notices[n.EventId].Audience);
        m.Allowed = true; m.Unknown = true; Assert.Empty(e.Freshen(n, 1005)!.Audience);
        m.Unknown = false; var fresh = e.Freshen(n, 1006)!; Assert.Equal(n.EventId, fresh.EventId); Assert.Equal(n.FirstSeenAt, fresh.FirstSeenAt);
    }

    [Fact] public void SummaryEvaluationMakesProgressBeyondTheBoundedAudienceCache()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model { AsyncAudience = true }; Bound(j);
        var e = new Engine(j, m, "key"); e.BeginRecovery(Setup, 1000);
        for (int i = 1; i <= 237; i++) { m.Add(i); e.QueueAdd(Id(i), "Movie", Id(1000 + i), 1001, Setup, "recovery"); }
        e.AdvanceAdds(1002, _ => { }); e.AdvanceRecovery(Setup, 1002, true); var n = Assert.Single(j.State.Notices.Values, n => n.Op == "summary");
        Notice? result = null;
        for (int i = 0; i < 100 && result is null; i++)
        { try { result = e.Freshen(n, 1003 + i); } catch (LibraryPendingException) { } m.CompleteAudienceCalls(); }
        Assert.NotNull(result); Assert.Equal(new[] { UserHash }, result.Audience);
    }

    [Fact] public void ANewExpiredLiveOutageDoesNotReuseAnAlreadyDrainedSummary()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1); Bound(j);
        var e = new Engine(j, m, "key"); var old = e.BeginRecovery(Setup, 1000);
        j.Commit(new Change { Kind = "recovery", Recovery = old with { Complete = true, Emitted = true, HasAddition = true } }, new() { Kind = "recoveryItem", RecoveryItem = new(Id(1), old.Id, Setup) });
        e.Put(Event(100) with { Id = Id(1), SetupHash = Setup, QueuedAt = 10000, At = 10000, Audience = [UserHash] });
        Assert.Null(e.Freshen(j.State.Notices[Id(100)], 14000)); e.AdvanceRecovery(Setup, 14001, true);
        var summary = Assert.Single(j.State.Notices.Values); Assert.Equal("summary", summary.Op); Assert.NotEqual(old.Id, summary.OutageId);
        Assert.Equal(Identity.PresentationKey("key", "profile"), summary.PresentationKey);
        Assert.Equal(summary.OutageId, Assert.Single(j.State.RecoveryItems.Values).OutageId);
    }

    [Fact] public void TwelveHourOldLiveOutboxFoldsAndNeverRefreshesFirstSeen()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1); Bound(j);
        var e = new Engine(j, m, "key"); e.QueueAdd(Id(1), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1001, _ => { });
        var n = j.State.Notices[Id(100)]; Assert.Equal(1000, n.FirstSeenAt); Assert.NotNull(e.Freshen(n, 1002));
        Assert.Null(e.Freshen(n, 1000 + 12 * 3600)); e.AdvanceRecovery(Setup, 50000, true);
        Assert.Equal("summary", Assert.Single(j.State.Notices.Values).Op);
    }

    [Theory] [InlineData(true, false)] [InlineData(false, false)] [InlineData(true, true)] [InlineData(false, true)]
    public void ObservedMoveIsQuietInBothOrdersIncludingDifferentFolders(bool removeFirst, bool recovery)
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); m.Add(1, "1234567890abcdef"); m.Add(2, "1234567890abcdef", Id(9));
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title);
        string origin = recovery ? "recovery" : "live"; if (recovery) { Bound(j); e.BeginRecovery(Setup, 1000); }
        if (removeFirst) e.Remove(Id(1), Id(100), 1000, Setup, origin);
        e.QueueAdd(Id(2), "Movie", Id(101), 1001, Setup, origin); e.AdvanceAdds(1002, _ => { });
        if (!removeFirst) { Assert.Equal("pendingAdd", j.State.Notices[Id(101)].Stage); e.Remove(Id(1), Id(100), 1003, Setup, origin); e.AdvanceAdds(1004, _ => { }); }
        e.AdvanceRemovals(1700, _ => { }); if (recovery) e.AdvanceRecovery(Setup, 1701, true);
        Assert.DoesNotContain(j.State.Notices.Values, n => n.Op == "summary");
        Assert.All(j.State.Notices.Values, n => Assert.True(n.Quiet)); Assert.DoesNotContain(j.State.Notices.Values, n => Rules.Audible(n, 1700));
        Assert.Contains(Id(2), j.State.Index.Keys); Assert.DoesNotContain(Id(1), j.State.Index.Keys);
    }

    [Fact] public void RecoveryMoveDiscoveredBeforeItsDigestIsReadyDoesNotQualifyASummary()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); Bound(j);
        m.Add(1, UserHash); m.Add(2); var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.BeginRecovery(Setup, 1000);
        e.QueueAdd(Id(2), "Movie", Id(100), 1001, Setup, "recovery");
        m.Add(2, UserHash); e.Remove(Id(1), Id(101), 1002, Setup, "recovery"); e.AdvanceAdds(1003, _ => { });
        e.AdvanceRecovery(Setup, 1004, true); Assert.DoesNotContain(j.State.Notices.Values, n => n.Op == "summary");
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void SecondEditionWithoutRemovalAlertsWhenScanCompletes(bool sameDigest)
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model { ScanRunning = true };
        m.Add(1, "1234567890abcdef"); m.Add(2, sameDigest ? "1234567890abcdef" : "abcdef1234567890");
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.QueueAdd(Id(2), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1001, _ => { });
        Assert.Equal(sameDigest ? "pendingAdd" : "outbox", j.State.Notices[Id(100)].Stage);
        m.ScanRunning = false; e.AdvanceAdds(1100, _ => { }); Assert.True(Rules.Audible(j.State.Notices[Id(100)], 1100));
    }

    [Fact] public void ScanBoundedHoldCannotKeepAnAdditionAudibleBeyondThirtyMinutes()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model { ScanRunning = true }; m.Add(1, UserHash); m.Add(2, UserHash); Bound(j);
        var e = new Engine(j, m, "key"); e.Index(m.Items[Id(1)].Title); e.QueueAdd(Id(2), "Movie", Id(100), 1000, Setup); e.AdvanceAdds(1001, _ => { });
        e.AdvanceAdds(2801, _ => { }); Assert.Null(e.Freshen(j.State.Notices[Id(100)], 2801)); e.AdvanceRecovery(Setup, 2802, true);
        Assert.Single(j.State.Notices.Values, n => n.Op == "summary");
    }

    [Theory] [InlineData("journal")] [InlineData("snapshot")]
    public void VersionedMigrationClassifiesOldNoticesRecoveryAndLeavesDowngradeFilesUnchanged(string layout)
    {
        using var temp = new TempDirectory();
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-1.0.1-" + layout);
        foreach (string file in Directory.EnumerateFiles(fixture)) File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        var original = Directory.EnumerateFiles(temp.Path).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
        using (var upgraded = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
        {
            Assert.False(upgraded.RecoveredCorruption); var n = Assert.Single(upgraded.State.Notices.Values);
            Assert.Equal("recovery", n.Origin); Assert.Equal(n.QueuedAt, n.FirstSeenAt); Assert.Single(upgraded.State.Index);
            upgraded.Commit(new Change { Kind = "health", Health = new(Dropped: 42) }); upgraded.Compact();
        }
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(temp.Path, file.Key!)));
        using (var downgrade = new Journal(temp.Path))
        { Assert.False(downgrade.RecoveredCorruption); Assert.Single(downgrade.State.Notices); Assert.Single(downgrade.State.Index); Assert.Equal(7, downgrade.State.Health.Dropped); }
        using var reupgrade = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true);
        Assert.False(reupgrade.RecoveredCorruption); Assert.Equal(42, reupgrade.State.Health.Dropped);
    }

    [Theory] [InlineData("ids")] [InlineData("describe")]
    public async Task HungLibraryCallsHaveDeadlinesAndNeverReplaceTheirThread(string op)
    {
        var clock = new Clock(); using var blocked = new ManualResetEventSlim(); var fake = new CompatFake { Block = blocked, BlockOp = op }; fake.Add(1);
        using var lanes = new LibraryLanes(fake, "key", clock);
        if (op == "ids") Assert.Throws<LibraryPendingException>(() => lanes.Ids(Id(8)));
        else Assert.Throws<LibraryPendingException>(() => lanes.Read(Id(1), 1000, 1001));
        await Until(() => Volatile.Read(ref fake.Entered) != 0);
        clock.Advance(TimeSpan.FromSeconds(op == "ids" ? 121 : 16)); Assert.True(lanes.Stuck);
        int thread = fake.ThreadId;
        for (int i = 0; i < 100; i++)
            Assert.Throws<LibraryCallException>(() => op == "ids" ? (object)lanes.Ids(Id(8)) : lanes.Read(Id(1), 1000, 1001));
        Assert.Equal(1, fake.Entered); Assert.Equal(thread, fake.ThreadId);
        if (op == "ids") { ItemSnapshot? item = null; await Poll(() => item = lanes.Read(Id(1), 1000, 1001)); Assert.NotNull(item); }
        clock.Advance(TimeSpan.FromMinutes(30)); Assert.True(lanes.Stalled);
        blocked.Set();
        await Until(() => !lanes.Stuck);
        Assert.Throws<LibraryCallException>(() => op == "ids" ? (object)lanes.Ids(Id(8)) : lanes.Read(Id(1), 1000, 1001));
        await Poll(() => { if (op == "ids") lanes.Ids(Id(8)); else lanes.Read(Id(1), 1000, 1001); });
        Assert.False(lanes.Stuck);
    }

    [Fact] public async Task LaneQueueIsBoundedAndRequestsBehindStuckCallFailFast()
    {
        var clock = new Clock(); using var release = new ManualResetEventSlim(); using var lane = new LibraryLane("fixture", clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Throws<LibraryPendingException>(() => lane.Poll("first", () => { entered.TrySetResult(); release.Wait(); return 1; }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (int i = 0; i < 1000; i++) Assert.Throws<LibraryPendingException>(() => lane.Poll("queued" + i, () => 1));
        Assert.InRange(lane.Count, 1, 17); clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Throws<LibraryCallException>(() => lane.Poll("behind", () => 1)); release.Set();
        await Until(() => !lane.Stuck);
        Assert.Throws<LibraryCallException>(() => lane.Poll("first", () => 1));
        await Poll(() => lane.Poll("first", () => 1));
    }

    [Fact] public async Task AFullRequestTableBackpressuresNewCallersWithoutEvictingCompletedResults()
    {
        using var lane = new LibraryLane("retention fixture", new Clock()); using var release = new ManualResetEventSlim(); int calls = 0;
        try
        {
            for (int i = 0; i < 64; i++)
            {
                int value = i; release.Reset();
                Assert.Throws<LibraryPendingException>(() => lane.Poll("request-" + value, () => { release.Wait(); Interlocked.Increment(ref calls); return value; }));
                release.Set(); await Until(() => Volatile.Read(ref calls) == value + 1);
            }
            Assert.Throws<LibraryPendingException>(() => lane.Poll("newcomer", () => { Interlocked.Increment(ref calls); return -1; }));
            Assert.Equal(0, lane.Poll<int>("request-0", () => throw new InvalidOperationException("Must consume the original result")));
            Assert.Equal(64, calls); Assert.Equal(63, lane.Count);
            lane.ForgetAll(); Assert.Equal(0, lane.Count);
        }
        finally { release.Set(); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidatingAnotherItemDoesNotDiscardAnOutstandingResult(bool audience)
    {
        var clock = new Clock(); using var release = new ManualResetEventSlim();
        var fake = new CompatFake { BlockOp = "get", BlockedId = Id(1), Block = release }; fake.Add(1);
        using var lanes = new LibraryLanes(fake, "key", clock);
        object? Read() => audience ? lanes.Audience(Id(1), [], 1000) : lanes.Read(Id(1), 1000, 1000);
        try
        {
            Assert.Throws<LibraryPendingException>(() => Read()); await Until(() => fake.Entered != 0);
            lanes.Invalidate(Id(2)); fake.BlockOp = null; release.Set(); await Poll(() => { _ = Read(); });
            if (audience) Assert.Equal(1, fake.AudienceCalls); else Assert.Single(fake.Reads);
        }
        finally { release.Set(); }
    }

    [Theory] [InlineData(-3600)] [InlineData(3600)]
    public void MonotonicLimiterHealthAndBackoffIgnoreWallClockChanges(int jump)
    {
        var clock = new Clock(); var limiter = new HeartbeatLimiter(clock);
        for (int i = 0; i < 12; i++) Assert.True(limiter.TryAcquire());
        clock.Jump(TimeSpan.FromSeconds(jump)); Assert.False(limiter.TryAcquire());
        var snapshot = new RuntimeSnapshot { ErrorCode = "none", StorageUnavailable = false, ProgressStamp = clock.GetTimestamp(), UncoveredStamp = clock.GetTimestamp() };
        Assert.True(Heartbeat.Healthy(snapshot, clock, false)); clock.Advance(TimeSpan.FromSeconds(301)); Assert.False(Heartbeat.Healthy(snapshot, clock, false));
        clock.Advance(TimeSpan.FromSeconds(3300)); Assert.True(limiter.TryAcquire());
        limiter.Backoff(TimeSpan.FromMinutes(15)); Assert.False(limiter.TryAcquire()); clock.Advance(TimeSpan.FromMinutes(15)); Assert.True(limiter.TryAcquire());
    }

    [Fact] public void ReprobeForNonRetryableResponseIsCappedAndHonorsRelayBackoff()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), NotificationService.ProbeDelay(new(false, [], "unauthorized", null, false), 1000));
        Assert.Equal(TimeSpan.FromMinutes(40), NotificationService.ProbeDelay(new(false, [], "unauthorized", TimeSpan.FromMinutes(40), false), 1000));
    }

    [Theory] [InlineData(-3600)] [InlineData(3600)]
    public void HeartbeatAckSampleRemainsValidAfterAWallClockCorrection(int delta)
    {
        var clock = new Clock(); long ack = clock.GetUtcNow().ToUnixTimeSeconds();
        var status = new RuntimeSnapshot { LastAckAt = ack, ProgressStamp = clock.GetTimestamp() }; clock.Jump(TimeSpan.FromSeconds(delta));
        using var json = JsonDocument.Parse(Heartbeat.Encode("fixture", status, clock, false));
        var sample = json.RootElement.GetProperty("lastAckAt");
        if (delta < 0) Assert.Equal(JsonValueKind.Null, sample.ValueKind);
        else Assert.Equal(ack, sample.GetInt64());
    }

    [Fact] public async Task DiagnosticsRedactArbitraryStringsCapAndSurviveFailures()
    {
        using var temp = new TempDirectory(); using var ring = new DiagnosticsRing(() => (temp.Path, Path.GetDirectoryName(temp.Path)!), TimeProvider.System);
        ring.Record("private-path-title-secret"); for (int i = 0; i < 5000; i++) ring.Record(i % 2 == 0 ? "restartRequired" : "stateRecovered");
        Assert.Equal(1024, ring.Read().Length); Assert.All(ring.Read(), e => Assert.Contains(e.Cause, CauseCodes.Allowed));
        string path = Path.Combine(temp.Path, "diagnostics", "diagnostics.ndjson"); await Until(() => File.Exists(path) && new FileInfo(path).Length > 0);
        Assert.InRange(new FileInfo(path).Length, 1, DiagnosticsRing.MaxBytes);
        Assert.DoesNotContain("private-path-title-secret", File.ReadAllText(path));
        using var failing = new DiagnosticsRing(() => throw new IOException("secret"), TimeProvider.System); failing.Record("stateUnavailable"); Assert.Single(failing.Read());
    }

    [Theory] [InlineData(2)] [InlineData(5)] [InlineData(32)] [InlineData(123)]
    public async Task SupervisorReopensEveryCompletedStateFailureWithHourlyRedactedLogs(int code)
    {
        using var temp = new TempDirectory(); int opens = 0; var clock = new Clock(); var logger = new RecordingLog { ThrowAfterRecording = code == 123 }; var pauses = new List<double>();
        using var harness = new ServiceHarness(temp.Path, clock, new PluginConfiguration(), logger: logger,
            factory: (path, root, token, owner) => ++opens <= 3 ? throw new IOException("private-path-secret", unchecked((int)0x80070000) | code) : new Journal(path, root, token, TimeSpan.FromSeconds(3), owner),
            retry: (delay, _) => { pauses.Add(delay.TotalSeconds); return Task.CompletedTask; });
        await harness.Service.StartAsync(default); await Until(() => harness.Service.Snapshot.StorageUnavailable == false && opens == 4);
        Assert.Equal(4, opens); Assert.Equal(new[] { 1d, 2d, 4d }, pauses); Assert.Single(logger.Messages); Assert.DoesNotContain("private-path-secret", logger.Messages[0]);
        Assert.False(harness.Service.ExecuteTask!.IsCompleted); Assert.DoesNotContain("Status", JsonSerializer.Serialize(harness.Config));
        await harness.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AmbiguousOperationOrConstructorCleanupRequiresRestartUntilCleanThenAutomaticallyReopens(bool inCleanup)
    {
        using var temp = new TempDirectory(); using var release = new ManualResetEventSlim(); int opens = 0; var clock = new Clock(); StateIoWorker? blocked = null;
        using var harness = new ServiceHarness(temp.Path, clock, new PluginConfiguration(),
            factory: (path, root, token, owner) =>
            {
                if (Interlocked.Increment(ref opens) != 1) return new Journal(path, root, token, TimeSpan.FromSeconds(1), owner);
                blocked = new StateIoWorker(token, TimeSpan.FromMilliseconds(30), () => { if (inCleanup) release.Wait(); }); owner.Attach(blocked);
                if (!inCleanup) Assert.Throws<StatePersistenceException>(() => blocked.Invoke(() => release.Wait()));
                else blocked.Invoke(() => { });
                blocked.Dispose(); throw new StatePersistenceException(new IOException("secret"));
            }, retry: async (_, ct) => await Task.Delay(10, ct));
        await harness.Service.StartAsync(default); await Until(() => harness.Service.Snapshot.RestartRequired);
        Assert.Equal(1, opens); Assert.False(harness.Service.ExecuteTask!.IsCompleted); release.Set();
        await Until(() => opens == 2 && !harness.Service.Snapshot.StorageUnavailable); Assert.False(harness.Service.Snapshot.RestartRequired);
        await harness.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); blocked?.Dispose();
    }

    [Fact] public async Task IndependentHeartbeatKeepsLeaseAndLastKnownCountersWhileWorkerCannotProgress()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); using var blocked = new ManualResetEventSlim(); int opens = 0;
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, factory: (path, root, token, owner) =>
        { Interlocked.Increment(ref opens); blocked.Wait(); return new Journal(path, root, token, TimeSpan.FromSeconds(1), owner); });
        await h.Service.StartAsync(default); await Until(() => opens == 1 && h.Posts.Count != 0);
        for (int i = 0; i < 4; i++) { clock.Advance(TimeSpan.FromMinutes(10)); await Until(() => h.Posts.Count >= i + 2); }
        var posts = h.Posts.ToArray(); Assert.All(posts, p => { using var j = JsonDocument.Parse(p.Body); Assert.Equal(3, j.RootElement.GetProperty("v").GetInt32()); Assert.Equal(1200, j.RootElement.GetProperty("leaseSeconds").GetInt32()); Assert.Equal(typeof(Plugin).Assembly.GetName().Version!.ToString(), j.RootElement.GetProperty("version").GetString()); });
        using var last = JsonDocument.Parse(posts[^1].Body); Assert.True(last.RootElement.GetProperty("progressAge").GetInt64() > 300);
        blocked.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4));
    }

    [Fact] public async Task BindUsesOnlySessionUserRetainsAcrossRestartAndHealthHasExactShape()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); var config = new PluginConfiguration { Enabled = true, Secret = "fixture" };
        using (var h = new ServiceHarness(temp.Path, clock, config))
        {
            await h.Service.StartAsync(default); await Pump(clock, () => h.Service.Snapshot.StateBytes > 0);
            var controller = Controller(h.Service, Guid.ParseExact(Id(1), "N"));
            var request = JsonSerializer.Deserialize<BindRequest>("{\"registration\":\"" + Id(100) + "\",\"profile\":\"profile\",\"UserId\":\"" + Id(2) + "\",\"UserHash\":\"attacker\"}")!;
            var binding = controller.Bind(request, default); await Pump(clock, () => binding.IsCompleted && h.Service.Snapshot.Bindings.Length != 0 && h.Posts.Any(p => p.Route == "bindings")); Assert.IsType<OkResult>(await binding);
            var stored = Assert.Single(h.Service.Snapshot.Bindings); Assert.Equal(Identity.UserHash(Identity.ServerKey("fixture"), Guid.ParseExact(Id(1), "N")), stored.UserHash);
            var response = controller.Health().Value!; using var json = JsonDocument.Parse(JsonSerializer.Serialize(response)); Assert.Equal(new[] { "health", "setupId" }, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(Identity.Hash("fixture"), response.SetupId);
            int reports = h.Posts.Count(p => p.Route == "bindings");
            var rebind = controller.Bind(request, default); await Pump(clock, () => rebind.IsCompleted && h.Posts.Count(p => p.Route == "bindings") > reports); Assert.IsType<OkResult>(await rebind);
            await Pump(clock, () => h.Service.Health().Health == "healthy"); config.Secret = "rotated";
            Assert.Equal("not", h.Service.Health().Health); Assert.Equal(Identity.Hash("rotated"), h.Service.Health().SetupId);
            await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4));
        }
        using var reopened = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(1), versioned: true); Assert.Single(reopened.State.Bindings);
    }

    [Fact] public async Task EndpointsRequireSignedInSessionAndDiagnosticsRequiresElevation()
    {
        using var temp = new TempDirectory(); using var h = new ServiceHarness(temp.Path, new Clock(), new PluginConfiguration());
        var controller = Controller(h.Service, null);
        Assert.IsType<UnauthorizedResult>(await controller.Bind(new(Id(1), "profile"), default)); Assert.IsType<UnauthorizedResult>(controller.Health().Result);
        var type = typeof(NotificationsController); Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("RequiresElevation", type.GetMethod(nameof(NotificationsController.Diagnostics))!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Null(type.GetMethod(nameof(NotificationsController.Health))!.GetCustomAttribute<AuthorizeAttribute>());
    }

    private static Library FixtureLibrary(long until) => new(Identity.Hash("library"), Id(8), [], true, true, until);
    private static void Seed(string path, long now, bool binding = false, bool outbox = false)
    {
        using (var j = new Journal(path))
        {
            j.Commit(new Change { Kind = "library", Library = FixtureLibrary(now - 8 * 86400) }, new() { Kind = "title", Title = new(Id(1), "Movie", [Id(8)], null, 1, 0) });
            if (binding) j.Commit(new Change { Kind = "binding", Binding = new(Id(999), "profile", UserHash, Identity.Hash("fixture")) });
        }
        if (outbox)
        {
            using var current = new Journal(path, Path.GetDirectoryName(path)!, default, TimeSpan.FromSeconds(3), versioned: true);
            current.Commit(new Change { Kind = "notice", Notice = Event(100) with { Id = Id(1), QueuedAt = now, FirstSeenAt = now, At = now, SetupHash = Identity.Hash("fixture"), Audience = [UserHash] } });
        }
    }
    private static IEnumerable<JsonElement> SentEvents(ServiceHarness h)
    {
        foreach (var post in h.Posts.Where(p => p.Route == "events"))
        { using var json = JsonDocument.Parse(post.Body); foreach (var e in json.RootElement.GetProperty("events").EnumerateArray()) yield return e.Clone(); }
    }

    [Fact] public async Task FifteenHundredQueuedCallbacksDrainWithoutReissuingCompletedReads()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds());
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)));
            for (int i = 2; i <= 1501; i++) { fake.Add(i); h.EmitAdd(i); }
            // A large durable burst needs real fsync time; keep the virtual two-second slice
            // representative rather than advancing a whole virtual second every five ms.
            for (int tick = 0; tick < 6000 && h.Service.Snapshot.IndexCount != 1501; tick++)
            { clock.Advance(TimeSpan.FromMilliseconds(50)); await Task.Delay(5); }
            Assert.True(h.Service.Snapshot.IndexCount == 1501, $"index={h.Service.Snapshot.IndexCount}; reads={fake.Reads.Count}; distinct={fake.Reads.Distinct().Count()}; cause={h.Service.Snapshot.ErrorCode}");
            Assert.Equal(1500, fake.Reads.Where(id => id != Id(1)).Distinct().Count());
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task CoverageQuotaReaddKeepsOriginalAndRemovalHoldAndUncoversTheService()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); using var release = new ManualResetEventSlim();
        long now = clock.GetUtcNow().ToUnixTimeSeconds(); Seed(temp.Path, now, outbox: true);
        string setup = Identity.Hash("fixture");
        using (var j = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
            for (int i = 0; i < State.CoverageFolderCapacity / 2000; i++)
            {
                string[] folders = Enumerable.Range(i * 2000 + 1, 2000).Select(Id).ToArray();
                string proof = Identity.Hash(string.Concat(folders));
                j.Commit(new Change { Kind = "coverage", Coverage = new(proof, folders) },
                    new() { Kind = "remove", Tombstone = new(Id(500 + i), j.State.Seq + 1, now, 1, new(Id(10000 + i), now, setup, CoverageId: proof)) });
            }
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using (var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake,
            send: (request, _) => Task.FromResult(request.RequestUri!.Segments[^1] == "events" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : ApprovalResponse(request))))
        {
            await h.Service.StartAsync(default);
            try
            {
                await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)));
                fake.BlockedId = Id(1); fake.BlockOp = "get"; fake.Block = release;
                h.EmitRemove(fake.Items[Id(1)]); h.EmitAdd(1);
                await Pump(clock, () => h.Service.Snapshot.DroppedTotal >= 1);
                clock.Advance(TimeSpan.FromSeconds(601)); long barrier = clock.GetTimestamp();
                await Pump(clock, () => h.Service.Snapshot.ProgressStamp > barrier);
                Assert.DoesNotContain(SentEvents(h), e => e.GetProperty("op").GetString() == "rm");
                Assert.Empty(h.Service.CoveredLibraries); Assert.NotEqual("healthy", h.Service.Health().Health);
                await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4));
            }
            finally { release.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
        }
        using var resumed = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true);
        Assert.Equal("pendingAdd", resumed.State.Notices[Id(100)].Stage);
        var pendingRemoval = Assert.Single(resumed.State.Notices.Values, n => n.Op == "rm"); Assert.Equal("pendingRemove", pendingRemoval.Stage);
        var model = new Model(); model.Add(1); var engine = new Engine(resumed, model, "key");
        engine.AdvanceAdds(clock.GetUtcNow().ToUnixTimeSeconds(), _ => { });
        Assert.True(Rules.Audible(resumed.State.Notices[Id(100)], clock.GetUtcNow().ToUnixTimeSeconds()));
        Assert.DoesNotContain(resumed.State.Notices.Values, n => n.Op == "rm");
        Assert.Single(resumed.State.Notices.Values, n => n.Op == "add" && n.Quiet);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FirstReadFlapPreservesExactlyOneOriginalAlert(bool restartMidRead)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); using var release = new ManualResetEventSlim();
        Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds());
        var config = new PluginConfiguration { Enabled = true, Secret = "fixture" };
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        string? originalId = null; long receivedAt = 0;
        using (var h = new ServiceHarness(temp.Path, clock, config, compat: fake))
        {
            await h.Service.StartAsync(default);
            try
            {
                await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)));
                fake.Add(2); fake.BlockedId = Id(2); fake.BlockOp = "get"; fake.Block = release;
                receivedAt = clock.GetUtcNow().ToUnixTimeSeconds(); h.EmitAdd(2);
                await Pump(clock, () => fake.Entered != 0);
                if (restartMidRead)
                {
                    // Stop while the metadata call is still pending: only journaled work survives.
                    await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4));
                    using var saved = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true);
                    var pending = Assert.Single(saved.State.Notices.Values, n => n.Id == Id(2));
                    Assert.Equal("pendingAdd", pending.Stage); Assert.Equal("live", pending.Origin);
                    Assert.Equal(receivedAt, pending.FirstSeenAt); originalId = pending.EventId;
                }
                else
                {
                    clock.Advance(TimeSpan.FromSeconds(1)); h.EmitRemove(fake.Items[Id(2)]); h.EmitAdd(2);
                    long barrier = clock.GetTimestamp();
                    await Pump(clock, () => h.Service.Snapshot.ProgressStamp > barrier);
                    fake.BlockOp = null; release.Set();
                    await Pump(clock, () => SentEvents(h).Count(e => e.GetProperty("op").GetString() == "add") >= 2);
                    AssertOriginalAndRepair(SentEvents(h), receivedAt, null);
                }
            }
            finally { release.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
        }
        if (!restartMidRead) return;
        release.Reset();
        using var resumed = new ServiceHarness(temp.Path, clock, config, compat: fake);
        await resumed.Service.StartAsync(default);
        // Queue the rest of the flap before the restarted loop can resolve metadata.
        try
        {
            await Pump(clock, () => fake.Entered >= 2);
            resumed.EmitRemove(fake.Items[Id(2)]); resumed.EmitAdd(2);
            long barrier = clock.GetTimestamp();
            await Pump(clock, () => resumed.Service.Snapshot.ProgressStamp > barrier);
            fake.BlockOp = null; release.Set();
            await Pump(clock, () => SentEvents(resumed).Count(e => e.GetProperty("op").GetString() == "add") >= 2);
            AssertOriginalAndRepair(SentEvents(resumed), receivedAt, originalId);
        }
        finally { release.Set(); await resumed.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    private static void AssertOriginalAndRepair(IEnumerable<JsonElement> events, long receivedAt, string? originalId)
    {
        var adds = events.Where(e => e.GetProperty("op").GetString() == "add").ToArray();
        var original = Assert.Single(adds, e => !e.TryGetProperty("quiet", out var quiet) || !quiet.GetBoolean());
        Assert.Equal("live", original.GetProperty("origin").GetString());
        Assert.Equal(receivedAt, original.GetProperty("firstSeenAt").GetInt64());
        if (originalId is not null) Assert.Equal(originalId, original.GetProperty("e").GetString());
        Assert.Single(adds, e => e.TryGetProperty("quiet", out var quiet) && quiet.GetBoolean());
        Assert.DoesNotContain(events, e => e.GetProperty("op").GetString() == "summary");
    }

    [Fact] public async Task StartupScanCallbacksAreRecoveryEvenWhenProcessedAfterCoverageReturns()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); using var release = new ManualResetEventSlim(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), binding: true);
        var fake = new CompatFake { BlockOp = "ids", Block = release, Libraries = [FixtureLibrary(0)] }; fake.Add(1); fake.Add(2);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => fake.Entered != 0); h.EmitAdd(2);
            await Pump(clock, () => h.Service.Snapshot.IndexCount >= 2); Assert.Empty(h.Service.CoveredLibraries);
            release.Set(); await Pump(clock, () => SentEvents(h).Any(e => e.GetProperty("op").GetString() == "summary"));
            Assert.DoesNotContain(SentEvents(h), e => e.GetProperty("op").GetString() == "add");
            Assert.Single(SentEvents(h), e => e.GetProperty("op").GetString() == "summary");
        }
        finally { release.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task HungIdsDoesNotBlockCapturedLiveWorkOrCreateASecondCensusThread()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); using var release = new ManualResetEventSlim(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds());
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)));
            fake.BlockOp = "ids"; fake.Block = release; clock.Advance(TimeSpan.FromMinutes(16)); await Pump(clock, () => fake.Entered != 0);
            fake.Add(2); h.EmitAdd(2); await Pump(clock, () => h.Service.Snapshot.OutboxDepth > 0);
            clock.Advance(TimeSpan.FromSeconds(121));
            await Pump(clock, () => SentEvents(h).Any(e => e.GetProperty("op").GetString() == "add"));
            Assert.Equal("live", Assert.Single(SentEvents(h), e => e.GetProperty("op").GetString() == "add").GetProperty("origin").GetString());
            Assert.Equal(1, fake.Entered); Assert.Empty(h.Service.CoveredLibraries); Assert.Contains(h.Service.Snapshot.ErrorCode, new[] { "libraryStalled", "requiredCallTimeout" });
        }
        finally { release.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task RepairedRelayResumesEventsAfterFifteenMinutesWithoutWorkerRestart()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), outbox: true);
        int attempts = 0; var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake, send: async (request, ct) =>
        {
            if (request.RequestUri!.Segments[^1] != "events") return ApprovalResponse(request);
            if (Interlocked.Increment(ref attempts) == 1) return new(HttpStatusCode.Unauthorized);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { accepted = json.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("e").GetString()).ToArray() })) };
        });
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => attempts == 1 && h.Service.Snapshot.ErrorCode == "unauthorized"); string boot = h.Service.Snapshot.BootId;
            clock.Advance(TimeSpan.FromMinutes(14)); await Task.Delay(20); Assert.Equal(1, attempts);
            clock.Advance(TimeSpan.FromMinutes(1)); await Pump(clock, () => attempts == 2 && h.Service.Snapshot.OutboxDepth == 0);
            Assert.Equal(boot, h.Service.Snapshot.BootId); Assert.False(h.Service.ExecuteTask!.IsCompleted);
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task IndependentHeartbeatReportsStaleLoopWithBlockedSenderAndKnownCounters()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), outbox: true);
        var blocked = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake,
            send: (request, ct) => request.RequestUri!.Segments[^1] == "events" ? blocked.Task.WaitAsync(ct) : Task.FromResult(ApprovalResponse(request)));
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Posts.Any(p => p.Route == "events")); clock.Advance(TimeSpan.FromMinutes(10));
            await Pump(clock, () => h.Posts.Where(p => p.Route == "heartbeat").Any(p => { using var j = JsonDocument.Parse(p.Body); return j.RootElement.GetProperty("progressAge").GetInt64() > 300; }));
            Assert.Equal("not", h.Service.Health().Health); Assert.Equal(1, h.Service.Snapshot.OutboxDepth);
        }
        finally { blocked.TrySetResult(new(HttpStatusCode.ServiceUnavailable)); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public void ElapsedSliceBudgetStopsMetadataAndRemovalWorkBeforeTraversingTheQueue()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model(); var e = new Engine(j, m, "key");
        for (int i = 1; i <= 20; i++) { m.Add(i); e.QueueAdd(Id(i), "Movie", Id(100 + i), 1000, Setup); }
        int checks = 0; e.BudgetAvailable = () => ++checks <= 2; e.AdvanceAdds(1001, _ => { });
        Assert.Equal(2, j.State.Notices.Values.Count(n => n.Stage == "outbox")); Assert.Equal(18, j.State.Notices.Values.Count(n => n.Stage == "pendingAdd"));
    }

    [Fact] public async Task UpdatedCallbackRunsReadinessBeforeThirtySecondBackstop()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds());
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)));
            fake.ReadyValue = false; fake.Add(2); h.EmitAdd(2); await Pump(clock, () => fake.Reads.Contains(Id(2)));
            fake.ReadyValue = true; h.EmitUpdate(2); long start = clock.GetTimestamp();
            await Pump(clock, () => h.Service.Snapshot.OutboxDepth > 0);
            Assert.True(clock.GetElapsedTime(start) < TimeSpan.FromSeconds(30));
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [WindowsFact] public async Task RealSharingViolationDuringMigrationReopensAfterLockRelease()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds());
        using var blocker = new FileStream(Path.Combine(temp.Path, "journal.ndjson"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration(), retry: (_, ct) => Task.Delay(10, ct));
        await h.Service.StartAsync(default);
        try
        {
            await Until(() => h.Service.Snapshot.ErrorCode == "stateUnavailable"); blocker.Dispose();
            await Pump(clock, () => !h.Service.Snapshot.StorageUnavailable); Assert.False(h.Service.ExecuteTask!.IsCompleted);
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task DelayedAdditionReadCannotResurrectAnItemRemovedBeforeItCompletes()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); using var release = new ManualResetEventSlim(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds());
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)));
            fake.Add(2); var removed = fake.Items[Id(2)]; fake.BlockOp = "get"; fake.Block = release; fake.BlockedId = Id(2); h.EmitAdd(2);
            await Pump(clock, () => fake.Entered != 0); fake.Items.TryRemove(Id(2), out _); h.EmitRemove(removed);
            clock.Advance(TimeSpan.FromSeconds(1)); await Until(() => h.Service.Snapshot.ProgressStamp == clock.GetTimestamp());
            release.Set(); await Until(() => fake.Returned != 0);
            for (int i = 0; i < 90; i++) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); }
            await Until(() => h.Service.Snapshot.ProgressStamp == clock.GetTimestamp());
            Assert.Equal(1, h.Service.Snapshot.IndexCount); Assert.DoesNotContain(SentEvents(h), e => e.GetProperty("op").GetString() == "add");
        }
        finally { release.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task MoreThanTwentyExpiredSetupsAcrossRestartsCannotPoisonTheWorker()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); var config = new PluginConfiguration { Enabled = true, Secret = "fixture-0" };
        for (int generation = 0; generation < 22; generation++)
        {
            config.Secret = "fixture-" + generation; clock.Advance(TimeSpan.FromDays(2));
            using var h = new ServiceHarness(temp.Path, clock, config, retry: (_, ct) => Task.Delay(10, ct));
            await h.Service.StartAsync(default);
            try { await Pump(clock, () => h.Service.Snapshot.ProgressStamp == clock.GetTimestamp() && !h.Service.Snapshot.StorageUnavailable && h.Service.Snapshot.StateBytes > 0); }
            finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
        }
        using var j = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true);
        Assert.Single(j.State.Recoveries); Assert.Contains(Identity.Hash(config.Secret), j.State.Recoveries.Keys);
    }

    [Fact] public async Task ThirtyMinuteVirtualSoakSurvivesSharingFailuresRelay5xxAndBlockedCensus()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); using var release = new ManualResetEventSlim(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), binding: true);
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)], BlockOp = "ids", Block = release }; fake.Add(1);
        int opens = 0, minute = 0, failedPosts = 0;
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake,
            factory: (path, root, token, owner) => ++opens <= 2 ? throw new IOException("private-state-path", unchecked((int)0x80070020)) : new Journal(path, root, token, TimeSpan.FromSeconds(3), owner, versioned: true),
            retry: (_, ct) => Task.Delay(1, ct), send: async (request, ct) =>
            {
                if (request.RequestUri!.Segments[^1] != "events") return ApprovalResponse(request);
                if (Volatile.Read(ref minute) < 15) { Interlocked.Increment(ref failedPosts); return new(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60)) } }; }
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { accepted = json.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("e").GetString()).ToArray() })) };
            });
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => fake.Entered != 0); fake.Add(2); h.EmitAdd(2);
            for (minute = 1; minute <= 30; minute++)
            {
                clock.Advance(TimeSpan.FromMinutes(1)); if (minute == 20) release.Set();
                await Task.Delay(15);
            }
            await Pump(clock, () => SentEvents(h).Any(e => e.GetProperty("op").GetString() == "summary") && h.Service.Snapshot.OutboxDepth == 0 && h.Service.CoveredLibraries.Contains(Id(8)));
            Assert.Equal(3, opens); Assert.True(failedPosts > 0); Assert.InRange(h.Posts.Count(p => p.Route == "heartbeat"), 3, 12);
            Assert.DoesNotContain(SentEvents(h), e => e.GetProperty("op").GetString() == "add");
            Assert.Single(SentEvents(h).Where(e => e.GetProperty("op").GetString() == "summary").Select(e => e.GetProperty("e").GetString()).Distinct());
        }
        finally { release.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task ThreeHourScanNeverPromotesItsDiscoveryToLive()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); using var release = new ManualResetEventSlim(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), binding: true);
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)));
            fake.Add(2); fake.BlockedId = Id(2); fake.BlockOp = "get"; fake.Block = release; clock.Advance(TimeSpan.FromMinutes(16));
            await Pump(clock, () => fake.Entered != 0); clock.Advance(TimeSpan.FromHours(3));
            await Pump(clock, () => h.Service.Snapshot.ErrorCode == "jellyfinStalled"); release.Set();
            await Pump(clock, () => SentEvents(h).Any(e => e.GetProperty("op").GetString() == "summary"));
            Assert.DoesNotContain(SentEvents(h), e => e.GetProperty("op").GetString() == "add");
        }
        finally { release.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task DroppedCallbacksUncoverAtReceiptAndHeartbeatAcknowledgesOnlyReportedDrops()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), binding: true);
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8))); fake.Add(2); fake.Add(3);
            for (int i = 0; i <= State.Capacity; i++) h.EmitAdd(2);
            Assert.Empty(h.Service.CoveredLibraries); h.EmitAdd(3);
            await Pump(clock, () => h.Service.Snapshot.Dropped >= 2 && h.Service.Snapshot.IndexCount == 3);
            await Pump(clock, () => SentEvents(h).Any(e => e.GetProperty("op").GetString() == "summary"));
            Assert.DoesNotContain(SentEvents(h), e => e.GetProperty("op").GetString() == "add" && e.GetProperty("id").GetString() == Id(3));
            clock.Advance(TimeSpan.FromMinutes(10)); await Pump(clock, () => h.Service.Snapshot.Dropped == 0);
            Assert.Contains(h.Posts.Where(p => p.Route == "heartbeat"), p => { using var json = JsonDocument.Parse(p.Body); return json.RootElement.GetProperty("droppedSinceLast").GetInt64() >= 2; });
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Theory] [InlineData(-3600)] [InlineData(3600)]
    public async Task WallClockStepsFoldPendingLiveWorkWithoutRefreshingItsObservation(int seconds)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), binding: true, outbox: true);
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1); int events = 0;
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake,
            send: (request, _) => Task.FromResult(request.RequestUri!.Segments[^1] == "events" ? (Interlocked.Increment(ref events) > 0 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : throw new InvalidOperationException()) : ApprovalResponse(request)));
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => events == 1 && h.Service.Snapshot.ErrorCode == "unauthorized");
            long first = Assert.Single(SentEvents(h)).GetProperty("firstSeenAt").GetInt64();
            clock.Jump(TimeSpan.FromSeconds(seconds)); clock.Advance(TimeSpan.FromSeconds(1));
            await Pump(clock, () => h.Service.Snapshot.ProgressStamp == clock.GetTimestamp());
            await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4));
            using var j = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true);
            var original = j.State.Notices[Id(100)]; Assert.True(original.Folded || original.EnqueuedSeq <= j.State.Health.QuietThroughSeq); Assert.Equal(first, original.FirstSeenAt); Assert.Equal("live", original.Origin); Assert.False(Rules.Audible(original, clock.GetUtcNow().ToUnixTimeSeconds()));
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public void ProvenanceCannotBeRefreshedAndSetupPruningRollsBackAtomically()
    {
        var s = new State(); s.Apply(new() { Kind = "notice", Notice = Event() with { SetupHash = Setup } });
        Assert.Throws<InvalidDataException>(() => s.Apply(new() { Kind = "notice", Notice = s.Notices[Id(1)] with { FirstSeenAt = 200 } }));
        Assert.Throws<InvalidDataException>(() => s.Apply(new() { Kind = "notice", Notice = s.Notices[Id(1)] with { Origin = "recovery" } }));
        s.Apply(new() { Kind = "binding", Binding = new(Id(999), "profile", UserHash, Setup) });
        s.Apply(new() { Kind = "recovery", Recovery = new(Id(1000), Setup, 1000) });
        s.Apply(new() { Kind = "recoveryItem", RecoveryItem = new(Id(100), Id(1000), Setup) });
        Assert.Throws<InvalidDataException>(() => s.Transaction([new() { Kind = "unsetup", Key = Setup }, new() { Kind = "health", Health = new(Dropped: -1) }]));
        Assert.Single(s.Notices); Assert.Single(s.Bindings); Assert.Single(s.Recoveries); Assert.Single(s.RecoveryItems);
        s.Apply(new() { Kind = "unsetup", Key = Setup }); Assert.Empty(s.Notices); Assert.Empty(s.Bindings); Assert.Empty(s.Recoveries); Assert.Empty(s.RecoveryItems);
    }

    [Fact] public async Task RemovalCensusRetainsItsCursorWhenExistingTitlesMovedToAnotherFolder()
    {
        using var temp = new TempDirectory(); var clock = new Clock();
        using (var j = new Journal(temp.Path))
        {
            j.State.Apply(new() { Kind = "library", Library = FixtureLibrary(0) });
            for (int i = 1; i <= 40; i++) j.State.Apply(new() { Kind = "title", Title = new(Id(i), "Movie", [Id(8)], null, 1, 0) });
            j.Compact();
        }
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)], CensusIds = [], DescribeFolders = [Id(9)] };
        for (int i = 1; i <= 40; i++) fake.Add(i);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try { await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8))); }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
        using var restored = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true);
        Assert.All(restored.State.Index.Values, t => Assert.Equal(new[] { Id(9) }, t.Folders));
    }

    [Fact] public async Task FailedInventoryCannotBeMadeHealthyByScanningOnlyPreviouslyKnownLibraries()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds());
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)) && h.Service.Health().Health == "healthy");
            fake.FailInventory = true; clock.Advance(TimeSpan.FromMinutes(2));
            await Pump(clock, () => h.Service.Snapshot.ErrorCode == "inventoryIncomplete");
            clock.Advance(TimeSpan.FromMinutes(31));
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)) && h.Service.Snapshot.ProgressStamp == clock.GetTimestamp());
            Assert.Equal("not", h.Service.Health().Health); Assert.NotNull(h.Service.Snapshot.UncoveredStamp); Assert.Equal("inventoryIncomplete", h.Service.Snapshot.ErrorCode);
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task HeartbeatRefreshesVerifiedAdminsWhileTheProcessingLoopIsBlocked()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), outbox: true);
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        var blocked = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake,
            send: (request, ct) => request.RequestUri!.Segments[^1] == "events" ? blocked.Task.WaitAsync(ct) : Task.FromResult(ApprovalResponse(request)));
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Posts.Any(p => p.Route == "events"));
            fake.AdminHashes = ["abcdef1234567890"]; clock.Advance(TimeSpan.FromMinutes(10));
            await Pump(clock, () => h.Posts.Count(p => p.Route == "heartbeat") >= 2);
            using var last = JsonDocument.Parse(h.Posts.Last(p => p.Route == "heartbeat").Body);
            Assert.Equal(new[] { "abcdef1234567890" }, last.RootElement.GetProperty("adminUserHashes").EnumerateArray().Select(v => v.GetString()));
        }
        finally { blocked.TrySetResult(new(HttpStatusCode.ServiceUnavailable)); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task AnUncoveredReceiptCannotBePromotedByMovingBeforeFolderResolution()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); using var release = new ManualResetEventSlim(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), binding: true);
        var b = new Library(Identity.Hash("other"), Id(9), [], true, true);
        string rootA = Path.Combine(Path.GetTempPath(), "jellysee-fixture-library"), rootB = Path.Combine(Path.GetTempPath(), "jellysee-other-library");
        var fake = new CompatFake { Libraries = [FixtureLibrary(0), b], BlockOp = "ids", BlockedLibrary = Id(9), Block = release, Roots = [new(Id(8), rootA), new(Id(9), rootB)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8)) && fake.Entered != 0);
            fake.Add(2); fake.Items[Id(2)].Path = Path.Combine(rootB, "2.mkv"); h.EmitAdd(2); fake.Items[Id(2)].Path = Path.Combine(rootA, "2.mkv");
            await Pump(clock, () => h.Service.Snapshot.IndexCount == 2); clock.Advance(TimeSpan.FromMinutes(10));
            await Pump(clock, () => SentEvents(h).Any(e => e.GetProperty("op").GetString() == "summary"));
            Assert.DoesNotContain(SentEvents(h), e => e.GetProperty("op").GetString() == "add");
        }
        finally { release.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public void CachedSummaryEntitlementKeepsItsActualEvaluationTimestampOnTheWire()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var m = new Model { SampleAt = 1001 }; m.Add(1); Bound(j);
        var e = new Engine(j, m, "key"); e.BeginRecovery(Setup, 1000); e.QueueAdd(Id(1), "Movie", Id(100), 1001, Setup, "recovery"); e.AdvanceAdds(1002, _ => { }); e.AdvanceRecovery(Setup, 1002, true);
        var summary = Assert.Single(j.State.Notices.Values, n => n.Op == "summary");
        Assert.Equal(1001, e.Freshen(summary, 1100)!.At);
        var expired = e.Freshen(summary, 2302)!; Assert.Empty(expired.Audience); Assert.Equal(2302, expired.At);
    }

    [Theory] [InlineData(-3600, false)] [InlineData(3600, false)] [InlineData(-3600, true)] [InlineData(3600, true)]
    public async Task ClockCorrectionAndCredentialRotationInEitherOrderRetainTheOldSetup(int correction, bool rotationFirst)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), binding: true, outbox: true);
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        var config = new PluginConfiguration { Enabled = true, Secret = "fixture" };
        using var h = new ServiceHarness(temp.Path, clock, config, compat: fake, send: (request, _) => Task.FromResult(request.RequestUri!.Segments[^1] == "events" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : ApprovalResponse(request)));
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Posts.Any(p => p.Route == "events"));
            if (!rotationFirst) clock.Jump(TimeSpan.FromSeconds(correction));
            config.RetiredSecrets = Credentials.Rotate(config, "rotated", clock.GetUtcNow().ToUnixTimeSeconds()); config.Secret = "rotated";
            if (rotationFirst) { await Pump(clock, () => h.Service.Snapshot.SetupHash == Identity.Hash("rotated")); clock.Jump(TimeSpan.FromSeconds(correction)); }
            clock.Advance(TimeSpan.FromSeconds(1)); await Pump(clock, () => h.Service.Snapshot.SetupHash == Identity.Hash("rotated") && h.Service.Snapshot.ProgressStamp == clock.GetTimestamp());
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
        using var j = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true);
        Assert.Contains(j.State.Bindings.Values, b => b.SetupHash == Identity.Hash("fixture")); Assert.Contains(Identity.Hash("fixture"), j.State.Recoveries.Keys);
    }

    [Fact] public void FutureRetirementIsPreservedUntilExpiryCanBeEstablished()
    {
        var old = new PluginConfiguration { Secret = "current", RetiredSecrets = [new() { Secret = "retained", RetiredAt = 10000 }] };
        Assert.Contains(Identity.Hash("retained"), Credentials.Available(old, 6400).Keys);
        var retained = Credentials.Rotate(old, "next", 6400); Assert.Contains(retained, c => c.Secret == "retained");
        Assert.DoesNotContain(Identity.Hash("retained"), Credentials.Available(old, 96400).Keys);
    }

    [Theory] [InlineData("quiet")] [InlineData("recovery")] [InlineData("expired")]
    public void QuietRecoveryOrExpiredRemovalCannotSilenceAnAudibleBulkWindow(string disposition)
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var e = new Engine(j, new Model(), "key");
        long added = disposition == "expired" ? 1500 : 1000, now = disposition == "expired" ? 2801 : 1601;
        for (int i = 1; i <= 201; i++) e.Put(Event(i) with { Op = "rm", Stage = "pendingRemove", Folders = [Id(8)], QueuedAt = added, SetupHash = Setup });
        e.Put(Event(202) with { Op = "rm", Stage = "pendingRemove", Folders = [Id(8)], QueuedAt = 1000, SetupHash = Setup, Quiet = disposition == "quiet", Origin = disposition == "recovery" ? "recovery" : "live" });
        e.AdvanceRemovals(now, _ => { });
        var bulk = Assert.Single(j.State.Notices.Values, n => n.Op == "rmbulk"); Assert.Equal(201, bulk.Count); Assert.True(Rules.Audible(bulk, now));
        Assert.True(Assert.Single(j.State.Notices.Values, n => n.Op == "rm").Quiet);
        j.Compact();
    }

    [Fact] public void LargeValidRecoverySnapshotReplaysWithinTheProductionInitializationDeadline()
    {
        using var temp = new TempDirectory();
        using (var j = new Journal(temp.Path))
        {
            j.Commit(new Change { Kind = "recovery", Recovery = new(Id(999999), Setup, 1000) });
            for (int i = 1; i <= 40000; i++) { var item = new RecoveryItem(Id(i), Id(999999), Setup); j.State.RecoveryItems.Add(item.Key, item); }
            j.Compact();
        }
        using var reopened = new Journal(temp.Path); Assert.Equal(40000, reopened.State.RecoveryItems.Count); Assert.False(reopened.RecoveredCorruption);
    }

    [Fact] public async Task ProductionRemovalCacheCannotRefreshARevokedGrantIntoASecondFreshnessWindow()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var clock = new Clock(); var fake = new CompatFake();
        using var lanes = new LibraryLanes(fake, "key", clock); var e = new Engine(j, lanes, "key"); long at = clock.GetUtcNow().ToUnixTimeSeconds();
        await Poll(() => lanes.Audience(null, [Id(8)], at)); clock.Advance(TimeSpan.FromMinutes(19));
        e.Put(Event(100) with { Op = "rm", Stage = "pendingRemove", Folders = [Id(8)], QueuedAt = at + 540, SetupHash = Setup });
        e.AdvanceRemovals(at + 1140, _ => { }); var removal = j.State.Notices[Id(100)]; Assert.Equal(at, removal.At);
        fake.AudienceAllowed = false; clock.Advance(TimeSpan.FromMinutes(15)); Notice? result = removal;
        await Poll(() => result = e.Freshen(removal, at + 2040)); Assert.Null(result); Assert.Equal(2, fake.AudienceCalls);
    }

    [Fact] public void RunningRefreshMediaLibraryTaskUsesJellyfinsActualScheduledTaskKey()
    {
        bool running = true;
        var task = Proxy<IScheduledTask>((method, _) => method.Name == "get_Key" ? "RefreshLibrary" : throw new InvalidOperationException());
        var worker = Proxy<IScheduledTaskWorker>((method, _) => method.Name switch { "get_ScheduledTask" => task, "get_State" => running ? TaskState.Running : TaskState.Idle, _ => throw new InvalidOperationException() });
        var manager = Proxy<ITaskManager>((method, _) => method.Name == "get_ScheduledTasks" ? new[] { worker } : throw new InvalidOperationException());
        using var lanes = new LibraryLanes(new CompatFake(), "key", new Clock(), manager);
        Assert.True(lanes.ScanRunning); running = false; Assert.False(lanes.ScanRunning);
    }

    [Fact] public async Task CensusReadinessCacheCannotPostponeTheFifteenMinuteFallback()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path); var clock = new Clock(); var fake = new CompatFake { ReadyValue = false }; fake.Add(1);
        using var lanes = new LibraryLanes(fake, "key", clock); var e = new Engine(j, lanes, "key"); long at = clock.GetUtcNow().ToUnixTimeSeconds();
        e.QueueAdd(Id(1), "Movie", Id(100), at, Setup); clock.Advance(TimeSpan.FromSeconds(899));
        await Poll(() => lanes.Read(Id(1), at + 899, at + 899)); clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => { e.AdvanceAdds(at + 900, _ => { }); return j.State.Notices[Id(100)].Stage == "outbox"; });
        Assert.Equal(at, j.State.Notices[Id(100)].FirstSeenAt);
    }

    [Theory] [InlineData(-3600)] [InlineData(3600)]
    public async Task CorrectingAnInitiallyWrongClockRebasesNewWireObservationsWithoutRestart(int error)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); clock.Jump(TimeSpan.FromSeconds(error)); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds());
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake);
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8))); clock.Jump(TimeSpan.FromSeconds(-error)); long corrected = clock.GetUtcNow().ToUnixTimeSeconds();
            clock.Advance(TimeSpan.FromSeconds(1)); await Pump(clock, () => h.Service.Snapshot.ProgressStamp == clock.GetTimestamp() && h.Service.CoveredLibraries.Contains(Id(8)));
            fake.Add(2); h.EmitAdd(2); await Pump(clock, () => SentEvents(h).Any(e => e.GetProperty("op").GetString() == "add"));
            var addition = Assert.Single(SentEvents(h), e => e.GetProperty("op").GetString() == "add");
            Assert.InRange(addition.GetProperty("firstSeenAt").GetInt64(), corrected, corrected + 240); Assert.InRange(addition.GetProperty("at").GetInt64(), corrected, corrected + 240);
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Theory] [InlineData(1)] [InlineData(500)]
    public async Task BindingRetentionHasNoPerUserLimitAndRejectsIdentityReplacement(int victims)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); string setup = Identity.Hash("fixture"), attacker = "bbbbbbbbbbbbbbbb";
        using (var j = new Journal(temp.Path))
        {
            for (int i = 0; i < victims; i++) j.State.Apply(new() { Kind = "binding", Binding = new(Id(1000 + i), "victim-" + i, UserHash, setup, i) });
            j.Compact();
        }
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }); await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.Snapshot.Bindings.Length == victims && h.Service.Snapshot.StateBytes > 0);
            for (int i = 0; i < (victims == 500 ? 1 : 70); i++)
            {
                var bind = h.Service.Bind(Id(100000 + i), "attacker-" + i, attacker, default); await Pump(clock, () => bind.IsCompleted);
                Assert.True(await bind);
            }
            await Pump(clock, () => h.Service.Snapshot.ProgressStamp == clock.GetTimestamp());
            Assert.Equal(victims == 500 ? 499 : victims, h.Service.Snapshot.Bindings.Count(b => b.UserHash == UserHash)); Assert.Equal(victims == 500 ? 1 : 70, h.Service.Snapshot.Bindings.Count(b => b.UserHash == attacker));
            var replace = h.Service.Bind(Id(1000 + victims - 1), "attacker", attacker, default); await Pump(clock, () => replace.IsCompleted); Assert.False(await replace);
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Theory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task ContinuousRebindingCannotStrandAnotherUsersBindingOrEvents(bool summary, bool newRegistrations, bool atCapacity)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); long now = clock.GetUtcNow().ToUnixTimeSeconds();
        string setup = Identity.Hash("fixture"), attacker = "bbbbbbbbbbbbbbbb";
        Seed(temp.Path, now);
        using (var j = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
        {
            for (int i = 0; i < (atCapacity ? 499 : 64); i++) j.Commit(new Change { Kind = "binding", Binding = new(Id(1000 + i), "victim", attacker, setup, now, j.State.Seq + 1) });
            j.Commit(new Change { Kind = "binding", Binding = new(Id(999), "victim", UserHash, setup, now, j.State.Seq + 1) });
            var e = new Engine(j, new Model(), "key");
            if (summary)
            {
                var recovery = e.BeginRecovery(setup, now - 301);
                j.Commit(new Change { Kind = "recovery", Recovery = recovery with { Complete = true, Emitted = true, HasAddition = true } },
                    new() { Kind = "recoveryItem", RecoveryItem = new(Id(1), recovery.Id, setup) });
                e.Put(new Notice { EventId = Id(100), Op = "summary", Stage = "outbox", SetupHash = setup, QueuedAt = now - 300, FirstSeenAt = now - 300, At = now,
                    OutageStart = now - 301, OutageId = recovery.Id, Profile = "victim", PresentationKey = Identity.PresentationKey("key", "victim") });
            }
            else e.Put(Event(100) with { Id = Id(1), SetupHash = setup, QueuedAt = now - 300, FirstSeenAt = now - 300, At = now, Audience = [UserHash] });
        }
        ServiceHarness? running = null; bool victimReported = false; int generation = 0; var refreshes = new ConcurrentBag<Task<bool>>();
        var fake = new CompatFake { Libraries = [FixtureLibrary(now)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake, send: async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.RequestUri!.Segments[^1] == "bindings")
            {
                victimReported |= json.RootElement.GetProperty("bindings").EnumerateArray().Any(b => b.GetProperty("registration").GetString() == Id(999));
                int offset = newRegistrations ? 64 * ++generation : 0;
                for (int i = 0; i < 64; i++) refreshes.Add(running!.Service.Bind(Id(1000 + offset + i), "victim", attacker, default));
            }
            return ApprovalResponse(request);
        });
        running = h; await h.Service.StartAsync(default);
        try { await Pump(clock, () => victimReported && SentEvents(h).Any()); Assert.Equal(summary ? "summary" : "add", Assert.Single(SentEvents(h)).GetProperty("op").GetString()); }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); await Task.WhenAll(refreshes).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task RetainedSetupStillSubmitsItsSummaryAfterRotationAndAnExpiredAddition()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), binding: true, outbox: true);
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1); var config = new PluginConfiguration { Enabled = true, Secret = "fixture" };
        bool repaired = false; var submittedWith = new ConcurrentQueue<string>();
        using var h = new ServiceHarness(temp.Path, clock, config, compat: fake, send: async (request, ct) =>
        {
            if (request.RequestUri!.Segments[^1] != "events") return ApprovalResponse(request);
            if (!repaired) return new(HttpStatusCode.Unauthorized);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (json.RootElement.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("op").GetString() == "summary")) submittedWith.Enqueue(request.Headers.Authorization!.Parameter!);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { accepted = json.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("e").GetString()).ToArray() })) };
        });
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Posts.Any(p => p.Route == "events")); config.RetiredSecrets = Credentials.Rotate(config, "rotated", clock.GetUtcNow().ToUnixTimeSeconds()); config.Secret = "rotated"; repaired = true;
            clock.Advance(TimeSpan.FromMinutes(31)); await Pump(clock, () => !submittedWith.IsEmpty);
            Assert.Equal("fixture", Assert.Single(submittedWith)); Assert.Single(SentEvents(h), e => e.GetProperty("op").GetString() == "summary");
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Theory] [InlineData(true, -3600)] [InlineData(true, 3600)] [InlineData(false, -3600)] [InlineData(false, 3600)]
    public async Task ClockCorrectionsDuringInitializationOrBackoffAreNotAbsorbedByAReopen(bool initialization, int error)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); clock.Jump(TimeSpan.FromSeconds(error)); Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds());
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int opens = 0;
        var fake = new CompatFake { Libraries = [FixtureLibrary(0)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake,
            factory: (path, root, token, owner) =>
            {
                if (++opens == 1)
                {
                    if (!initialization) throw new IOException("private-state-path");
                    entered.TrySetResult(); release.Wait();
                }
                return new Journal(path, root, token, TimeSpan.FromSeconds(3), owner, versioned: true);
            }, retry: (_, ct) => { entered.TrySetResult(); return Task.Run(() => release.Wait(ct), ct); });
        await h.Service.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); clock.Jump(TimeSpan.FromSeconds(-error)); long corrected = clock.GetUtcNow().ToUnixTimeSeconds(); release.Set();
            await Pump(clock, () => h.Service.CoveredLibraries.Contains(Id(8))); fake.Add(2); h.EmitAdd(2);
            await Pump(clock, () => SentEvents(h).Any(e => e.GetProperty("op").GetString() == "add"));
            Assert.InRange(Assert.Single(SentEvents(h), e => e.GetProperty("op").GetString() == "add").GetProperty("firstSeenAt").GetInt64(), corrected, corrected + 240);
        }
        finally { release.Set(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task BindWaitsForRelayAcceptanceAndFailureIsRetryable()
    {
        using var temp = new TempDirectory(); var clock = new Clock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fail = true, entered = false;
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" },
            send: async (request, ct) => {
                if (request.RequestUri!.Segments[^1] != "bindings") return ApprovalResponse(request);
                entered = true; await release.Task.WaitAsync(ct);
                return fail ? new(HttpStatusCode.ServiceUnavailable) : ApprovalResponse(request);
            });
        await h.Service.StartAsync(default);
        try
        {
            var controller = Controller(h.Service, Guid.ParseExact(Id(1), "N"));
            var bind = controller.Bind(new(Id(100), "profile"), default);
            await Pump(clock, () => entered);
            Assert.False(bind.IsCompleted);
            release.SetResult(); await Pump(clock, () => bind.IsCompleted);
            Assert.Equal(503, Assert.IsType<StatusCodeResult>(await bind).StatusCode);
            fail = false; clock.Advance(TimeSpan.FromMinutes(2));
            var retry = controller.Bind(new(Id(100), "profile"), default);
            await Pump(clock, () => retry.IsCompleted); Assert.IsType<OkResult>(await retry);
        }
        finally { release.TrySetResult(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task SixtyFiveProfilesSurviveBindingAndRestartAndAllHaveEntitledSummaries()
    {
        using var temp = new TempDirectory(); var clock = new Clock();
        string setup = Identity.Hash("fixture");
        using (var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }))
        {
            await h.Service.StartAsync(default);
            try
            {
                for (int i = 0; i < 65; i++)
                {
                    var bind = h.Service.Bind(Id(1000 + i), "profile-" + i, UserHash, default);
                    await Pump(clock, () => bind.IsCompleted); Assert.True(await bind);
                    clock.Advance(TimeSpan.FromSeconds(61)); // respect the shared relay budget
                }
                await Pump(clock, () => h.Service.Snapshot.Bindings.Length == 65);
            }
            finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
        }
        using var j = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true);
        Assert.Equal(65, j.State.Bindings.Count);
        var model = new Model(); model.Add(1);
        long now = clock.GetUtcNow().ToUnixTimeSeconds(); var engine = new Engine(j, model, Identity.ServerKey("fixture"));
        engine.BeginRecovery(setup, now); engine.QueueAdd(Id(1), "Movie", Id(100), now, setup, "recovery");
        engine.AdvanceAdds(now + 1, _ => { });
        foreach (var n in j.State.Notices.Values.ToArray()) engine.Freshen(n, now + 2);
        engine.AdvanceRecovery(setup, now + 2, true);
        var summaries = j.State.Notices.Values.Where(n => n.Op == "summary").ToArray();
        Assert.Equal(65, summaries.Select(n => n.Profile).Distinct().Count());
        Assert.All(summaries, n => Assert.Equal(new[] { UserHash }, engine.Freshen(n, now + 2)!.Audience));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AtCapacityEvictsStaleBeforeOldestCurrent(bool stale)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); string setup = Identity.Hash("fixture");
        using (var j = new Journal(temp.Path))
        {
            for (int i = 0; i < 500; i++) j.State.Apply(new Change { Kind = "binding", Binding = new(Id(1000 + i), "p" + i, UserHash, setup, i, i + 1) });
            j.Compact();
        }
        var roster = Enumerable.Range(0, 500).Where(i => !stale || i != 200).Select(i => Id(1000 + i)).ToArray();
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" },
            send: (request, _) => {
                using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                if (request.RequestUri!.Segments[^1] != "bindings") return Task.FromResult(ApprovalResponse(request));
                var accepted = json.RootElement.GetProperty("bindings").EnumerateArray().Select(b => b.GetProperty("registration").GetString()).ToArray();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { currentRegistrations = RosterKeys(request, roster), accepted })) });
            });
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.Snapshot.Bindings.Length == 500);
            var bind = h.Service.Bind(Id(9999), "new", UserHash, default); await Pump(clock, () => bind.IsCompleted); Assert.True(await bind);
            await Pump(clock, () => h.Service.Snapshot.Bindings.Any(b => b.Registration == Id(9999)));
            Assert.Equal(500, h.Service.Snapshot.Bindings.Length);
            Assert.DoesNotContain(h.Service.Snapshot.Bindings, b => b.Registration == Id(stale ? 1200 : 1000));
            if (stale) Assert.Contains(h.Service.Snapshot.Bindings, b => b.Registration == Id(1000));
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public async Task RejectedAdmissionAtCapacityPreservesEveryDurableApproval()
    {
        using var temp = new TempDirectory(); var clock = new Clock(); string setup = Identity.Hash("fixture");
        using (var j = new Journal(temp.Path))
        {
            for (int i = 0; i < 500; i++) j.State.Apply(new Change { Kind = "binding", Binding = new(Id(1000 + i), "p" + i, UserHash, setup, i, i + 1) });
            j.Compact();
        }
        var roster = Enumerable.Range(1000, 500).Select(Id).ToArray();
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" },
            send: (request, _) => {
                if (request.RequestUri!.Segments[^1] != "bindings") return Task.FromResult(ApprovalResponse(request));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                    currentRegistrations = RosterKeys(request, roster), accepted = Array.Empty<string>() })) });
            });
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Service.Snapshot.Bindings.Length == 500);
            var bind = h.Service.Bind(Id(9999), "candidate", UserHash, default);
            await Pump(clock, () => bind.IsCompleted); Assert.False(await bind);
            Assert.Equal(roster.Order(), h.Service.Snapshot.Bindings.Select(b => b.Registration).Order());
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task RejectedRegistrationCannotBlockCurrentProfilesSummaryAfterRestart(bool rotated, bool currentMismatch)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); long now = clock.GetUtcNow().ToUnixTimeSeconds();
        string setup = Identity.Hash("fixture"); Seed(temp.Path, now, binding: true);
        using (var j = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
        {
            j.Commit(new Change { Kind = "binding", Binding = new(Id(1000), "profile", UserHash, setup, now, j.State.Seq + 1) });
            var e = new Engine(j, new Model(), Identity.ServerKey("fixture")); var recovery = e.BeginRecovery(setup, now - 301);
            j.Commit(new Change { Kind = "recovery", Recovery = recovery with { Complete = true, Emitted = true, HasAddition = true } },
                new() { Kind = "recoveryItem", RecoveryItem = new(Id(1), recovery.Id, setup) });
            e.Put(new Notice { EventId = Id(100), Op = "summary", Stage = "outbox", SetupHash = setup, QueuedAt = now - 300, FirstSeenAt = now - 300, At = now,
                OutageStart = now - 301, OutageId = recovery.Id, Profile = "profile", PresentationKey = Identity.PresentationKey(Identity.ServerKey("fixture"), "profile") });
        }
        var fake = new CompatFake { Libraries = [FixtureLibrary(now)] }; fake.Add(1);
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = rotated ? "rotated" : "fixture",
                RetiredSecrets = rotated ? [new() { Secret = "fixture", RetiredAt = now }] : [] }, compat: fake,
            send: (request, _) => {
                if (request.RequestUri!.Segments[^1] != "bindings") return Task.FromResult(ApprovalResponse(request));
                using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var accepted = json.RootElement.GetProperty("bindings").EnumerateArray().Select(b => b.GetProperty("registration").GetString()).Where(id => id == Id(999)).ToArray();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { currentRegistrations = RosterKeys(request, currentMismatch ? [Id(999), Id(1000)] : [Id(999)]), accepted })) });
            });
        await h.Service.StartAsync(default);
        try { await Pump(clock, () => SentEvents(h).Any()); Assert.Equal("summary", Assert.Single(SentEvents(h)).GetProperty("op").GetString()); }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [Fact] public void RecoveryAdditionSurvivesACompleteCensusWhileApprovalIsPending()
    {
        using var temp = new TempDirectory(); using var j = new Journal(temp.Path);
        var model = new Model(); model.Add(1); Bound(j);
        var engine = new Engine(j, model, "key"); var recovery = engine.BeginRecovery(Setup, 1000);
        j.Commit(new Change { Kind = "recovery", Recovery = recovery with { HasAddition = true } },
            new() { Kind = "recoveryItem", RecoveryItem = new(Id(1), recovery.Id, Setup) });
        engine.AdvanceRecovery(Setup, 1001, true, waitForApproval: true);
        Assert.True(j.State.Recoveries[Setup].Complete); Assert.False(j.State.Recoveries[Setup].Emitted);
        Assert.Equal(recovery.Id, engine.BeginRecovery(Setup, 1002).Id);
        Assert.Contains(j.State.RecoveryItems.Values, item => item.Id == Id(1) && item.OutageId == recovery.Id);
        engine.AdvanceRecovery(Setup, 1003, false);
        Assert.Equal(new[] { UserHash }, engine.Freshen(Assert.Single(j.State.Notices.Values), 1003)!.Audience);
    }

    [Theory] [InlineData(1, false)] [InlineData(1, true)] [InlineData(9, false)]
    public async Task CandidatePresentAtRecoveryCompletionIsIncludedBeforeEmission(int queuedProfiles, bool slowReports)
    {
        using var temp = new TempDirectory(); var clock = new Clock(); long now = clock.GetUtcNow().ToUnixTimeSeconds();
        string setup = Identity.Hash("fixture"); Seed(temp.Path, now, binding: true);
        using (var j = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
        {
            var engine = new Engine(j, new Model(), Identity.ServerKey("fixture"));
            var recovery = engine.BeginRecovery(setup, now - 601);
            j.Commit(new Change { Kind = "recovery", Recovery = recovery with { HasAddition = true } },
                new() { Kind = "recoveryItem", RecoveryItem = new(Id(1), recovery.Id, setup) });
        }
        bool entered = false; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new CompatFake { Libraries = [FixtureLibrary(now)] }; fake.Add(1);
        ServiceHarness? running = null; var refreshes = new ConcurrentBag<Task<bool>>();
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" }, compat: fake,
            send: async (request, ct) => {
                if (request.RequestUri!.Segments[^1] == "bindings") {
                    entered = true; await release.Task.WaitAsync(ct);
                    if (slowReports) {
                        clock.Advance(TimeSpan.FromSeconds(3));
                        refreshes.Add(running!.Service.Bind(Id(999), "profile", UserHash, default));
                    }
                }
                return ApprovalResponse(request);
            });
        running = h;
        var binds = Enumerable.Range(0, queuedProfiles).Select(i => h.Service.Bind(Id(1000 + i), "profileB" + i, UserHash, default)).ToArray();
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => entered); Assert.All(binds, task => Assert.False(task.IsCompleted));
            Assert.DoesNotContain(SentEvents(h), e => e.GetProperty("op").GetString() == "summary");
            release.SetResult();
            // Keep CPU slices representative while the transport itself advances three seconds.
            for (int tick = 0; tick < 2000 && !(binds.All(task => task.IsCompleted) && SentEvents(h).Count(e => e.GetProperty("op").GetString() == "summary") == queuedProfiles + 1); tick++)
            { clock.Advance(TimeSpan.FromMilliseconds(50)); await Task.Delay(5); }
            Assert.Equal(queuedProfiles + 1, SentEvents(h).Count(e => e.GetProperty("op").GetString() == "summary"));
            Assert.All(await Task.WhenAll(binds), approved => Assert.True(approved));
            Assert.Equal(Enumerable.Range(0, queuedProfiles).Select(i => "profileB" + i).Prepend("profile").Order(), SentEvents(h).Where(e => e.GetProperty("op").GetString() == "summary").Select(e => e.GetProperty("profile").GetString()).Order());
        }
        finally {
            release.TrySetResult(); await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4));
            await Task.WhenAll(refreshes).WaitAsync(TimeSpan.FromSeconds(4));
        }
    }

    [Fact] public async Task RejectedRefreshStopsReportingUntilAnotherExplicitBind()
    {
        using var temp = new TempDirectory(); var clock = new Clock();
        Seed(temp.Path, clock.GetUtcNow().ToUnixTimeSeconds(), binding: true);
        bool reject = false;
        using var h = new ServiceHarness(temp.Path, clock, new PluginConfiguration { Enabled = true, Secret = "fixture" },
            send: (request, _) => Task.FromResult(request.RequestUri!.Segments[^1] == "bindings" && reject
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                    currentRegistrations = RosterKeys(request, [Id(999)]), accepted = Array.Empty<string>() })) }
                : ApprovalResponse(request)));
        await h.Service.StartAsync(default);
        try
        {
            await Pump(clock, () => h.Posts.Any(p => p.Route == "bindings"));
            reject = true;
            var bind = h.Service.Bind(Id(999), "profile", UserHash, default);
            await Pump(clock, () => bind.IsCompleted); Assert.False(await bind);
            int reports = h.Posts.Count(p => p.Route == "bindings");
            for (int i = 0; i < 20; i++) { clock.Advance(TimeSpan.FromSeconds(3)); await Task.Delay(5); }
            Assert.Equal(reports, h.Posts.Count(p => p.Route == "bindings"));
            reject = false;
            var retry = h.Service.Bind(Id(999), "profile", UserHash, default);
            await Pump(clock, () => retry.IsCompleted); Assert.True(await retry);
        }
        finally { await h.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    private static string[] RosterKeys(HttpRequestMessage request, IEnumerable<string> registrations) =>
        registrations.Select(id => Identity.RegistrationRosterKey(Identity.Hash(request.Headers.Authorization!.Parameter!), id)).ToArray();

    private static HttpResponseMessage ApprovalResponse(HttpRequestMessage request)
    {
        using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var root = json.RootElement;
        request.Options.TryGetValue(new HttpRequestOptionsKey<string[]>("fixture-roster"), out var roster);
        string body = root.TryGetProperty("bindings", out var bindings) && request.RequestUri!.Segments[^1] == "bindings"
            ? JsonSerializer.Serialize(new { currentRegistrations = RosterKeys(request, roster ?? bindings.EnumerateArray().Select(b => b.GetProperty("registration").GetString()!).ToArray()),
                accepted = bindings.EnumerateArray().Select(b => b.GetProperty("registration").GetString()).ToArray() })
            : root.TryGetProperty("events", out var events)
                ? JsonSerializer.Serialize(new { accepted = events.EnumerateArray().Select(e => e.GetProperty("e").GetString()).ToArray() }) : "{}";
        return new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private static NotificationsController Controller(NotificationService service, Guid? user)
    {
        var result = new NotificationsController(service, Proxy<IServerApplicationHost>((_, _) => "fixture"));
        result.ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(user is null ? new ClaimsIdentity() : new ClaimsIdentity([new Claim("Jellyfin-UserId", user.Value.ToString("N"))], "Jellyfin")) } }; return result;
    }
    internal static async Task Until(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException("Fixture did not progress"); await Task.Delay(5); }
    }
    private static async Task Poll(Action action) => await Until(() => { try { action(); return true; } catch (LibraryPendingException) { return false; } });
    internal static async Task Pump(Clock clock, Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException("Fixture did not progress"); clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); }
    }
    internal sealed class Clock : TimeProvider
    {
        private long ticks;
        private long wall = DateTimeOffset.Parse("2026-11-03T12:00:00Z").Ticks;
        private readonly List<Timer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref wall), TimeSpan.Zero);
        internal void Jump(TimeSpan delta) => Interlocked.Add(ref wall, delta.Ticks);
        internal void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref ticks, duration.Ticks); Interlocked.Add(ref wall, duration.Ticks);
            Timer[] due;
            lock (timers) due = timers.Where(t => t.Due <= ticks && !t.Disposed).ToArray();
            foreach (var t in due) { t.Due = long.MaxValue; t.Callback(t.State); }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(callback, state, timers) { Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : GetTimestamp() + dueTime.Ticks }; lock (timers) timers.Add(timer); return timer; }
        private sealed class Timer(TimerCallback callback, object? state, List<Timer> timers) : ITimer
        {
            internal long Due; internal bool Disposed; internal readonly TimerCallback Callback = callback; internal readonly object? State = state;
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { lock (timers) { Disposed = true; timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    internal sealed class CompatFake : IJellyfinCompat
    {
        internal readonly ConcurrentDictionary<string, BaseItem> Items = [];
        internal Library[] Libraries = [];
        internal bool FailInventory;
        internal string[] DescribeFolders = [Id(8)];
        internal IReadOnlyList<Guid>? CensusIds;
        internal string? BlockedLibrary;
        internal LibraryRoot[]? Roots;
        internal string? BlockOp, BlockedId; internal ManualResetEventSlim? Block; internal int Entered, ThreadId, Returned;
        private void Wait(string op) { if (BlockOp != op) return; Interlocked.Increment(ref Entered); ThreadId = Environment.CurrentManagedThreadId; Block!.Wait(); }
        internal void Add(int id) => Items[Id(id)] = new Movie { Id = Guid.ParseExact(Id(id), "N"), Path = Path.Combine(Path.GetTempPath(), "jellysee-fixture-library", id + ".mkv") };
        public BaseItem? Get(string id) { var item = Items.GetValueOrDefault(id); if (BlockedId == id && BlockOp == "get") { Wait("get"); Interlocked.Increment(ref Returned); } return item; }
        public string? TitleType(BaseItem item) => "Movie";
        public bool IsTitle(BaseItem item) => true;
        public Title Describe(BaseItem item, long seq, int life) { Wait("describe"); return new(item.Id.ToString("N"), "Movie", DescribeFolders, null, seq, life); }
        internal string[] AdminHashes = [UserHash];
        public string[] AdminUserHashes(string serverKey) => AdminHashes;
        internal bool ReadyValue = true;
        internal readonly ConcurrentBag<string> Reads = [];
        public bool Ready(BaseItem item, long queuedAt, long now) { Reads.Add(item.Id.ToString("N")); return ReadyValue; }
        public string? Series(BaseItem item) => null;
        public string[] Users(string serverKey) => [UserHash];
        internal bool AudienceAllowed = true;
        internal int AudienceCalls;
        public string[] Audience(BaseItem? item, string[] folders, string serverKey) { Interlocked.Increment(ref AudienceCalls); return AudienceAllowed ? [UserHash] : []; }
        public Library[] Inventory() => FailInventory ? throw new IOException("private-library-path") : Libraries;
        public InventoryView ReadInventory() => new(Inventory(), Roots ?? [new(Id(8), Path.Combine(Path.GetTempPath(), "jellysee-fixture-library"))]);
        public IReadOnlyList<Guid> Ids(string libraryId) { if (BlockedLibrary is null || BlockedLibrary == libraryId) Wait("ids"); return CensusIds ?? Items.Values.Select(i => i.Id).ToArray(); }
    }
    private sealed class RecordingLog : ILogger<NotificationService>
    {
        internal readonly List<string> Messages = [];
        internal bool ThrowAfterRecording;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { Assert.Null(exception); Messages.Add(formatter(state, exception)); if (ThrowAfterRecording) throw new IOException("private-log-path"); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class ServiceHarness : IDisposable
    {
        internal readonly NotificationService Service;
        internal readonly PluginConfiguration Config;
        internal readonly CompatFake Compat;
        internal EventHandler<ItemChangeEventArgs>? Added;
        internal EventHandler<ItemChangeEventArgs>? Updated;
        internal EventHandler<ItemChangeEventArgs>? Removed;
        internal void EmitRemove(BaseItem item) => Removed?.Invoke(this, new() { Item = item });
        internal void EmitAdd(int id) => Added?.Invoke(this, new() { Item = Compat.Items[Id(id)] });
        internal void EmitUpdate(int id) => Updated?.Invoke(this, new() { Item = Compat.Items[Id(id)] });
        internal readonly RelayClient Relay;
        internal readonly ConcurrentQueue<(string Route, string Body)> Posts = new();
        internal ServiceHarness(string path, Clock clock, PluginConfiguration config, Func<(string Directory, string Root)>? paths = null,
            Func<TimeSpan, CancellationToken, Task>? retry = null, RecordingLog? logger = null,
            Func<string, string, CancellationToken, StateOwnership, Journal>? factory = null, CompatFake? compat = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
        {
            Config = config; Compat = compat ?? new CompatFake();
            Relay = new RelayClient(new Handler(async (request, ct) =>
            {
                string body = await request.Content!.ReadAsStringAsync(ct); Posts.Enqueue((request.RequestUri!.Segments[^1], body));
                using var json = JsonDocument.Parse(body);
                var submitted = json.RootElement.TryGetProperty("bindings", out var bindings)
                    ? bindings.EnumerateArray().Select(b => b.GetProperty("registration").GetString()!).ToArray() : [];
                var roster = (Service?.Snapshot.Bindings.Select(b => b.Registration) ?? []).Concat(submitted).Distinct().Take(500).ToArray();
                request.Options.Set(new HttpRequestOptionsKey<string[]>("fixture-roster"), roster);
                if (send is not null) return await send(request, ct);
                return ApprovalResponse(request);
            }));
            Service = new NotificationService(Proxy<ILibraryManager>((method, args) =>
            {
                if (method.Name == "add_ItemAdded") Added += (EventHandler<ItemChangeEventArgs>)args![0]!;
                if (method.Name == "remove_ItemAdded") Added -= (EventHandler<ItemChangeEventArgs>)args![0]!;
                if (method.Name == "add_ItemRemoved") Removed += (EventHandler<ItemChangeEventArgs>)args![0]!;
                if (method.Name == "remove_ItemRemoved") Removed -= (EventHandler<ItemChangeEventArgs>)args![0]!;
                if (method.Name == "add_ItemUpdated") Updated += (EventHandler<ItemChangeEventArgs>)args![0]!;
                if (method.Name == "remove_ItemUpdated") Updated -= (EventHandler<ItemChangeEventArgs>)args![0]!;
                return null;
            }), Compat, Proxy<IServerApplicationHost>((_, _) => "fixture"), Relay, logger ?? new RecordingLog(),
                () => config, paths ?? (() => (path, Path.GetDirectoryName(path)!)), TimeSpan.FromSeconds(3), retry, clock, openJournal: factory);
        }
        public void Dispose() { Service.Dispose(); Assert.True(Service.DiagnosticsStopped.Wait(TimeSpan.FromSeconds(3))); Relay.Dispose(); }
    }
    public class Dispatch : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    { var value = DispatchProxy.Create<T, Dispatch>(); ((Dispatch)(object)value).Call = call; return value; }
}
