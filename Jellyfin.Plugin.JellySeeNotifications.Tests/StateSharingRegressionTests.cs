using System.Reflection;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
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

    [WindowsFact] public async Task ServiceReopensExistingJournalAfterRealSharingViolation()
    {
        using var temp = new TempDirectory();
        using (var journal = new Journal(temp.Path)) journal.Commit(new Change { Kind = "notice", Notice = Event() });
        using var blocker = new FileStream(Path.Combine(temp.Path, "journal.ndjson"), FileMode.Open, FileAccess.Read, FileShare.None);
        await ExerciseServiceRecovery(temp.Path, null, () => blocker.Dispose());
        using var reopened = new Journal(temp.Path);
        Assert.Single(reopened.State.Notices); Assert.Equal(1, reopened.State.Seq);
    }

    [Theory]
    [InlineData(false, 32)] [InlineData(true, 32)]
    [InlineData(false, 33)] [InlineData(true, 33)]
    [InlineData(false, 5)] [InlineData(true, 5)]
    public async Task ServiceRetriesSharingFailuresAtStartupAndAfterJournalLoad(bool afterStartup, int code)
    {
        using var temp = new TempDirectory();
        await ExerciseServiceRecovery(temp.Path, afterStartup, null, code);
    }

    private static async Task ExerciseServiceRecovery(string path, bool? afterStartup, Action? release, int code = 32)
    {
        int pathCalls = 0, configCalls = 0, retries = 0, subscriptions = 0;
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new PluginConfiguration(); // disabled: no external HTTP
        var logger = new RecordingLogger();
        var library = Proxy<ILibraryManager>((method, _) =>
        {
            if (method.Name.StartsWith("add_")) subscriptions++;
            if (method.Name.StartsWith("remove_") && subscriptions > 0) subscriptions--;
            return null;
        });
        using var relay = new RelayClient(new NoNetwork());
        using var service = new NotificationService(library, Proxy<IJellyfinCompat>((_, _) => throw new InvalidOperationException()),
            Proxy<IServerApplicationHost>((_, _) => "fixture-server"), relay, logger,
            () =>
            {
                if (afterStartup == true && ++configCalls == 1)
                    throw new StatePersistenceException(Recoverable(code));
                if (retries > 0 && config.Status == "notConfigured") healthy.TrySetResult();
                return config;
            },
            () =>
            {
                if (++pathCalls == 1 && afterStartup == false) throw Recoverable(code);
                return (path, Path.GetDirectoryName(path)!);
            }, TimeSpan.FromSeconds(3), async (pause, ct) =>
            {
                retries++;
                Assert.Equal(TimeSpan.FromSeconds(1), pause);
                Assert.Equal(0, subscriptions);
                Assert.Equal("stateUnavailable", config.Status);
                release?.Invoke();
                await Task.Yield(); ct.ThrowIfCancellationRequested();
            });
        await service.StartAsync(default);
        try
        {
            await healthy.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.False(service.ExecuteTask!.IsCompleted);
            Assert.Equal(2, pathCalls); Assert.Equal(1, retries); Assert.Equal(3, subscriptions);
            var log = Assert.Single(logger.Messages);
            Assert.Contains(code == 5 ? "System.UnauthorizedAccessException" : "System.IO.IOException", log);
            Assert.Contains($"800700{code:X2}", log);
            Assert.DoesNotContain("private-path-secret", log); Assert.All(logger.Exceptions, Assert.Null);
        }
        finally { await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.Equal(0, subscriptions);
    }

    [Theory]
    [InlineData(5)] [InlineData(2)] [InlineData(123)]
    public async Task OtherStateFailuresStopAndLogOnlyTypeAndHResult(int code)
    {
        var config = new PluginConfiguration(); var logger = new RecordingLogger(); int retries = 0;
        using var relay = new RelayClient(new NoNetwork());
        using var service = new NotificationService(Proxy<ILibraryManager>((_, _) => null),
            Proxy<IJellyfinCompat>((_, _) => null), Proxy<IServerApplicationHost>((_, _) => "fixture-server"), relay, logger,
            () => config, () => throw new StatePersistenceException(Sharing(code)), TimeSpan.FromSeconds(1),
            (_, _) => { retries++; return Task.CompletedTask; });
        await service.StartAsync(default);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, retries); Assert.Equal("stateUnavailable", config.Status);
        Assert.Equal($"[JellySee Notifications] State unavailable (stateUnavailable); exceptionType=System.IO.IOException; hResult=800700{code:X2}", Assert.Single(logger.Messages));
        Assert.All(logger.Exceptions, Assert.Null);
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

    [Fact] public async Task PersistentSharingFailuresKeepRetryingWithCappedBackoffAndCancelPromptly()
    {
        var config = new PluginConfiguration(); var logger = new RecordingLogger();
        List<TimeSpan> pauses = [];
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var relay = new RelayClient(new NoNetwork());
        using var service = new NotificationService(Proxy<ILibraryManager>((_, _) => null),
            Proxy<IJellyfinCompat>((_, _) => null), Proxy<IServerApplicationHost>((_, _) => "fixture-server"), relay, logger,
            () => config, () => throw Sharing(32), TimeSpan.FromSeconds(1), async (pause, ct) =>
            {
                pauses.Add(pause);
                if (pauses.Count == 9) { waiting.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            });
        await service.StartAsync(default);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(service.ExecuteTask!.IsCompleted);
            Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 60, 60, 60 }.Select(seconds => TimeSpan.FromSeconds(seconds)), pauses);
            Assert.Equal("stateUnavailable", config.Status);
        }
        finally { await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(1)); }
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

    private static Exception Recoverable(int code) => code == 5
        ? new StateReplacementException(new UnauthorizedAccessException("private-path-secret")) : Sharing(code);
    private static IOException Sharing(int code) => new("private-path-secret", unchecked((int)0x80070000) | code);
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Unexpected network request");
    }
    private sealed class RecordingLogger : ILogger<NotificationService>
    {
        internal readonly List<string> Messages = [];
        internal readonly List<Exception?> Exceptions = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, exception)); Exceptions.Add(exception); }
    }
    public class RecordingProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, RecordingProxy>(); ((RecordingProxy)(object)proxy).Call = call; return proxy;
    }
}
