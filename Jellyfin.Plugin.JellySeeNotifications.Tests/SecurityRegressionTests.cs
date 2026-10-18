using System.Buffers.Binary;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using Xunit;
using static Jellyfin.Plugin.JellySeeNotifications.Tests.ProtocolTests;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class SecurityRegressionTests
{
    [Theory]
    [InlineData("snapshot.tmp", false)]
    [InlineData("snapshot.bin", false)]
    [InlineData("journal.ndjson", false)]
    [InlineData("snapshot.tmp", true)]
    [InlineData("snapshot.bin", true)]
    [InlineData("journal.ndjson", true)]
    public void PostStartupLinksCannotRedirectCompaction(string name, bool hardlink)
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        string target = Path.Combine(outside.Path, "outside"); File.WriteAllText(target, "unchanged");
        using var journal = new Journal(temp.Path);
        journal.Commit(new Change { Kind = "notice", Notice = Event() });
        string path = Path.Combine(temp.Path, name); File.Delete(path);
        if (hardlink) CreateHardLink(path, target); else File.CreateSymbolicLink(path, target);
        journal.Compact();
        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.Equal(1, journal.State.Seq);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(temp.Path, "snapshot.bin")));
    }
    [Theory]
    [InlineData("journal.ndjson")]
    [InlineData("snapshot.bin")]
    public void ExistingHardLinksAreCopiedBeforeAnyWrite(string name)
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        string target = Path.Combine(outside.Path, "outside"); File.WriteAllText(target, "unchanged");
        string path = Path.Combine(temp.Path, name);
        CreateHardLink(path, target);
        using (var journal = new Journal(temp.Path))
        {
            journal.Commit(new Change { Kind = "notice", Notice = Event() });
            journal.Compact();
        }
        using var recovered = new Journal(temp.Path);
        Assert.Single(recovered.State.Notices);
        Assert.Equal("unchanged", File.ReadAllText(target));
    }
    [Fact] public void ReplacedJournalLinkFailsBeforeAppendAndLeavesTargetUnchanged()
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        using var journal = new Journal(temp.Path);
        string target = Path.Combine(outside.Path, "outside"); File.WriteAllText(target, "unchanged");
        string path = Path.Combine(temp.Path, "journal.ndjson"); File.Delete(path); File.CreateSymbolicLink(path, target);
        Assert.Throws<StatePersistenceException>(() => journal.Commit(new Change { Kind = "notice", Notice = Event() }));
        Assert.Equal("unchanged", File.ReadAllText(target)); Assert.Empty(journal.State.Notices);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuarantineDestinationLinkDoesNotTruncateOutsideFile(bool hardlink)
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        string target = Path.Combine(outside.Path, "outside"); File.WriteAllText(target, "unchanged");
        File.WriteAllText(Path.Combine(temp.Path, "snapshot.bin"), "corrupt");
        string quarantine = Path.Combine(temp.Path, "snapshot.bin.corrupt");
        if (hardlink) CreateHardLink(quarantine, target); else File.CreateSymbolicLink(quarantine, target);
        using var recovered = new Journal(temp.Path);
        Assert.True(recovered.RecoveredCorruption); Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.Equal("corrupt", File.ReadAllText(Path.Combine(temp.Path, "snapshot.bin.corrupt")));
    }
    [Fact] public void DirectoryReplacementCannotRedirectLaterWrites()
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        string moved = temp.Path + ".held";
        try
        {
            using (var journal = new Journal(temp.Path))
            {
                Directory.Move(temp.Path, moved); Directory.CreateSymbolicLink(temp.Path, outside.Path);
                Assert.Throws<StatePersistenceException>(() => journal.Commit(new Change { Kind = "notice", Notice = Event() }));
                Assert.Throws<StatePersistenceException>(() => journal.Compact());
                Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
            }
            using var recovered = new Journal(moved); Assert.Empty(recovered.State.Notices);
        }
        finally { Directory.Delete(temp.Path); Directory.Move(moved, temp.Path); }
    }
    [Theory]
    [InlineData("snapshot.bin", false)]
    [InlineData("snapshot.bin", true)]
    [InlineData("journal.ndjson", false)]
    [InlineData("journal.ndjson", true)]
    public void ExistingLinksAreRejectedBeforeRead(string name, bool dangling)
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        string target = Path.Combine(outside.Path, "outside");
        if (!dangling) File.WriteAllText(target, "unchanged");
        File.CreateSymbolicLink(Path.Combine(temp.Path, name), target);
        Assert.Throws<IOException>(() => new Journal(temp.Path));
        if (!dangling) Assert.Equal("unchanged", File.ReadAllText(target));
        else Assert.False(File.Exists(target));
    }
    [Fact] public void StateDirectoryMustStayInsideTrustedPluginDataPath()
    {
        using var root = new TempDirectory(); using var outside = new TempDirectory();
        Assert.Throws<IOException>(() => new Journal(outside.Path, root.Path));
        Assert.Throws<IOException>(() => new Journal(root.Path, root.Path));
        string escaped = Path.Combine(root.Path, "..", Path.GetFileName(outside.Path));
        Assert.Throws<IOException>(() => new Journal(escaped, root.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedDirectoryOrAncestorIsRejected(bool ancestor)
    {
        using var root = new TempDirectory(); using var outside = new TempDirectory();
        string link = Path.Combine(root.Path, "linked"); Directory.CreateSymbolicLink(link, outside.Path);
        string directory = ancestor ? Path.Combine(link, "state") : link;
        Assert.Throws<IOException>(() => new Journal(directory, root.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }
    [Fact] public void TrustedRootSubstitutionStopsFurtherWrites()
    {
        using var root = new TempDirectory(); using var outside = new TempDirectory();
        string moved = root.Path + ".held";
        try
        {
            using (var journal = new Journal(Path.Combine(root.Path, "state"), root.Path))
            {
                Directory.Move(root.Path, moved); Directory.CreateSymbolicLink(root.Path, outside.Path);
                Assert.Throws<StatePersistenceException>(() => journal.Commit(new Change { Kind = "notice", Notice = Event() }));
                Assert.Throws<StatePersistenceException>(() => journal.Compact());
                Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
            }
        }
        finally { Directory.Delete(root.Path); Directory.Move(moved, root.Path); }
    }
    [Fact] public void FailedCompactionRemovesItsTemporarySnapshot()
    {
        using var temp = new TempDirectory();
        using var journal = new Journal(temp.Path);
        journal.Commit(new Change { Kind = "notice", Notice = Event() });
        Directory.CreateDirectory(Path.Combine(temp.Path, "snapshot.bin"));
        for (int i = 0; i < 3; i++)
        {
            Assert.Throws<StatePersistenceException>(() => journal.Compact());
            Assert.Empty(Directory.EnumerateFiles(temp.Path, "snapshot.tmp.*"));
        }
        Directory.Delete(Path.Combine(temp.Path, "snapshot.bin"));
        journal.Compact();
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(temp.Path).Count());
    }
    [Theory]
    [InlineData("journal.ndjson")]
    [InlineData("snapshot.bin.corrupt")]
    public void FailedRewriteRemovesItsTemporaryFile(string target)
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, target));
        using var files = new StateDirectory(temp.Path, Path.GetDirectoryName(temp.Path)!);
        Assert.ThrowsAny<IOException>(() => files.Rewrite(target, null, 0));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp.*"));
    }
    [Fact] public void CopyFailureRemovesItsTemporaryFile()
    {
        using var temp = new TempDirectory();
        using var files = new StateDirectory(temp.Path, Path.GetDirectoryName(temp.Path)!);
        using var source = files.Create("source").Stream;
        source.WriteByte(1);
        Assert.Throws<IOException>(() => files.Rewrite("journal.ndjson", source, 2));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "journal.ndjson.tmp.*"));
        Assert.False(File.Exists(Path.Combine(temp.Path, "journal.ndjson")));
    }
    [Fact] public void StartupNeverRecursesIntoTemporaryDirectories()
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        string name = "snapshot.tmp." + Guid.NewGuid().ToString("N");
        Directory.CreateSymbolicLink(Path.Combine(temp.Path, name), outside.Path);
        string target = Path.Combine(outside.Path, "keep"); File.WriteAllText(target, "unchanged");
        Assert.Throws<IOException>(() => new Journal(temp.Path));
        Assert.Equal("unchanged", File.ReadAllText(target));
    }
    [Fact] public void StartupReclaimsOnlyOwnedRandomTemporaryEntriesWithoutFollowingLinks()
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        string target = Path.Combine(outside.Path, "outside"); File.WriteAllText(target, "unchanged");
        string[] stale = ["snapshot.tmp." + Guid.NewGuid().ToString("N"), "journal.ndjson.tmp." + Guid.NewGuid().ToString("N"), "snapshot.bin.corrupt.tmp." + Guid.NewGuid().ToString("N")];
        File.WriteAllText(Path.Combine(temp.Path, stale[0]), "abandoned");
        File.CreateSymbolicLink(Path.Combine(temp.Path, stale[1]), target);
        CreateHardLink(Path.Combine(temp.Path, stale[2]), target);
        File.WriteAllText(Path.Combine(temp.Path, "snapshot.tmp.keep"), "keep");
        using var journal = new Journal(temp.Path);
        Assert.All(stale, name => Assert.False(File.Exists(Path.Combine(temp.Path, name))));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(temp.Path, "snapshot.tmp.keep")));
        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.Equal(new FileInfo(Path.Combine(temp.Path, "journal.ndjson")).Length, journal.DiskBytes);
    }
    [Fact] public void StartupRecoveryIsBoundedAndCanResumeOnNextStartup()
    {
        using var temp = new TempDirectory();
        for (int i = 0; i < 130; i++) File.WriteAllText(Path.Combine(temp.Path, "snapshot.tmp." + Guid.NewGuid().ToString("N")), "stale");
        Assert.Throws<IOException>(() => new Journal(temp.Path));
        Assert.Equal(2, Directory.EnumerateFiles(temp.Path).Count());
        using var recovered = new Journal(temp.Path);
        Assert.Single(Directory.EnumerateFiles(temp.Path));
    }
    [Theory]
    [InlineData("pendingAdd")]
    [InlineData("outbox")]
    public void EveryTransitionAndTransactionEnforcesQueueCapsAtomically(string stage)
    {
        var state = FullQueue(stage);
        var overflow = new Change { Kind = "notice", Notice = Event(99999) with { Stage = stage } };
        Assert.Throws<InvalidDataException>(() => state.Apply(overflow));
        Assert.Equal(State.Capacity, state.Notices.Count); Assert.False(state.Notices.ContainsKey(Id(99999)));
        // Even a transaction that later retires a member must not exceed a cap mid-way.
        Assert.Throws<InvalidDataException>(() => state.Transaction([overflow, new() { Kind = "retire", Key = Id(1) }]));
        Assert.True(state.Notices.ContainsKey(Id(1))); Assert.Equal(State.Capacity, state.Notices.Count);
    }
    [Fact] public void BulkEnforcesAggregateCapsAndRestoresAllSharedRoutes()
    {
        var state = FullQueue("outbox");
        foreach (int i in Enumerable.Range(10000, 201)) state.Apply(new() { Kind = "notice", Notice = Removal(i) with { Folders = [Id(8), Id(9)] } });
        var before = JsonSerializer.Serialize(state.Notices);
        Assert.Throws<InvalidDataException>(() => state.Apply(Bulk(10000, 201)));
        Assert.Equal(before, JsonSerializer.Serialize(state.Notices)); state.ValidateInvariants();
    }
    [Theory]
    [InlineData("pendingRemove", "", 201)]
    [InlineData("outbox", "bad setup", 201)]
    [InlineData("outbox", "", 1)]
    [InlineData("outbox", "", 202)]
    public void BulkValidatesStageSetupAndExactMemberCount(string stage, string setup, int count)
    {
        var state = new State();
        foreach (int i in Enumerable.Range(1, 201)) state.Apply(new() { Kind = "notice", Notice = Removal(i) });
        var change = Bulk(1, count); change = change with { Notice = change.Notice! with { Stage = stage, SetupHash = setup } };
        Assert.Throws<InvalidDataException>(() => state.Apply(change));
        Assert.Equal(201, state.Notices.Count); Assert.All(state.Notices.Values, n => Assert.Equal("pendingRemove", n.Stage));
    }
    [Fact] public void SeveralBulkRewritesRollBackEveryDerivedId()
    {
        var state = new State();
        foreach (int i in Enumerable.Range(1, 201)) state.Apply(new() { Kind = "notice", Notice = Removal(i) with { Folders = [Id(8), Id(9), Id(10)] } });
        var first = Bulk(1, 201);
        var second = Bulk(1, 201) with { Notice = first.Notice! with { EventId = Identity.Hash(Id(1) + ":" + Id(8))[..32], Folders = [Id(9)] } };
        var before = state.Notices.Values.OrderBy(n => n.EventId).ToArray();
        Assert.Throws<InvalidDataException>(() => state.Transaction([first, second, new() { Kind = "unknown" }]));
        Assert.Equal(before, state.Notices.Values.OrderBy(n => n.EventId));
    }
    [Fact] public void ExtraneousPayloadCannotRedirectTransactionRollback()
    {
        var state = new State(); var title = new Title(Id(1), "Movie", [Id(8)], null, 1, 0);
        state.Apply(new() { Kind = "title", Title = title });
        var malicious = new Change { Kind = "remove", Tombstone = new(Id(1), 2, 1000, 1), Title = title with { Id = Id(2) } };
        Assert.Throws<InvalidDataException>(() => state.Transaction([malicious, new() { Kind = "unknown" }]));
        Assert.Equal(title, Assert.Single(state.Index.Values)); Assert.Empty(state.Tombstones); Assert.Empty(state.Lifecycles);
    }
    [Fact] public void ChecksummedBulkInjectionIsRejectedDuringReplay()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "journal.ndjson"), Envelope(new { Seq = 1, Changes = new[] { Bulk(1, 1) } }));
        using var recovered = new Journal(temp.Path);
        Assert.True(recovered.RecoveredCorruption); Assert.Empty(recovered.State.Notices); Assert.Equal(0, recovered.State.Seq);
        Assert.Equal(0, new FileInfo(Path.Combine(temp.Path, "journal.ndjson")).Length);
    }
    [Fact] public void AggregateReplayOverflowRejectsTheWholeTailTransaction()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "journal.ndjson");
        using (var output = File.Create(path))
        {
            foreach (int i in Enumerable.Range(1, State.Capacity))
                output.Write(Envelope(new { Seq = i, Changes = new[] { new Change { Kind = "notice", Notice = Event(i) with { Op = "rmbulk", Folders = [Id(8)], Count = 201 } } } }));
            output.Write(Envelope(new { Seq = State.Capacity + 1, Changes = new[] {
                new Change { Kind = "health", Health = new Health(Dropped: 999) },
                new Change { Kind = "notice", Notice = Event(99999) } } }));
        }
        using var recovered = new Journal(temp.Path);
        Assert.True(recovered.RecoveredCorruption); Assert.Equal(State.Capacity, recovered.State.Notices.Count);
        Assert.Equal(State.Capacity, recovered.State.Seq); Assert.Equal(0, recovered.State.Health.Dropped); recovered.State.ValidateInvariants();
    }
    [Fact] public void ValidlyChecksummedSnapshotAboveAggregateCapIsQuarantined()
    {
        using var temp = new TempDirectory(); string path = Path.Combine(temp.Path, "snapshot.bin");
        using (var output = File.Create(path))
        {
            output.Write(Envelope(new { Seq = 1, Format = 3 }));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (int i in Enumerable.Range(1, State.Capacity + 1))
            {
                byte[] entry = Envelope(new { Seq = 1, Changes = new[] { new Change { Kind = "notice", Notice = Event(i) } } })[..^1];
                byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, entry.Length);
                output.WriteByte(0); output.Write(length); output.Write(entry); hash.AppendData([0]); hash.AppendData(length); hash.AppendData(entry);
            }
            output.WriteByte(4); byte[] footer = new byte[40]; BinaryPrimitives.WriteInt64LittleEndian(footer, State.Capacity + 1);
            hash.GetHashAndReset().CopyTo(footer, 8); output.Write(footer);
        }
        using var recovered = new Journal(temp.Path);
        Assert.True(recovered.RecoveredCorruption); Assert.Empty(recovered.State.Notices); Assert.True(File.Exists(path + ".corrupt"));
    }
    [Fact] public void AssemblyVersionsMatchTheAbiMigrationScheme()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var net9 = AssemblyName.GetAssemblyName(Path.Combine(root, "Jellyfin.Plugin.JellySeeNotifications/bin/Release/net9.0/Jellyfin.Plugin.JellySeeNotifications.dll")).Version;
        var net10 = typeof(Engine).Assembly.GetName().Version;
        Assert.Equal(new Version(1, 0, 0, 0), net9); Assert.Equal(new Version(1, 0, 0, 1), net10); Assert.True(net10 > net9);
    }
    // Fixture creation only. No handwritten native calls in either assembly.
    private static void CreateHardLink(string path, string target)
    {
        var command = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/ln") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (OperatingSystem.IsWindows()) { command.ArgumentList.Add("/c"); command.ArgumentList.Add("mklink"); command.ArgumentList.Add("/H"); }
        command.ArgumentList.Add(OperatingSystem.IsWindows() ? path : target);
        command.ArgumentList.Add(OperatingSystem.IsWindows() ? target : path);
        using var process = Process.Start(command)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000), "Hardlink fixture timed out");
        Assert.True(process.ExitCode == 0, output);
    }
    private static State FullQueue(string stage)
    {
        var state = new State();
        foreach (int i in Enumerable.Range(1, State.Capacity)) state.Apply(new() { Kind = "notice", Notice = Event(i) with { Stage = stage } });
        return state;
    }
    private static Notice Removal(int i) => Event(i) with { Op = "rm", Stage = "pendingRemove", Folders = [Id(8)], QueuedAt = 1000 };
    private static Change Bulk(int id, int count) => new() { Kind = "bulk", Key = "1600", Notice = Event(id) with { Op = "rmbulk", Stage = "outbox", Folders = [Id(8)], Count = count, QueuedAt = 1000 } };
    private static byte[] Envelope<T>(T value)
    {
        string payload = JsonSerializer.Serialize(value);
        return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Payload = payload, Sha256 = Identity.Hash(payload) }) + "\n");
    }
}
