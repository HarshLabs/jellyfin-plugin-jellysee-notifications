using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Jellyfin.Plugin.JellySeeNotifications.Core;

namespace Jellyfin.Plugin.JellySeeNotifications;

internal sealed record RuntimeSnapshot
{
    public string ErrorCode { get; init; } = "stateUnavailable";
    public string SetupHash { get; init; } = "";
    public string BootId { get; init; } = Identity.EventId();
    public long ProgressStamp { get; init; }
    public long? OldestReadyStamp { get; init; }
    public long? UncoveredStamp { get; init; }
    public bool StorageUnavailable { get; init; } = true;
    public bool RestartRequired { get; init; }
    public int OutboxDepth { get; init; }
    public long Dropped { get; init; }
    public long DroppedTotal { get; init; }
    public long? LastAckAt { get; init; }
    public bool IndexFull { get; init; }
    public bool TooManyUsers { get; init; }
    public int IndexCount { get; init; }
    public long StateBytes { get; init; }
    public int BaselineLibrariesRemaining { get; init; }
    public ImmutableArray<string> AdminUserHashes { get; init; } = [];
    public long AdminStamp { get; init; }
    public ImmutableArray<Binding> Bindings { get; init; } = [];
}
internal sealed record AdminSnapshot(ImmutableArray<string> Hashes, long At, bool TooManyUsers = false, string ErrorCode = "none");

internal static class CauseCodes
{
    internal static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    { "none", "relayUnreachable", "unauthorized", "tooManyUsers", "indexFull", "stateRecovered", "inventoryIncomplete", "itemEvaluationFailed", "reconcileFailed", "invalidConfiguration", "adapterUnavailable", "stateUnavailable", "libraryStalled", "jellyfinStalled", "channelDropped", "requiredCallTimeout", "restartRequired" };
}

// One monotonic rolling window across all supervisor sessions and all attempts.
internal sealed class HeartbeatLimiter(TimeProvider clock)
{
    private readonly Queue<long> attempts = new();
    private long? backoff;
    private TimeSpan backoffDelay;
    internal bool TryAcquire()
    {
        long now = clock.GetTimestamp();
        while (attempts.TryPeek(out long at) && clock.GetElapsedTime(at, now) >= TimeSpan.FromHours(1)) attempts.Dequeue();
        if (attempts.Count >= 12 || backoff is { } b && clock.GetElapsedTime(b, now) < backoffDelay) return false;
        attempts.Enqueue(now); return true;
    }
    internal void Backoff(TimeSpan delay) { backoff = clock.GetTimestamp(); backoffDelay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay; }
}

