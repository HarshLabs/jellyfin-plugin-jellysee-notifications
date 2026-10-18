using System.Data.Common;
using System.Text.Json;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using Xunit;
using static Jellyfin.Plugin.JellySeeNotifications.Tests.ProtocolTests;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class LoopRegressionTests
{
    private sealed class QueryFailure : DbException;
    [Fact] public async Task TransientQueriesAtPreflightAndSendKeepTheLoopAndSubscriptionsAlive()
    {
        using var ct = new CancellationTokenSource();
        int queries = 0, iterations = 0, received = 0;
        var signals = new SignalSource(); signals.Changed += () => received++;
        List<string> errors = []; List<TimeSpan> delays = [];
        void Users() { if (++queries is 1 or 3) throw new QueryFailure(); }
        await IterationLoop.Run(() =>
        {
            iterations++; signals.Emit();
            Users(); // readiness preflight
            Users(); // sender preflight
            ct.Cancel(); return Task.CompletedTask;
        }, errors.Add, ct.Token, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        Assert.Equal(3, iterations); Assert.Equal(3, received); Assert.Equal(5, queries);
        Assert.Equal(new[] { "adapterUnavailable", "adapterUnavailable", "none" }, errors);
        Assert.Equal(new[] { 1d, 2d, 1d }, delays.Select(d => d.TotalSeconds));
        using var json = JsonDocument.Parse(Heartbeat.Encode("server", new State(), 1000, errors[0], false, 0));
        Assert.Equal("adapterUnavailable", json.RootElement.GetProperty("errorCode").GetString());
    }
    [Fact] public async Task PersistentQueryFailuresHaveBoundedBackoffAndNoExceptionTextInHealth()
    {
        using var ct = new CancellationTokenSource(); List<double> delays = []; List<string> errors = [];
        await IterationLoop.Run(() => throw new QueryFailure(), errors.Add, ct.Token, (delay, _) =>
        {
            delays.Add(delay.TotalSeconds); if (delays.Count == 10) ct.Cancel(); return Task.CompletedTask;
        });
        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 32, 60, 60, 60, 60 }, delays);
        Assert.All(errors, e => Assert.Equal("adapterUnavailable", e));
    }
    [Fact] public async Task PersistenceAndShutdownEscapeWithoutRetrying()
    {
        int retries = 0;
        var fatal = new StatePersistenceException(new IOException());
        var thrown = await Assert.ThrowsAsync<StatePersistenceException>(() => IterationLoop.Run(() => throw fatal, _ => retries++, CancellationToken.None));
        Assert.Same(fatal, thrown); Assert.Equal(0, retries);
        using var ct = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IterationLoop.Run(() => { ct.Cancel(); ct.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, _ => retries++, ct.Token));
        Assert.Equal(0, retries);
    }
    [Fact] public void TimerCannotStarveTheTailOfTwoHundredLibraries()
    {
        var libraries = Enumerable.Range(1, 200).Select(Library).ToArray(); var queue = new ReconcileQueue(); queue.Request();
        List<string> visited = [];
        for (int i = 0; i < 200; i++)
        {
            if (i == 180) queue.Request(); // 15 minutes at five seconds/query
            visited.Add(queue.Next(libraries)!.Id);
        }
        Assert.Equal(libraries.Select(l => l.Id), visited);
        Assert.Equal(libraries[0], queue.Next(libraries));
    }
    [Fact] public void DirtyRequestsDuringALongScanCoalesceWithoutResettingItsProgressOrTail()
    {
        var libraries = Enumerable.Range(1, 3).Select(Library).ToArray(); var queue = new ReconcileQueue(); queue.Request();
        Assert.Equal(libraries[0], queue.Next(libraries));
        int position = 0;
        // Service calls Next only after this scan finishes, regardless of dirty/timer requests.
        for (; position < 300_000; position += 1000) queue.Request();
        Assert.Equal(300_000, position);
        Assert.Equal(libraries[1], queue.Next(libraries)); Assert.Equal(libraries[2], queue.Next(libraries));
        foreach (var library in libraries) Assert.Equal(library, queue.Next(libraries));
        Assert.Null(queue.Next(libraries));
    }
    private static Library Library(int id) => new(Identity.Hash("dir" + id), Id(id), [], true, true);
    private sealed class SignalSource { internal event Action? Changed; internal void Emit() => Changed?.Invoke(); }
}
