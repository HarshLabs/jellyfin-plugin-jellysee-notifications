using Jellyfin.Plugin.JellySeeNotifications.Core;
using Xunit;
using static Jellyfin.Plugin.JellySeeNotifications.Tests.ProtocolTests;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows mandatory sharing fixture"; }
}

public sealed class StateSharingRegressionTests
{
    [Fact] public void LengthAndDiskBytesWorkWhileExclusiveWriterIsLive()
    {
        using var temp = new TempDirectory();
        using var files = new StateDirectory(temp.Path, Path.GetDirectoryName(temp.Path)!);
        using (var writer = files.Rewrite("journal.ndjson", null, 0))
        {
            Assert.Equal(0, files.Length("journal.ndjson"));
            writer.WriteByte(1); writer.Flush(true);
            Assert.Equal(1, files.Length("journal.ndjson"));
        }
        File.Delete(Path.Combine(temp.Path, "journal.ndjson"));
        using var journal = new Journal(temp.Path);
        Assert.Equal(0, journal.DiskBytes);
        journal.Commit(new Change { Kind = "notice", Notice = Event() });
        Assert.True(journal.DiskBytes > 0);
    }

    [Theory]
    [InlineData("journal.ndjson")]
    [InlineData("snapshot.bin")]
    [InlineData("snapshot.bin.corrupt")]
    public void RewriteClosesOwnedDestinationReaderBeforeReplacement(string name)
    {
        using var temp = new TempDirectory();
        using var files = new StateDirectory(temp.Path, Path.GetDirectoryName(temp.Path)!);
        File.WriteAllBytes(Path.Combine(temp.Path, name), [42]);
        using var reader = files.Read(name)!; // Read|Delete, no write sharing
        using var replacement = files.Rewrite(name, reader, 1, closeInput: true);
        replacement.Position = replacement.Length;
        replacement.WriteByte(43); replacement.Flush(true);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
        Assert.Equal(2, files.Length(name));
        // Delete of an owned live writer remains supported by Delete sharing.
        files.Remove(name);
        replacement.Dispose();
        Assert.Equal(0, files.Length(name));
    }

    [Fact] public void ExistingJournalAndRepeatedCompactionsReopenWithDurableState()
    {
        using var temp = new TempDirectory();
        for (int generation = 1; generation <= 3; generation++)
        {
            using var journal = new Journal(temp.Path);
            Assert.Equal(generation - 1, journal.State.Seq);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                journal.Compact(); // closes the old writer before replacement
                Assert.True(journal.DiskBytes > 0);
            }
            journal.Commit(new Change { Kind = "health", Health = new(Dropped: generation) });
            Assert.Equal(generation, journal.State.Seq);
        }
        using var reopened = new Journal(temp.Path);
        Assert.Equal(3, reopened.State.Seq); Assert.Equal(3, reopened.State.Health.Dropped);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp.*"));
    }

    [Fact] public void RepeatedQuarantineReplacesDestinationAndClosesSnapshotBeforeDelete()
    {
        using var temp = new TempDirectory();
        using var files = new StateDirectory(temp.Path, Path.GetDirectoryName(temp.Path)!);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            string corrupt = "corrupt-" + cycle;
            File.WriteAllText(Path.Combine(temp.Path, "snapshot.bin"), corrupt);
            // Production has no retained handles to the quarantine destination.
            using var journal = new Journal(temp.Path);
            Assert.True(journal.RecoveredCorruption);
            Assert.False(File.Exists(Path.Combine(temp.Path, "snapshot.bin")));
            Assert.Equal(corrupt, File.ReadAllText(Path.Combine(temp.Path, "snapshot.bin.corrupt")));
        }
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp.*"));
    }

    [WindowsFact] public void FailedJournalReplacementAfterSnapshotCanReplayAndResume()
    {
        using var temp = new TempDirectory();
        using (var journal = new Journal(temp.Path))
        {
            journal.Commit(new Change { Kind = "notice", Notice = Event() });
            using (var blocker = new FileStream(Path.Combine(temp.Path, "journal.ndjson"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite)) // deny Delete only
            {
                var failure = Assert.Throws<StatePersistenceException>(() => journal.Compact());
                Assert.True(StateFailure.IsSharing(failure));
                Assert.True(File.Exists(Path.Combine(temp.Path, "snapshot.bin")));
                Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp.*"));
            }
        }
        using var reopened = new Journal(temp.Path);
        Assert.Single(reopened.State.Notices); Assert.Equal(1, reopened.State.Seq);
        reopened.Commit(new Change { Kind = "health", Health = new(Dropped: 7) });
        reopened.Compact(); Assert.Equal(2, reopened.State.Seq);
    }

    [Fact] public void TimeoutNeverBecomesRetryableThroughAnInnerSharingError()
    {
        Assert.False(StateFailure.IsSharing(new StatePersistenceException(new TimeoutException("private-path-secret", Sharing(32)))));
        Assert.False(StateFailure.IsSharing(new UnauthorizedAccessException("private-path-secret")));
        Assert.True(StateFailure.IsSharing(new StatePersistenceException(new StateReplacementException(new UnauthorizedAccessException("private-path-secret")))));
    }

    [Theory]
    [InlineData("journal.ndjson")]
    [InlineData("snapshot.bin")]
    public void MetadataLengthRetainsLinkAndContainmentChecks(string name)
    {
        using var temp = new TempDirectory(); using var outside = new TempDirectory();
        using var files = new StateDirectory(temp.Path, Path.GetDirectoryName(temp.Path)!);
        Assert.Equal(0, files.Length(name));
        string target = Path.Combine(outside.Path, "keep"); File.WriteAllText(target, "unchanged");
        File.CreateSymbolicLink(Path.Combine(temp.Path, name), target);
        Assert.Throws<IOException>(() => files.Length(name));
        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.Throws<IOException>(() => files.Length("../keep"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedOrStalledWorkerCleanupNeverAuthorizesReopen(bool stalled)
    {
        using var release = new ManualResetEventSlim();
        using var worker = new StateIoWorker(default, TimeSpan.FromMilliseconds(100), () =>
        {
            if (stalled) release.Wait();
            throw new IOException("private-path-secret");
        });
        worker.Invoke(() => { });
        try { worker.Dispose(); Assert.False(worker.CanRestart); }
        finally { release.Set(); }
    }

    private static IOException Sharing(int code) => new("private-path-secret", unchecked((int)0x80070000) | code);
}