public sealed record DiagnosticEntry(DateTimeOffset Utc, double MonotonicSeconds, string Cause);
internal sealed class DiagnosticsRing : IDisposable
{
    internal const int MaxBytes = 256 * 1024;
    private readonly Channel<DiagnosticEntry> queue = Channel.CreateBounded<DiagnosticEntry>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;
    internal Task Completion => stopped.Task;
    private ImmutableArray<DiagnosticEntry> entries = [];
    private readonly object gate = new();
    private readonly Func<(string Directory, string Root)> paths;
    internal DiagnosticsRing(Func<(string Directory, string Root)> paths, TimeProvider clock)
    {
        this.paths = paths; this.clock = clock;
        // Dedicated thread: a blocked filesystem cannot starve heartbeat or the loop.
        new Thread(() =>
        {
            try
            {
                Load();
                while (queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                { while (queue.Reader.TryRead(out _)) { } if (!stop.IsCancellationRequested) Write(); }
            }
            finally { stop.Dispose(); stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "JellySee diagnostics" }.Start();
    }
    internal void Record(string cause)
    {
        if (!CauseCodes.Allowed.Contains(cause)) return;
        var entry = new DiagnosticEntry(clock.GetUtcNow(), (double)clock.GetTimestamp() / clock.TimestampFrequency, cause);
        lock (gate) { entries = entries.Add(entry); if (entries.Length > 1024) entries = entries.RemoveAt(0); }
        queue.Writer.TryWrite(entry);
    }
    internal DiagnosticEntry[] Read() { lock (gate) return entries.ToArray(); }
    private void Load()
    {
        try
        {
            var path = paths(); using var files = new StateDirectory(Path.Combine(path.Directory, "diagnostics"), path.Root, stop.Token);
            using var input = files.Read("diagnostics.ndjson");
            if (input is null || input.Length > MaxBytes) return;
            byte[] bytes = new byte[input.Length]; input.ReadExactly(bytes);
            var recovered = System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<DiagnosticEntry>(line)).Where(e => e is not null && CauseCodes.Allowed.Contains(e.Cause) && double.IsFinite(e.MonotonicSeconds)).Select(e => e!).TakeLast(1024).ToArray();
            lock (gate) entries = recovered.Concat(entries).TakeLast(1024).ToImmutableArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private void Write()
    {
        try
        {
            var path = paths();
            using var files = new StateDirectory(Path.Combine(path.Directory, "diagnostics"), path.Root, stop.Token);
            byte[] bytes;
            lock (gate) bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
            // Fixed-size records; reserve space for encoding and delimiters.
            if (bytes.Length > MaxBytes) return;
            using var output = files.Rewrite("diagnostics.ndjson", null, 0);
            DiagnosticEntry[] current = Read();
            foreach (var line in current) { output.Write(JsonSerializer.SerializeToUtf8Bytes(line)); output.WriteByte(10); }
            output.Flush(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Optional diagnostics must never affect delivery. */ }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); queue.Writer.TryComplete();
        // Optional diagnostics never hold host shutdown behind a blocked filesystem.
        stopped.Task.Wait(TimeSpan.FromMilliseconds(100));
    }
}

public static class Heartbeat
{
    public static byte[] Encode(string server, State state, long now, string error, bool tooManyUsers, long bytes)
    {
        var clock = TimeProvider.System;
        return Encode(server, new RuntimeSnapshot { ErrorCode = error, StorageUnavailable = error == "stateUnavailable", ProgressStamp = clock.GetTimestamp(), OutboxDepth = state.Notices.Values.Count(n => n.Stage == "outbox"), Dropped = state.Health.Dropped - state.Health.ReportedDropped, DroppedTotal = state.Health.Dropped, LastAckAt = state.Health.LastAckAt, IndexFull = state.Health.IndexFull, TooManyUsers = tooManyUsers, IndexCount = state.Index.Count, StateBytes = bytes, BaselineLibrariesRemaining = state.Libraries.Values.Count(l => !l.BaselineComplete) }, clock, false);
    }
    internal static long Age(TimeProvider clock, long? stamp) => stamp is null ? 0 : Math.Clamp((long)clock.GetElapsedTime(stamp.Value).TotalSeconds, 0, 86400);
    internal static bool Healthy(RuntimeSnapshot s, TimeProvider clock, bool stuck) => !s.RestartRequired && !s.StorageUnavailable && !stuck && s.ErrorCode is not ("requiredCallTimeout" or "libraryStalled" or "jellyfinStalled" or "stateUnavailable" or "restartRequired") && Age(clock, s.ProgressStamp) <= 300 && Age(clock, s.OldestReadyStamp) <= 600 && !s.IndexFull && !s.TooManyUsers && Age(clock, s.UncoveredStamp) <= 1800;
    internal static byte[] Encode(string server, RuntimeSnapshot s, TimeProvider clock, bool stuck, IEnumerable<Binding>? bindings = null)
    {
        if (!CauseCodes.Allowed.Contains(s.ErrorCode)) throw new InvalidDataException("Invalid health code");
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 3, server, version = typeof(Plugin).Assembly.GetName().Version!.ToString(),
            outboxDepth = s.OutboxDepth, oldestPendingAgeS = Age(clock, s.OldestReadyStamp), droppedSinceLast = s.Dropped,
            // A backward correction can invalidate a previously real UTC sample. The relay
            // rejects future acknowledgements; unknown preserves heartbeats even with the loop blocked.
            lastAckAt = s.LastAckAt > clock.GetUtcNow().ToUnixTimeSeconds() + 120 ? null : s.LastAckAt,
            errorCode = s.RestartRequired ? "restartRequired" : s.TooManyUsers ? "tooManyUsers" : s.IndexFull ? "indexFull" : s.ErrorCode,
            indexFull = s.IndexFull, tooManyUsers = s.TooManyUsers, indexCount = s.IndexCount, stateBytes = s.StateBytes,
            baselineLibrariesRemaining = s.BaselineLibrariesRemaining, leaseSeconds = 1200, progressAge = Age(clock, s.ProgressStamp), bootId = s.BootId,
            storageUnavailable = s.StorageUnavailable, requiredCallStuck = stuck || s.ErrorCode is "requiredCallTimeout" or "libraryStalled" or "jellyfinStalled", restartRequired = s.RestartRequired, uncoveredAge = Age(clock, s.UncoveredStamp),
            adminUserHashes = Age(clock, s.AdminStamp) <= 1800 ? s.AdminUserHashes : [],
            bindings = (bindings ?? []).Take(64).Select(b => new { registration = b.Registration, profile = b.Profile, userHash = b.UserHash })
        });
    }
}
