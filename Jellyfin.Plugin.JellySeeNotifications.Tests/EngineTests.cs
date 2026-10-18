using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;
using static Jellyfin.Plugin.JellySeeNotifications.Tests.ProtocolTests;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class EngineTests
{
    private sealed class FakeCompat : IJellyfinCompat
    {
        public Dictionary<string, BaseItem> Items { get; } = [];
        public bool HasAudience { get; set; } = true;
        public bool Refreshed { get; set; } = true;
        public bool Overflow { get; set; }
        public string[] Folders { get; set; } = [Id(8)];
        public BaseItem? Get(string id) => Items.GetValueOrDefault(id);
        public string? TitleType(BaseItem item) => "Movie";
        public bool IsTitle(BaseItem item) => true;
        public Title Describe(BaseItem item, long seq, int life) => new(item.Id.ToString("N"), "Movie", Folders, "1234567890abcdef", seq, life);
        public bool Ready(BaseItem item, long queuedAt, long now) => Rules.Ready("Movie", true, Refreshed, queuedAt, now);
        public string? Series(BaseItem item) => null;
        public string[] Users(string key) => Overflow ? throw new TooManyUsersException() : ["1234567890abcdef"];
        public string[] Audience(BaseItem? item, string[] folders, string key) => Overflow ? throw new TooManyUsersException() : HasAudience ? ["1234567890abcdef"] : [];
        public Library[] Inventory() => [];
        public IReadOnlyList<Guid> Ids(string id) => Items.Keys.Select(k => Guid.ParseExact(k, "N")).ToArray();
        public void Add(int id) => Items[Id(id)] = new Movie { Id = Guid.ParseExact(Id(id), "N") };
    }
    [Fact] public void ReadinessAudienceRetryAndRestartKeepId()
    {
        using var temp = new TempDirectory(); var fake = new FakeCompat { Refreshed = false }; fake.Add(1);
        using (var journal = new Journal(temp.Path))
        {
            var engine = new Engine(journal, fake, "key"); engine.QueueAdd(Id(1), "Movie", Id(100), 100, "");
            engine.AdvanceAdds(200, _ => { }); Assert.Equal("pendingAdd", journal.State.Notices[Id(100)].Stage);
        }
        using var recovered = new Journal(temp.Path); var restored = new Engine(recovered, fake, "key");
        fake.Refreshed = true; fake.HasAudience = false; restored.AdvanceAdds(400, _ => { });
        Assert.Equal("pendingAdd", recovered.State.Notices[Id(100)].Stage);
        fake.HasAudience = true; restored.AdvanceAdds(500, _ => { });
        var notice = Assert.Single(recovered.State.Notices).Value; Assert.Equal(Id(100), notice.EventId); Assert.Equal("outbox", notice.Stage);
        Assert.Equal(500, notice.At); Assert.Single(recovered.State.Index);
    }
    [Fact] public void AudienceTimeoutDropsAndReportsInsteadOfSending()
    {
        using var temp = new TempDirectory(); using var journal = new Journal(temp.Path);
        var fake = new FakeCompat { HasAudience = false }; fake.Add(1); var engine = new Engine(journal, fake, "key");
        engine.QueueAdd(Id(1), "Movie", Id(100), 100, ""); engine.AdvanceAdds(1900, _ => { });
        Assert.Empty(journal.State.Notices); Assert.Equal(1, journal.State.Health.Dropped);
    }
    [Fact] public void UpgradeSuppressesOnlyRemovalAndStillAdvancesLifecycle()
    {
        using var temp = new TempDirectory(); using var journal = new Journal(temp.Path);
        var fake = new FakeCompat(); fake.Add(1); var engine = new Engine(journal, fake, "key");
        engine.Index(fake.Get(Id(1))!); engine.Remove(Id(1), Id(100), 1000, "");
        engine.QueueAdd(Id(1), "Movie", Id(101), 1001, ""); engine.AdvanceAdds(1002, _ => { }); engine.AdvanceRemovals(1600, _ => { });
        Assert.DoesNotContain(journal.State.Notices.Values, n => n.Op == "rm");
        var add = Assert.Single(journal.State.Notices).Value; Assert.Equal("add", add.Op); Assert.Equal(1, add.Life);
        Assert.Equal(1, journal.State.Index[Id(1)].Life);
    }
    [Fact] public void UnreadyReplacementSuppressesRemovalBeforeTheFifteenMinuteFallbackAcrossRestart()
    {
        using var temp = new TempDirectory(); var fake = new FakeCompat { Refreshed = false }; fake.Add(1); fake.Add(2);
        using (var journal = new Journal(temp.Path))
        {
            var engine = new Engine(journal, fake, "key"); engine.Index(fake.Get(Id(1))!);
            engine.Remove(Id(1), Id(100), 1000, "");
            engine.QueueAdd(Id(2), "Movie", Id(101), 1001, ""); engine.AdvanceAdds(1002, _ => { });
            Assert.Equal("pendingAdd", journal.State.Notices[Id(101)].Stage);
            Assert.Equal(1001, journal.State.Additions[Id(2)].At);
            // History is separately bounded and may evict this still-pending replacement.
            journal.Commit(new Change { Kind = "unaddition", Key = Id(2) }); journal.Compact();
        }
        using var restored = new Journal(temp.Path); var resumed = new Engine(restored, fake, "key");
        resumed.AdvanceRemovals(1600, _ => { });
        Assert.DoesNotContain(restored.State.Notices.Values, n => n.Op == "rm");
        Assert.Equal("pendingAdd", Assert.Single(restored.State.Notices.Values).Stage);
        resumed.AdvanceAdds(1901, _ => { });
        var add = Assert.Single(restored.State.Notices.Values); Assert.Equal("add", add.Op); Assert.Equal("outbox", add.Stage); Assert.Equal(Id(101), add.EventId);
        Assert.Equal(1, resumed.Life(Id(1)));
    }
    [Fact] public void UnreadyReplacementInAnotherLibraryDoesNotSuppressRemoval()
    {
        using var temp = new TempDirectory(); using var journal = new Journal(temp.Path);
        var fake = new FakeCompat { Refreshed = false }; fake.Add(1); fake.Add(2); var engine = new Engine(journal, fake, "key");
        engine.Index(fake.Get(Id(1))!); engine.Remove(Id(1), Id(100), 1000, ""); fake.Folders = [Id(9)];
        engine.QueueAdd(Id(2), "Movie", Id(101), 1001, ""); engine.AdvanceAdds(1002, _ => { }); engine.AdvanceRemovals(1600, _ => { });
        Assert.Contains(journal.State.Notices.Values, n => n.Op == "rm" && n.Stage == "outbox");
        Assert.Contains(journal.State.Notices.Values, n => n.Op == "add" && n.Stage == "pendingAdd");
    }
    [Fact] public void MoveBetweenLibrariesDoesNotSuppressRemoval()
    {
        using var temp = new TempDirectory(); using var journal = new Journal(temp.Path);
        var fake = new FakeCompat(); fake.Add(1); var engine = new Engine(journal, fake, "key");
        engine.Index(fake.Get(Id(1))!); engine.Remove(Id(1), Id(100), 1000, "");
        fake.Folders = [Id(9)]; engine.QueueAdd(Id(1), "Movie", Id(101), 1001, ""); engine.AdvanceAdds(1002, _ => { }); engine.AdvanceRemovals(1600, _ => { });
        Assert.Equal(2, journal.State.Notices.Count); Assert.Contains(journal.State.Notices.Values, n => n.Op == "rm" && n.Stage == "outbox");
    }
    [Fact] public void MoreThanTwoHundredRemovalsProduceOneBulkPerLibraryAndRecoverAtomically()
    {
        using var temp = new TempDirectory();
        using (var journal = new Journal(temp.Path))
        {
            var fake = new FakeCompat(); var engine = new Engine(journal, fake, "key");
            foreach (int i in Enumerable.Range(1, 250)) engine.Put(Event(i) with { Id = Id(1000 + i), Op = "rm", Stage = "pendingRemove", Folders = [Id(8), Id(9)], QueuedAt = 1000 });
            engine.AdvanceRemovals(1600, _ => { });
            Assert.Equal(2, journal.State.Notices.Count);
            Assert.All(journal.State.Notices.Values, n => { Assert.Equal("rmbulk", n.Op); Assert.Equal(250, n.Count); }); journal.Compact();
        }
        using var restored = new Journal(temp.Path); Assert.Equal(2, restored.State.Notices.Count); Assert.All(restored.State.Notices.Values, n => Assert.Equal(250, n.Count));
    }
    [Fact] public void ExactlyTwoHundredDoNotBulk()
    {
        var events = Enumerable.Range(1, 200).Select(i => Event(i) with { Op = "rm", Folders = [Id(8)] }).ToArray();
        Assert.False(Rules.Bulk(events, Id(8))); Assert.True(Rules.Bulk(events.Append(Event(201) with { Op = "rm", Folders = [Id(8)] }), Id(8)));
    }
    [Fact] public void RetryReevaluatesAudienceWithoutChangingEventId()
    {
        using var temp = new TempDirectory(); using var journal = new Journal(temp.Path);
        var fake = new FakeCompat(); fake.Add(101); var engine = new Engine(journal, fake, "key"); var original = Event() with { QueuedAt = 2000 }; engine.Put(original);
        var fresh = engine.Freshen(original, 2000)!; Assert.Equal(2000, fresh.At); Assert.Equal(Id(1), fresh.EventId); Assert.NotEmpty(fresh.Audience);
        fake.HasAudience = false; Assert.Null(engine.Freshen(fresh, 3300)); Assert.True(journal.State.Notices.ContainsKey(Id(1)));
        Assert.Null(engine.Freshen(fresh, 4000)); Assert.Empty(journal.State.Notices);
    }
    [Fact] public void IndexCapStopsNewIndexEntriesAndRetainsLiveAdds()
    {
        using var temp = new TempDirectory(); using var journal = new Journal(temp.Path); var fake = new FakeCompat();
        foreach (int i in Enumerable.Range(1, State.IndexCapacity)) journal.State.Apply(new() { Kind = "title", Title = new(Id(i), "Movie", [Id(8)], null, 1, 0) });
        fake.Add(State.IndexCapacity + 1); var engine = new Engine(journal, fake, "key");
        engine.QueueAdd(Id(State.IndexCapacity + 1), "Movie", Id(999999), 100, ""); engine.AdvanceAdds(200, _ => { });
        Assert.Equal(State.IndexCapacity, journal.State.Index.Count); Assert.True(journal.State.Health.IndexFull); Assert.Single(journal.State.Notices);
        Assert.Equal("outbox", Assert.Single(journal.State.Notices.Values).Stage);
    }
    [Fact] public void OldOutboxOccurrenceCannotHideRecoveredReadditionAndPendingRetirementIsAtomic()
    {
        using var temp = new TempDirectory(); var fake = new FakeCompat(); fake.Add(1);
        using (var journal = new Journal(temp.Path))
        {
            var engine = new Engine(journal, fake, "key"); engine.QueueAdd(Id(1), "Movie", Id(100), 100, ""); engine.AdvanceAdds(101, _ => { });
            engine.Remove(Id(1), Id(101), 200, "");
            Assert.DoesNotContain(Id(1), journal.State.DiscoveryKnowledge());
            engine.QueueAdd(Id(1), "Movie", Id(102), 201, "");
            engine.Remove(Id(1), Id(103), 202, ""); Assert.Equal(2, engine.Life(Id(1)));
            Assert.DoesNotContain(journal.State.Notices.Values, n => n.Stage == "pendingAdd");
        }
        using var restored = new Journal(temp.Path); Assert.Equal(2, restored.State.Lifecycles[Id(1)]);
        Assert.DoesNotContain(restored.State.Notices.Values, n => n.Stage == "pendingAdd");
    }
    [Fact] public void LifecycleSurvivesExpiredTombstoneAndRestart()
    {
        using var temp = new TempDirectory(); var fake = new FakeCompat(); fake.Add(1);
        using (var journal = new Journal(temp.Path))
        {
            var engine = new Engine(journal, fake, "key"); engine.Index(fake.Get(Id(1))!); engine.Remove(Id(1), Id(100), 100, "");
            engine.Sweep(86500); Assert.Empty(journal.State.Tombstones); Assert.Equal(1, engine.Life(Id(1))); journal.Compact();
        }
        using var restored = new Journal(temp.Path); var resumed = new Engine(restored, fake, "key");
        resumed.QueueAdd(Id(1), "Movie", Id(101), 90000, ""); resumed.AdvanceAdds(90001, _ => { });
        Assert.Equal(1, Assert.Single(restored.State.Notices.Values).Life);
        resumed.Remove(Id(1), Id(102), 90100, ""); Assert.Equal(2, resumed.Life(Id(1)));
    }
    [Theory]
    [InlineData("pendingAdd")][InlineData("outbox")]
    public void OverflowDropsOldestWithinItsOwnQueueAndReports(string stage)
    {
        using var temp = new TempDirectory(); using var journal = new Journal(temp.Path);
        foreach (int i in Enumerable.Range(1, State.Capacity)) journal.State.Apply(new() { Kind = "notice", Notice = Event(i) with { Stage = stage, QueuedAt = i } });
        var engine = new Engine(journal, new FakeCompat(), "key"); engine.Put(Event(99999) with { Stage = stage, QueuedAt = 99999 });
        Assert.Equal(State.Capacity, journal.State.Notices.Count); Assert.False(journal.State.Notices.ContainsKey(Id(1)));
        Assert.True(journal.State.Notices.ContainsKey(Id(99999))); Assert.Equal(1, journal.State.Health.Dropped);
    }
    [Fact] public void BaselineBatchesAndUpdatedFolderMappingSurviveCompactSnapshot()
    {
        using var temp = new TempDirectory(); var fake = new FakeCompat();
        foreach (int i in Enumerable.Range(1, 10001)) fake.Add(i);
        using (var journal = new Journal(temp.Path))
        {
            var engine = new Engine(journal, fake, "key"); engine.IndexMany(fake.Items.Values, journal.State.Seq);
            Assert.Equal(10001, journal.State.Index.Count); Assert.InRange(journal.State.Seq, 1, 101);
            fake.Folders = [Id(9)]; engine.Index(fake.Get(Id(1))!); journal.Compact();
            Assert.InRange(new FileInfo(System.IO.Path.Combine(temp.Path, "snapshot.bin")).Length, 1, 10001L * 104 + 1024);
        }
        using var restored = new Journal(temp.Path); Assert.Equal(10001, restored.State.Index.Count); Assert.Equal(new[] { Id(9) }, restored.State.Index[Id(1)].Folders);
    }
    [Fact] public void DirtyPassCannotReinsertNewerTombstone()
    {
        using var temp = new TempDirectory(); using var journal = new Journal(temp.Path); var fake = new FakeCompat(); fake.Add(1);
        var engine = new Engine(journal, fake, "key"); engine.Index(fake.Get(Id(1))!); long start = journal.State.Seq;
        engine.Remove(Id(1), Id(100), 100, ""); engine.Index(fake.Get(Id(1))!, start); Assert.Empty(journal.State.Index);
        engine.Index(fake.Get(Id(1))!); Assert.Single(journal.State.Index);
    }
}
