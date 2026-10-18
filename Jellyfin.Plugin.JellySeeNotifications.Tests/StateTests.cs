using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using Xunit;
using static Jellyfin.Plugin.JellySeeNotifications.Tests.ProtocolTests;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "TestResults", Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}

public sealed class StateTests
{
    [Theory]
    [InlineData("Movie", false, true, 500, false)]
    [InlineData("Movie", true, false, 500, false)]
    [InlineData("Movie", true, true, 500, true)]
    [InlineData("Series", false, false, 900, true)]
    [InlineData("Episode", true, true, 0, true)]
    [InlineData("MusicVideo", false, false, 899, false)]
    [InlineData("Book", false, true, 10, true)]
    [InlineData("AudioBook", false, true, 10, true)]
    [InlineData("AudioBook", false, false, 900, true)]
    public void ReadinessMatchesEachType(string type, bool providers, bool refreshed, long age, bool expected) => Assert.Equal(expected, Rules.Ready(type, providers, refreshed, 100, 100 + age));
    [Fact] public void FreshnessAndAudienceDeadlineIncludeFutureClockSkew()
    {
        Assert.True(Rules.Stale(0, 2000)); Assert.True(Rules.Stale(2121, 2000)); Assert.False(Rules.Stale(800, 2000));
        Assert.True(Rules.AudienceExpired(Event() with { QueuedAt = 100 }, 1900));
        Assert.False(Rules.AudienceExpired(Event() with { QueuedAt = 100 }, 1899));
        Assert.True(Rules.Expired(Event(), 86500));
    }
    [Fact] public void UpgradeRequiresNonemptyDigestAndExactlySameFoldersAndType()
    {
        var rm = Event() with { Op = "rm", Folders = [Id(8), Id(9)], ProviderDigest = "1234567890abcdef", QueuedAt = 1000 };
        var add = new Addition(Id(2), "Movie", [Id(9), Id(8)], rm.ProviderDigest, 1600);
        Assert.True(Rules.Upgrade(rm, add));
        Assert.False(Rules.Upgrade(rm, add with { At = 1601 }));
        Assert.False(Rules.Upgrade(rm with { ProviderDigest = null }, add with { ProviderDigest = null }));
        Assert.False(Rules.Upgrade(rm, add with { Folders = [Id(8)] }));
        Assert.False(Rules.Upgrade(rm, add with { Type = "Book" }));
    }
    [Theory]
    [InlineData("pendingAdd")][InlineData("outbox")]
    public void CrashRecoveryKeepsOccurrenceId(string stage)
    {
        using var temp = new TempDirectory();
        var e = Event() with { Stage = stage };
        using (var journal = new Journal(temp.Path)) journal.Commit(new Change { Kind = "notice", Notice = e });
        using (var journal = new Journal(temp.Path))
        {
            Assert.Equal(JsonSerializer.Serialize(e), JsonSerializer.Serialize(Assert.Single(journal.State.Notices).Value));
            journal.Commit(new Change { Kind = "retire", Key = e.EventId });
        }
        using var recovered = new Journal(temp.Path); Assert.Empty(recovered.State.Notices);
    }
    [Fact] public void SnapshotRenameAndJournalReplayAreIdempotent()
    {
        using var temp = new TempDirectory();
        string journalPath = System.IO.Path.Combine(temp.Path, "journal.ndjson"); byte[] before;
        using (var journal = new Journal(temp.Path))
        {
            journal.Commit(new Change { Kind = "notice", Notice = Event() }); before = File.ReadAllBytes(journalPath);
            journal.Compact();
        }
        // Crash after snapshot rename, before log truncation.
        File.WriteAllBytes(journalPath, before);
        using var restored = new Journal(temp.Path);
        Assert.Single(restored.State.Notices); Assert.Equal(1, restored.State.Seq);
    }
    [Fact] public void CorruptTailPreservesDurablePrefix()
    {
        using var temp = new TempDirectory(); long good;
        using (var journal = new Journal(temp.Path)) { journal.Commit(new Change { Kind = "notice", Notice = Event() }); }
        string path = System.IO.Path.Combine(temp.Path, "journal.ndjson"); good = new FileInfo(path).Length;
        File.AppendAllText(path, "{\"Payload\":\"crash midway");
        using var recovered = new Journal(temp.Path);
        Assert.True(recovered.RecoveredCorruption); Assert.Single(recovered.State.Notices); Assert.Equal(good, new FileInfo(path).Length);
    }
    [Fact] public void CorruptSnapshotIsMovedAsideAndNeverPartiallyApplied()
    {
        using var temp = new TempDirectory();
        using (var journal = new Journal(temp.Path)) { journal.Commit(new Change { Kind = "notice", Notice = Event() }); journal.Compact(); }
        string snapshot = System.IO.Path.Combine(temp.Path, "snapshot.bin");
        var bytes = File.ReadAllBytes(snapshot); File.WriteAllBytes(snapshot, bytes[..^41]);
        using var recovered = new Journal(temp.Path);
        Assert.True(recovered.RecoveredCorruption); Assert.Empty(recovered.State.Notices); Assert.True(File.Exists(snapshot + ".corrupt"));
    }
    [Fact] public void HostileRecordIsBoundedAndSemanticTransactionIsAtomic()
    {
        using var temp = new TempDirectory();
        using (var journal = new Journal(temp.Path)) journal.Commit(new Change { Kind = "notice", Notice = Event() });
        string path = System.IO.Path.Combine(temp.Path, "journal.ndjson");
        string payload = JsonSerializer.Serialize(new { Seq = 2, Changes = new object[] { new { Kind = "retire", Key = Id(1) }, new { Kind = "unknown" } } });
        File.AppendAllText(path, JsonSerializer.Serialize(new { Payload = payload, Sha256 = Identity.Hash(payload) }) + "\n");
        using (var recovered = new Journal(temp.Path)) { Assert.Single(recovered.State.Notices); Assert.Equal(1, recovered.State.Seq); }
        File.AppendAllText(path, new string('x', Journal.MaxRecord + 1) + "\n");
        using var bounded = new Journal(temp.Path); Assert.Single(bounded.State.Notices); Assert.True(bounded.RecoveredCorruption);
    }
    [Fact] public void LinkedFilesCannotEscapePluginDirectory()
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        string target = System.IO.Path.Combine(outside.Path, "untouched"); File.WriteAllText(target, "outside");
        File.CreateSymbolicLink(System.IO.Path.Combine(temp.Path, "journal.ndjson"), target);
        Assert.Throws<IOException>(() => new Journal(temp.Path)); Assert.Equal("outside", File.ReadAllText(target));
    }
    [Fact] public void TombstonePreventsStaleBaselineResurrectionAndSurvivesCompaction()
    {
        using var temp = new TempDirectory();
        using (var journal = new Journal(temp.Path))
        {
            journal.Commit(new Change { Kind = "remove", Tombstone = new(Id(1), 20, 1000, 1) });
            journal.Commit(new Change { Kind = "title", Title = new(Id(1), "Book", [Id(8)], null, 10, 0) });
            Assert.Empty(journal.State.Index); journal.Compact();
        }
        using var recovered = new Journal(temp.Path); Assert.Empty(recovered.State.Index); Assert.Equal(1, recovered.State.Tombstones[Id(1)].Life);
    }
    [Fact] public void InventoryRequiresTwoEmptyPollsAndUniqueRenamePair()
    {
        var old = Lib(1, "old", ["path"]);
        var first = InventoryRules.Evaluate([old], [], true, 0); Assert.False(first.Complete); Assert.Equal(1, first.EmptyPolls);
        var second = InventoryRules.Evaluate([old], [], true, first.EmptyPolls); Assert.Equal(old, Assert.Single(second.Removed));
        var renamed = InventoryRules.Evaluate([old], [Lib(2, "new", ["path"]) with { Id = "" }], true, 0);
        Assert.Empty(renamed.Removed); Assert.Equal(old.Id, Assert.Single(renamed.Current).Id);
        Assert.False(InventoryRules.Evaluate([old], [], false, 0).Complete);
    }
    [Fact] public void SharedPathsDoNotHideLibraryDeletionOrCreateAmbiguousRename()
    {
        var a = Lib(1, "a", ["path"]); var b = Lib(2, "b", ["path"]);
        Assert.Equal(a, Assert.Single(InventoryRules.Evaluate([a, b], [b], true, 0).Removed));
        Assert.Equal(2, InventoryRules.Evaluate([a, b], [Lib(3, "c", ["path"])], true, 0).Removed.Length);
        var pathChange = InventoryRules.Evaluate([a], [a with { Locations = [Identity.Hash("other")], Enabled = false }], true, 0);
        Assert.Empty(pathChange.Removed); Assert.False(Assert.Single(pathChange.Current).Enabled);
    }
    [Fact] public void HeartbeatAndJournalExcludeMetadataAndSecret()
    {
        var state = new State(); state.Apply(new() { Kind = "notice", Notice = Event() });
        using var json = JsonDocument.Parse(Heartbeat.Encode("server", state, 1000, "none", false, 100));
        Assert.Equal(new[] { "baselineLibrariesRemaining", "droppedSinceLast", "errorCode", "indexCount", "indexFull", "lastAckAt", "oldestPendingAgeS", "outboxDepth", "server", "stateBytes", "tooManyUsers", "v", "version" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        using var temp = new TempDirectory();
        using var journal = new Journal(temp.Path); journal.Commit(new Change { Kind = "notice", Notice = Event() }); journal.Compact();
        string disk = File.ReadAllText(System.IO.Path.Combine(temp.Path, "snapshot.bin"));
        foreach (string forbidden in new[] { "Secret", "Username", "Path", "Name", "Rating", "Tags" }) Assert.DoesNotContain(forbidden, disk);
        Assert.Throws<InvalidDataException>(() => Heartbeat.Encode("server", state, 1000, "arbitrary secret", false, 100));
    }
    [Fact] public void RotationKeepsOldCredentialsOutOfEventStateAndExpiresThem()
    {
        var old = new PluginConfiguration { Secret = "old-secret", Enabled = true };
        var next = new PluginConfiguration { Secret = "new-secret", RetiredSecrets = Credentials.Rotate(old, "new-secret", 1000), Enabled = true };
        Assert.Equal("old-secret", Credentials.Available(next, 1001)[Identity.Hash("old-secret")]);
        Assert.Equal("new-secret", Credentials.Available(next, 1001)[Identity.Hash("new-secret")]);
        Assert.False(Credentials.Available(next, 87400).ContainsKey(Identity.Hash("old-secret")));
        var notice = Event() with { SetupHash = Identity.Hash("old-secret") };
        Assert.DoesNotContain("old-secret", JsonSerializer.Serialize(notice));
        var rotatedBack = Credentials.Rotate(next, "old-secret", 2000);
        Assert.DoesNotContain(rotatedBack, c => c.Secret == "old-secret"); Assert.Contains(rotatedBack, c => c.Secret == "new-secret");
    }
    [Fact] public void LibraryRenameMigratesIndexRoutesAndLibraryRemovalPreservesSharedRoute()
    {
        var state = new State(); state.Apply(new() { Kind = "title", Title = new(Id(1), "Book", [Id(8), Id(9)], null, 1, 0) });
        state.Transaction([new() { Kind = "reroute", Key = Id(8) + ":" + Id(10) }]);
        Assert.Equal(new[] { Id(9), Id(10) }, state.Index[Id(1)].Folders);
        state.Transaction([new() { Kind = "unroute", Key = Id(10) }]); Assert.Equal(new[] { Id(9) }, state.Index[Id(1)].Folders);
        state.Transaction([new() { Kind = "unroute", Key = Id(9) }]); Assert.Empty(state.Index);
    }
    private static Library Lib(int id, string dir, string[] paths) => new(Identity.Hash(dir), Id(id), paths.Select(Identity.Hash).ToArray(), true, true);
}
