using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute() { if (OperatingSystem.IsWindows()) Skip = "Unix FIFO fixture"; }
}

public sealed class StateIoRegressionTests
{
    private static readonly TimeSpan IoTimeout = TimeSpan.FromMilliseconds(500);

    [UnixTheory]
    [InlineData("journal.ndjson")]
    public async Task FifoReadWithConnectedSilentWriterAlsoTimesOut(string name)
    {
        using var temp = new TempDirectory(); using var fifo = new Fifo(Path.Combine(temp.Path, name));
        using var peer = new FileStream(Path.Combine(temp.Path, name), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var failure = await Assert.ThrowsAsync<StatePersistenceException>(() => Task.Run(() =>
            new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, IoTimeout)).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsType<TimeoutException>(failure.InnerException);
    }

    [UnixTheory]
    [InlineData("snapshot.bin")]
    [InlineData("journal.ndjson")]
    public async Task FifoConstructorTimesOutAndNeverRetries(string name)
    {
        using var temp = new TempDirectory(); using var fifo = new Fifo(Path.Combine(temp.Path, name));
        var timer = Stopwatch.StartNew();
        // Outer deadline also bounds a regression that accidentally restores a synchronous open.
        var failure = await Assert.ThrowsAsync<StatePersistenceException>(() => Task.Run(() =>
            new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, IoTimeout)).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.InRange(timer.Elapsed, IoTimeout, TimeSpan.FromSeconds(3));
        Assert.Single(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [UnixTheory]
    [InlineData("snapshot.bin")]
    [InlineData("journal.ndjson")]
    public async Task LaterSizeProbeUsesMetadataWithoutOpeningFifoOrAbandoningWorker(string name)
    {
        using var temp = new TempDirectory();
        using var journal = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3));
        string path = Path.Combine(temp.Path, name); File.Delete(path);
        using var fifo = new Fifo(path);
        Assert.Equal(0, await Task.Run(() => journal.DiskBytes).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, journal.DiskBytes); // worker is still usable
        var timer = Stopwatch.StartNew();
        journal.Dispose();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerUsesDedicatedBackgroundThreadAndNeverJoinsItOnTimeoutOrShutdown(bool cancel)
    {
        using var ct = new CancellationTokenSource(); using var blocked = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = new StateIoWorker(ct.Token, IoTimeout, () => cleaned.TrySetResult());
        try
        {
            var call = Task.Run(() => worker.Invoke(() => { entered.TrySetResult(Thread.CurrentThread); blocked.Wait(); }));
            var thread = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(thread.IsBackground); Assert.False(thread.IsThreadPoolThread);
            if (cancel) ct.Cancel();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(3)));
            else await Assert.ThrowsAsync<StatePersistenceException>(() => call.WaitAsync(TimeSpan.FromSeconds(3)));
            var timer = Stopwatch.StartNew(); worker.Dispose();
            Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(250));
            Assert.False(cleaned.Task.IsCompleted);
        }
        finally { blocked.Set(); }
        await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [UnixTheory]
    [InlineData("snapshot.bin", false)]
    [InlineData("journal.ndjson", false)]
    [InlineData("snapshot.bin", true)]
    [InlineData("journal.ndjson", true)]
    public async Task HostShutdownCancelsWaitBeforeStorageDeadline(string name, bool net9Start)
    {
        using var temp = new TempDirectory(); using var fifo = new Fifo(Path.Combine(temp.Path, name));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new PluginConfiguration();
        var library = Proxy<ILibraryManager>((_, _) => null);
        var compat = Proxy<IJellyfinCompat>((_, _) => throw new InvalidOperationException());
        var server = Proxy<IServerApplicationHost>((_, _) => "fixture-server");
        using var relay = new RelayClient(new Handler(_ => throw new InvalidOperationException("No network allowed")));
        using var service = new NotificationService(library, compat, server, relay, NullLogger<NotificationService>.Instance,
            () => config, () => { entered.TrySetResult(); return (temp.Path, Path.GetDirectoryName(temp.Path)!); }, TimeSpan.FromSeconds(30));
        using var net9 = new Net9StartAdapter(service);
        using var host = new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostedService>(net9Start ? net9 : service)).Build();
        await host.StartAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Let the dedicated thread reach the FIFO; its deadline is deliberately much longer.
        await Task.Delay(100);
        var timer = Stopwatch.StartNew(); await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
    }

    // .NET 9 BackgroundService.StartAsync calls ExecuteAsync directly; .NET 10
    // dispatches it with Task.Run. Use the v9 startup algorithm inside a minimal
    // Host even when only runtime 10 is installed, exercising the actual service
    // ExecuteAsync (including its first yield), not a substitute worker body.
    // https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/BackgroundService.cs
    private sealed class Net9StartAdapter(NotificationService service) : IHostedService, IDisposable
    {
        private CancellationTokenSource? stopping;
        internal Task? Execution { get; private set; }
        public Task StartAsync(CancellationToken ct)
        {
            stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Execution = (Task)typeof(NotificationService).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, [stopping.Token])!;
            return Execution.IsCompleted ? Execution : Task.CompletedTask;
        }
        public async Task StopAsync(CancellationToken ct)
        {
            stopping?.Cancel();
            if (Execution is not null) await Execution.WaitAsync(ct);
        }
        public void Dispose() { var source = Interlocked.Exchange(ref stopping, null); source?.Cancel(); source?.Dispose(); }
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
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
    private sealed class Fifo : IDisposable
    {
        private readonly string path;
        internal Fifo(string path)
        {
            this.path = path;
            using var process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { "-m", "600", path }, UseShellExecute = false })!;
            Assert.True(process.WaitForExit(3000)); Assert.Equal(0, process.ExitCode);
        }
        public void Dispose()
        {
            // Opening both ends is nonblocking on Unix and releases an abandoned
            // reader. Production never does this; fixtures must leave no hung threads.
            using var release = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            release.WriteByte(0); release.Flush(); // also release an already-open blocked read
        }
    }
}
