using System.Collections.Immutable;
using System.Threading.Channels;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySeeNotifications;

public sealed class NotificationService : BackgroundService
{
    private readonly ILibraryManager library;
    private readonly IJellyfinCompat compat;
    private readonly IServerApplicationHost host;
    private readonly RelayClient relay;
    private readonly ILogger<NotificationService> logger;
    private readonly Func<PluginConfiguration> configuration;
    private readonly Func<(string Directory, string Root)> statePaths;
    private readonly TimeSpan stateIoTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> stateRetryDelay;
    private readonly TimeProvider timeProvider;
    private readonly LibraryLanes lanes;
    private readonly Func<string, string, CancellationToken, StateOwnership, Journal> openJournal;
    private readonly DiagnosticsRing diagnostics;
    private readonly HeartbeatLimiter limiter;
    private readonly Channel<Signal> channel;
    private readonly Channel<BindWork> bindQueue = Channel.CreateBounded<BindWork>(64);
    private readonly object bindGate = new();
    private long bindSequence, bindAdmittedThrough;
    private readonly object coverageGate = new();
    private long coverageEpoch;
    private ImmutableArray<LibraryRoot> libraryRoots = [];
    private readonly Dictionary<string, int> signalledAdds = new(StringComparer.Ordinal);
    private ImmutableHashSet<string> covered = ImmutableHashSet<string>.Empty;
    private RuntimeSnapshot snapshot;
    private AdminSnapshot admins = new([], 0);
    private RuntimeSnapshot CurrentSnapshot()
    {
        var s = Volatile.Read(ref snapshot); var a = Volatile.Read(ref admins);
        return s with { AdminUserHashes = a.Hashes, AdminStamp = a.At, TooManyUsers = s.TooManyUsers || a.TooManyUsers, ErrorCode = s.ErrorCode == "none" ? a.ErrorCode : s.ErrorCode };
    }
    internal RuntimeSnapshot Snapshot => CurrentSnapshot();
    internal Task DiagnosticsStopped => diagnostics.Completion;
    internal ImmutableHashSet<string> CoveredLibraries => Volatile.Read(ref covered);
    private string setupHash = "";
    private long signalSequence;
    private long channelDropped;
    private long reportedDroppedTotal;
    private bool enabled;
    private string error = "none";
    private bool tooManyUsers;
    private sealed record UtcAnchor(long Utc, long Stamp);
    private UtcAnchor utcAnchor;
    private long clockEpoch;
    private sealed record ClockCorrection(string Id, long Fence, Dictionary<string, (string Id, long Cutoff)> Recoveries);
    private ClockCorrection? pendingClockCorrection;
    private long Now { get { var anchor = Volatile.Read(ref utcAnchor); return anchor.Utc + (long)timeProvider.GetElapsedTime(anchor.Stamp).TotalSeconds; } }
    private long WallNow => timeProvider.GetUtcNow().ToUnixTimeSeconds();
    private long Mono => timeProvider.GetTimestamp();
    private sealed record Signal(string Op, string Id, string? Type, string EventId, long At, string Setup, ImmutableHashSet<string> Coverage, long Sequence, string Origin, long ClockEpoch);
    private sealed record BindWork(Binding Binding, TaskCompletionSource<bool> Completion, long Sequence);

    public NotificationService(ILibraryManager library, IJellyfinCompat compat, IServerApplicationHost host, RelayClient relay, ILogger<NotificationService> logger, ITaskManager tasks)
        : this(library, compat, host, relay, logger,
            () => (Plugin.Instance ?? throw new InvalidOperationException()).Configuration,
            () => { var plugin = Plugin.Instance ?? throw new InvalidOperationException(); return (plugin.DataFolderPath, plugin.StateRootPath); },
            StateIoWorker.DefaultTimeout, tasks: tasks) { }
    internal NotificationService(ILibraryManager library, IJellyfinCompat compat, IServerApplicationHost host, RelayClient relay, ILogger<NotificationService> logger,
        Func<PluginConfiguration> configuration, Func<(string Directory, string Root)> statePaths, TimeSpan stateIoTimeout,
        Func<TimeSpan, CancellationToken, Task>? stateRetryDelay = null, TimeProvider? timeProvider = null, ITaskManager? tasks = null, Func<string, string, CancellationToken, StateOwnership, Journal>? openJournal = null)
    {
        this.library = library; this.compat = compat; this.host = host; this.relay = relay; this.logger = logger;
        this.configuration = configuration; this.statePaths = statePaths; this.stateIoTimeout = stateIoTimeout;
        this.openJournal = openJournal ?? ((directory, root, token, owner) => new Journal(directory, root, token, stateIoTimeout, owner, versioned: true));
        this.stateRetryDelay = stateRetryDelay ?? Task.Delay; this.timeProvider = timeProvider ?? TimeProvider.System;
        utcAnchor = new(this.timeProvider.GetUtcNow().ToUnixTimeSeconds(), Mono);
        snapshot = new() { ProgressStamp = Mono };
        lanes = new(compat, Identity.ServerKey(host.SystemId), this.timeProvider, tasks);
        limiter = new(this.timeProvider); diagnostics = new(statePaths, this.timeProvider);
        channel = Channel.CreateBounded<Signal>(new BoundedChannelOptions(State.Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest }, lost =>
        { CompleteSignal(lost); Interlocked.Increment(ref channelDropped); UncoverLibraries(); });
    }
    private void UncoverLibraries(string? folder = null)
    {
        lock (coverageGate) { coverageEpoch++; Volatile.Write(ref covered, folder is null ? ImmutableHashSet<string>.Empty : covered.Remove(folder)); }
    }
    private bool TryCover(string folder, long epoch)
    {
        lock (coverageGate)
        {
            if (coverageEpoch != epoch || Interlocked.Read(ref channelDropped) != 0) return false;
            Volatile.Write(ref covered, covered.Add(folder)); return true;
        }
    }
    private void Added(object? sender, ItemChangeEventArgs e) { if (Volatile.Read(ref enabled) && compat.IsTitle(e.Item)) Enqueue("add", e.Item, compat.TitleType(e.Item)); }
    private void Removed(object? sender, ItemChangeEventArgs e)
    {
        if (!Volatile.Read(ref enabled)) return;
        if (compat.TitleType(e.Item) is { } type) Enqueue("rm", e.Item, type);
        if (e.Item is Folder) Enqueue("dirty", e.Item, null);
    }
    private void Updated(object? sender, ItemChangeEventArgs e)
    {
        if (!Volatile.Read(ref enabled)) return;
        if (compat.IsTitle(e.Item)) Enqueue("update", e.Item, compat.TitleType(e.Item));
        if (e.Item is Folder) Enqueue("dirty", e.Item, null);
    }
    private void Enqueue(string op, BaseItem item, string? type)
    {
        lock (coverageGate)
        {
            string id = item.Id.ToString("N");
            if (op == "add") signalledAdds[id] = signalledAdds.GetValueOrDefault(id) + 1;
            var signal = new Signal(op, id, type, Identity.EventId(), Now, Volatile.Read(ref setupHash), Volatile.Read(ref covered), ++signalSequence, ReceiptOrigin(item.Path), clockEpoch);
            if (!channel.Writer.TryWrite(signal)) CompleteSignal(signal);
        }
    }
    // Pure in-memory path matching captures the library at receipt without calling Jellyfin.
    // Roots never enter persistence, wire DTOs, logs or diagnostics.
    private string ReceiptOrigin(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "recovery";
        string normalized = path.Replace('\\', '/').TrimEnd('/'); bool matched = false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in libraryRoots)
        {
            string prefix = root.Path.Replace('\\', '/').TrimEnd('/');
            if (!normalized.Equals(prefix, comparison) && !normalized.StartsWith(prefix + "/", comparison)) continue;
            matched = true; if (!covered.Contains(root.Id)) return "recovery";
        }
        return matched ? "live" : "recovery";
    }
    private void CompleteSignal(Signal signal)
    {
        if (signal.Op != "add") return;
        lock (coverageGate)
        {
            if (!signalledAdds.TryGetValue(signal.Id, out int count)) return;
            if (count <= 1) signalledAdds.Remove(signal.Id); else signalledAdds[signal.Id] = count - 1;
        }
    }
    private bool HasSignalledAdd(string id) { lock (coverageGate) return signalledAdds.ContainsKey(id); }
    private sealed class PendingSignalOwner(NotificationService service, Queue<Signal> signals) : IDisposable
    {
        public void Dispose() { foreach (var signal in signals) service.CompleteSignal(signal); }
    }
    private void SetError(string cause)
    {
        if (!CauseCodes.Allowed.Contains(cause)) cause = "adapterUnavailable";
        if (cause == "tooManyUsers") tooManyUsers = true;
        if (cause != error) { error = cause; diagnostics.Record(cause); }
    }
    public HealthResponse Health()
    {
        var config = configuration(); var s = CurrentSnapshot();
        return new(config.Enabled && !string.IsNullOrWhiteSpace(config.Secret) && s.SetupHash == Identity.Hash(config.Secret) && Heartbeat.Healthy(s, timeProvider, lanes.Stuck) ? "healthy" : "not", Identity.Hash(config.Secret));
    }
    public object Diagnostics() => new { status = CurrentSnapshot(), health = Health().Health, causes = diagnostics.Read() };
    public async Task<bool> Bind(string registration, string profile, string userHash, CancellationToken ct)
    {
        var config = configuration();
        if (!config.Enabled || config.Secret.Length == 0 || !Identity.IsId(registration) || !Binding.ValidProfile(profile)) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (bindGate)
        {
            long sequence = bindSequence + 1;
            if (!bindQueue.Writer.TryWrite(new(new(registration, profile, userHash, Identity.Hash(config.Secret), Now), completion, sequence))) return false;
            Interlocked.Exchange(ref bindSequence, sequence);
        }
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (TimeoutException) { return false; }
        finally { completion.TrySetResult(false); }
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        // The heartbeat task is supervisor-owned and outlives every journal session.
        var heartbeat = HeartbeatLoop(ct);
        int failures = 0;
        Dictionary<string, long> logged = new(StringComparer.Ordinal);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ownership = new StateOwnership();
                try
                {
                    var paths = statePaths();
                    using var journal = openJournal(paths.Directory, paths.Root, ct, ownership);
                    await RunJournal(journal, ct, () => failures = 0);
                    if (!ct.IsCancellationRequested) throw new StatePersistenceException(new IOException());
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (ct.IsCancellationRequested) break;
                    Volatile.Write(ref enabled, false); UncoverLibraries();
                    SetError("stateUnavailable");
                    Volatile.Write(ref snapshot, Volatile.Read(ref snapshot) with { StorageUnavailable = true, ErrorCode = error, RestartRequired = !ownership.CanRestart });
                    string cause = StateFailure.Cause(ex).GetType().FullName ?? "Exception";
                    if (!logged.TryGetValue(cause, out long last) || timeProvider.GetElapsedTime(last) >= TimeSpan.FromHours(1))
                    { try { logger.LogError("[JellySee Notifications] State unavailable (stateUnavailable); exceptionType={ExceptionType}; hResult={HResult:X8}", cause, StateFailure.Cause(ex).HResult); } catch (Exception loggingError) when (loggingError is not OutOfMemoryException) { } logged[cause] = Mono; }
                }
                finally
                {
                    Volatile.Write(ref enabled, false); UncoverLibraries();
                    library.ItemAdded -= Added; library.ItemRemoved -= Removed; library.ItemUpdated -= Updated;
                }
                // No second writer, even if construction failed before returning a Journal.
                while (!ownership.CanRestart && !ct.IsCancellationRequested)
                {
                    SetError("restartRequired");
                    Volatile.Write(ref snapshot, Volatile.Read(ref snapshot) with { RestartRequired = true, StorageUnavailable = true, ErrorCode = "restartRequired" });
                    await stateRetryDelay(TimeSpan.FromSeconds(1), ct);
                }
                if (ct.IsCancellationRequested) break;
                diagnostics.Record("stateRecovered");
                failures = Math.Min(failures + 1, 10);
                await stateRetryDelay(TimeSpan.FromSeconds(Math.Min(300, 1 << (failures - 1))), ct);
            }
        }
        finally
        {
            try { await heartbeat; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            lanes.Dispose(); diagnostics.Dispose();
            while (bindQueue.Reader.TryRead(out var binding)) binding.Completion.TrySetResult(false);
        }
    }
    private async Task HeartbeatLoop(CancellationToken ct)
    {
        long? lastAttempt = null; string lastState = ""; int offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var config = configuration(); var s = CurrentSnapshot();
                string state = Identity.Hash(config.Secret) + ":" + s.RestartRequired + ":" + s.StorageUnavailable + ":" + lanes.Stuck;
                bool scheduled = lastAttempt is null || timeProvider.GetElapsedTime(lastAttempt.Value) >= TimeSpan.FromMinutes(10);
                bool transition = state != lastState && (lastAttempt is null || timeProvider.GetElapsedTime(lastAttempt.Value) >= TimeSpan.FromMinutes(5));
                if (config.Enabled && config.Secret.Length != 0 && (scheduled || transition) && limiter.TryAcquire())
                {
                    lastAttempt = Mono; lastState = state;
                    try
                    {
                        var hashes = await lanes.Admins(ct);
                        bool overflow = hashes.Length > 64;
                        Volatile.Write(ref admins, new(overflow ? [] : hashes, Mono, overflow, overflow ? "tooManyUsers" : "none"));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (TooManyUsersException) { Volatile.Write(ref admins, new([], Mono, true, "tooManyUsers")); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { string cause = ex is LibraryCallException ? "requiredCallTimeout" : "itemEvaluationFailed"; Volatile.Write(ref admins, new([], Mono, false, cause)); diagnostics.Record(cause); }
                    s = CurrentSnapshot();
                    string hash = Identity.Hash(config.Secret);
                    var bindings = s.Bindings.Where(b => b.SetupHash == hash).ToArray();
                    var report = bindings.Skip(offset).Take(32).ToArray();
                    byte[] body = Heartbeat.Encode(host.SystemId, s with { ErrorCode = lanes.Stalled ? "jellyfinStalled" : lanes.Stuck ? "libraryStalled" : s.ErrorCode }, timeProvider, lanes.Stuck, report);
                    var result = await relay.Post(config.RelayOrigin, Identity.ServerKey(host.SystemId), config.Secret, "heartbeat", body, [], ct);
                    if (result.Success) { offset = bindings.Length == 0 ? 0 : (offset + report.Length) % bindings.Length; Interlocked.Exchange(ref reportedDroppedTotal, s.DroppedTotal); }
                    else limiter.Backoff(result.RetryAfter ?? TimeSpan.FromMinutes(5));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* Keep last-known counters and retry on the independent timer. */ }
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, ct);
        }
    }
    private void PersistClockCorrection(Journal journal)
    {
        if (pendingClockCorrection is not { } correction) return;
        if (journal.State.Health.ClockCorrectionId != correction.Id)
        {
            List<Change> changes = [new() { Kind = "health", Health = journal.State.Health with { QuietThroughSeq = Math.Max(correction.Fence, journal.State.Health.QuietThroughSeq), ClockCorrectionId = correction.Id } }];
            foreach (var pair in correction.Recoveries)
                if (journal.State.Recoveries.TryGetValue(pair.Key, out var recovery) && recovery.Id == pair.Value.Id)
                    changes.Add(new() { Kind = "recovery", Recovery = recovery with { CutoffAt = pair.Value.Cutoff } });
            journal.Commit(changes.ToArray());
        }
        pendingClockCorrection = null;
    }
    private async Task RunJournal(Journal journal, CancellationToken ct, Action healthy)
    {
        PersistClockCorrection(journal);
        lanes.NewSession();
        var engine = new Engine(journal, lanes, Identity.ServerKey(host.SystemId));
        SetError(journal.RecoveredCorruption ? "stateRecovered" : "none");
        Volatile.Write(ref snapshot, Volatile.Read(ref snapshot) with { BootId = Identity.EventId(), RestartRequired = false, StorageUnavailable = false, ErrorCode = error, UncoveredStamp = Mono, OutboxDepth = journal.State.Notices.Values.Count(n => n.Stage == "outbox"), IndexCount = journal.State.Index.Count, Dropped = journal.State.Health.Dropped - journal.State.Health.ReportedDropped, DroppedTotal = journal.State.Health.Dropped, LastAckAt = journal.State.Health.LastAckAt, IndexFull = journal.State.Health.IndexFull, Bindings = journal.State.Bindings.Values.ToImmutableArray() });
        library.ItemAdded += Added; library.ItemRemoved += Removed; library.ItemUpdated += Updated;
        var reconciliation = new ReconcileQueue();
        Scan? scan = null;
        var pendingSignals = new Queue<Signal>();
        using var pendingOwner = new PendingSignalOwner(this, pendingSignals);
        var removedThrough = new Dictionary<string, long>(StringComparer.Ordinal);
        var schedules = new Dictionary<string, SendSchedule>(StringComparer.Ordinal);
        var batchStarted = new Dictionary<string, long>(StringComparer.Ordinal);
        var sentAt = new Queue<long>();
        var eventSentAt = new Queue<long>();
        var bindingSentAt = new Queue<long>();
        var reportedBindings = new HashSet<string>(StringComparer.Ordinal);
        var refreshBindings = new HashSet<string>(StringComparer.Ordinal);
        var resolvedBindings = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new Dictionary<string, Binding>(StringComparer.Ordinal);
        var recoveryBarriers = new Dictionary<string, (long Through, string Setup, HashSet<string> Keys)>(StringComparer.Ordinal);
        var bindWaiters = new Dictionary<string, List<TaskCompletionSource<bool>>>(StringComparer.Ordinal);
        using var pendingBindings = new PendingBindingOwner(bindWaiters);
        void AwaitReport(Binding b, TaskCompletionSource<bool> completion)
        {
            string key = ReportKey(b);
            if (!bindWaiters.TryGetValue(key, out var waiters)) bindWaiters[key] = waiters = [];
            waiters.Add(completion);
        }
        var bindingReportOrder = new Dictionary<string, long>(StringComparer.Ordinal);
        long reportOrder = 0;
        static string ReportKey(Binding b) => b.Key + ":" + b.Profile + ":" + b.UserHash;
        string configStamp = "";
        long? inventoryAt = null, reconcileAt = null, readyAt = null, libraryAt = null;
        long? oldestStamp = null, uncoveredStamp = Mono;
        string? oldestId = null;
        int emptyPolls = 0;
        bool readyRequested = false, inventoryComplete = false;
        HashSet<string> updated = new(StringComparer.Ordinal);
        bool Due(long? at, double seconds) => at is null || timeProvider.GetElapsedTime(at.Value).TotalSeconds >= seconds;
        await IterationLoop.Run(async () =>
        {
            long slice = Mono;
            engine.BudgetAvailable = () => timeProvider.GetElapsedTime(slice) < TimeSpan.FromSeconds(2);
            long now = Now; var config = configuration();
            var credentials = Credentials.Available(config, WallNow);
            engine.PruneSetups(credentials.Keys.ToHashSet(StringComparer.Ordinal));
            bool configured = config.Enabled && !string.IsNullOrWhiteSpace(config.Secret);
            string stamp = Identity.Hash(config.Enabled + ":" + config.RelayOrigin + ":" + config.Secret);
            void Uncover(string cause, string? folder = null)
            {
                SetError(cause); UncoverLibraries(folder); uncoveredStamp ??= Mono; reconciliation.Request();
                if (configured) engine.BeginRecovery(Identity.Hash(config.Secret), now);
            }
            if (Math.Abs(WallNow - now) > 120)
            {
                long corrected = WallNow, delta = corrected - now;
                pendingClockCorrection = new(Identity.EventId(), journal.State.Seq, journal.State.Recoveries.ToDictionary(p => p.Key, p => (p.Value.Id, Math.Max(0, p.Value.CutoffAt + delta))));
                lock (coverageGate) { Volatile.Write(ref utcAnchor, new(corrected, Mono)); clockEpoch++; UncoverLibraries(); }
                now = corrected;
                PersistClockCorrection(journal);
                lanes.NewSession(); engine.InvalidateAudienceCursors(); scan = null; inventoryComplete = false; inventoryAt = null;
                Uncover("reconcileFailed"); reconcileAt = null;
            }
            if (stamp != configStamp)
            {
                configStamp = stamp; Volatile.Write(ref setupHash, Identity.Hash(config.Secret));
                Volatile.Write(ref enabled, configured); inventoryAt = null; reconcileAt = null;
                schedules.Clear(); inventoryComplete = false; Uncover("none");
            }
            long acknowledgedDrops = Interlocked.Read(ref reportedDroppedTotal);
            if (acknowledgedDrops > journal.State.Health.ReportedDropped && acknowledgedDrops <= journal.State.Health.Dropped) journal.Commit(new Change { Kind = "health", Health = journal.State.Health with { ReportedDropped = acknowledgedDrops } });
            long lost = Interlocked.Exchange(ref channelDropped, 0);
            if (lost != 0) { Uncover("channelDropped"); journal.Commit(new Change { Kind = "health", Health = journal.State.Health with { Dropped = journal.State.Health.Dropped + lost } }); reconcileAt = null; scan = null; }
            for (int admitted = 0; admitted < 8 && timeProvider.GetElapsedTime(slice) < TimeSpan.FromMilliseconds(250) && engine.BudgetAvailable() && bindQueue.Reader.TryRead(out var bind); admitted++)
            {
                bindAdmittedThrough = bind.Sequence;
                if (bind.Completion.Task.IsCompleted) continue;
                foreach (var barrier in recoveryBarriers.Values)
                    if (bind.Sequence <= barrier.Through && bind.Binding.SetupHash == barrier.Setup) barrier.Keys.Add(ReportKey(bind.Binding));
                try
                {
                    var prior = candidates.GetValueOrDefault(bind.Binding.Key) ?? journal.State.Bindings.GetValueOrDefault(bind.Binding.Key);
                    if (prior is not null && (prior.UserHash != bind.Binding.UserHash || candidates.ContainsKey(prior.Key) && prior.Profile != bind.Binding.Profile))
                    { bind.Completion.TrySetResult(false); continue; }
                    // Candidates cannot replace a durable approval until the relay accepts them.
                    // Repeat approvals still require a fresh, rate-limited binding report.
                    if (prior is not null && prior.Profile == bind.Binding.Profile && !candidates.ContainsKey(prior.Key))
                    {
                        resolvedBindings.Remove(ReportKey(prior)); refreshBindings.Add(ReportKey(prior));
                        AwaitReport(prior, bind.Completion); continue;
                    }
                    candidates[bind.Binding.Key] = bind.Binding with { Sequence = journal.State.Seq + 1 };
                    resolvedBindings.Remove(ReportKey(bind.Binding));
                    reportedBindings.Remove(ReportKey(bind.Binding));
                    AwaitReport(bind.Binding, bind.Completion);
                }
                catch { bind.Completion.TrySetResult(false); throw; }
            }
            while (pendingSignals.Count < State.Capacity && channel.Reader.TryRead(out var signal)) pendingSignals.Enqueue(signal);
            int signals = Math.Min(250, pendingSignals.Count);
            for (int i = 0; i < signals && pendingSignals.Count != 0 && engine.BudgetAvailable(); i++)
            {
                var signal = pendingSignals.Dequeue();
                if (signal.Op is "add" or "update" && signal.Sequence < removedThrough.GetValueOrDefault(signal.Id)) { CompleteSignal(signal); continue; }
                bool retry = false;
                try
                {
                    string origin = signal.Origin;
                    if (signal.Op == "add")
                    {
                        if (journal.State.Notices.Values.Any(n => n.Op == "add" && n.Id == signal.Id && n.Life == engine.Life(signal.Id))) continue;
                        // Admit before any metadata call. AdvanceAdds owns evaluation and replay.
                        lanes.Invalidate(signal.Id);
                        if (!engine.QueueAdd(signal.Id, signal.Type!, signal.EventId, signal.At, signal.Setup, origin,
                            signal.ClockEpoch != Interlocked.Read(ref clockEpoch), signal.Coverage.Order(StringComparer.Ordinal).ToArray()))
                        { Uncover("channelDropped"); reconcileAt = null; scan = null; }
                        else scan?.Known.Add(signal.Id);
                        readyRequested = true;
                    }
                    else if (signal.Op == "rm")
                    {
                        removedThrough[signal.Id] = signal.Sequence; lanes.Invalidate(signal.Id); updated.Remove(signal.Id);
                        var removedFolders = journal.State.Index.TryGetValue(signal.Id, out var title) ? title.Folders : engine.HeldRemoval(signal.Id, signal.Setup)?.Folders;
                        if (removedFolders is null || removedFolders.Length == 0 || !removedFolders.All(signal.Coverage.Contains)) origin = "recovery";
                        engine.Remove(signal.Id, signal.EventId, signal.At, signal.Setup, origin, signal.ClockEpoch != Interlocked.Read(ref clockEpoch)); scan?.Known.Remove(signal.Id);
                    }
                    else if (signal.Op == "update")
                    {
                        if (updated.Add(signal.Id)) lanes.Invalidate(signal.Id);
                        if (journal.State.Notices.Values.Any(n => n.Id == signal.Id && n.Stage == "pendingAdd")) { engine.AdvanceAdds(now, code => { if (code == "requiredCallTimeout") Uncover(code); else SetError(code); }, signal.Id); readyRequested = true; }
                        else if (journal.State.Index.ContainsKey(signal.Id) && lanes.Read(signal.Id, signal.At, now) is { } item) engine.Index(item.Title);
                        updated.Remove(signal.Id);
                    }
                    else reconcileAt = null;
                }
                catch (LibraryPendingException) { pendingSignals.Enqueue(signal); retry = true; }
                catch (LibraryCallException) { Uncover("requiredCallTimeout"); pendingSignals.Enqueue(signal); retry = true; }
                finally { if (!retry) CompleteSignal(signal); }
            }
            var pendingIds = pendingSignals.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string id in removedThrough.Keys.Where(id => !pendingIds.Contains(id)).ToArray()) removedThrough.Remove(id);
            if (configured)
            {
                if (Due(inventoryAt, 120) && engine.BudgetAvailable())
                {
                    try
                    {
                        var view = lanes.Inventory();
                        var poll = InventoryRules.Evaluate(journal.State.Libraries.Values.ToArray(), view.Libraries, true, emptyPolls);
                        emptyPolls = poll.EmptyPolls;
                        if (poll.Complete) { lock (coverageGate) libraryRoots = view.Roots.Where(r => poll.Current.Any(l => l.Id == r.Id)).ToImmutableArray(); inventoryComplete = true; if (error == "inventoryIncomplete") SetError("none"); if (ReconcileInventory(journal, engine, poll, now, setupHash)) reconcileAt = null; }
                        else { inventoryComplete = false; Uncover("inventoryIncomplete"); }
                        inventoryAt = Mono;
                    }
                    catch (LibraryPendingException) { }
                    catch (Exception ex) when (ex is not (StatePersistenceException or OutOfMemoryException or OperationCanceledException)) { inventoryComplete = false; Uncover("inventoryIncomplete"); inventoryAt = Mono; }
                }
                if (Due(reconcileAt, 900)) { reconciliation.Request(); reconcileAt = Mono; }
                if (scan is null && !lanes.Stuck && Due(libraryAt, 5) && reconciliation.Next(journal.State.Libraries.Values) is { } folder)
                { scan = new(folder, journal.State.Seq, journal.State, Volatile.Read(ref covered).Contains(folder.Id), Interlocked.Read(ref coverageEpoch)); libraryAt = Mono; }
                if (scan is not null && engine.BudgetAvailable())
                {
                    try
                    {
                        scan.Ids ??= lanes.Ids(scan.Library.Id, scan.PassId);
                        scan.Present ??= scan.Ids.Select(id => id.ToString("N")).ToHashSet(StringComparer.Ordinal);
                        for (int i = 0; i < 1000 && scan.Position < scan.Ids.Count && engine.BudgetAvailable(); i++)
                        {
                            string id = scan.Ids[scan.Position].ToString("N");
                            var item = lanes.Read(id, now, now);
                            if (item is not null)
                            {
                                bool known = scan.Known.Contains(id);
                                if (scan.Library.BaselineComplete && !known && !HasSignalledAdd(id) && !journal.State.Health.IndexFull && !(journal.State.Tombstones.TryGetValue(id, out var tomb) && tomb.Seq > scan.StartSeq))
                                {
                                    string origin = scan.WasCovered && now - scan.Library.CoveredUntil <= 1800 && Volatile.Read(ref covered).Contains(scan.Library.Id) ? "live" : "recovery";
                                    engine.QueueAdd(id, item.Title.Type, Identity.EventId(), now, setupHash, origin); scan.Known.Add(id); readyRequested = true;
                                }
                                if (!scan.Library.BaselineComplete || journal.State.Index.ContainsKey(id)) engine.Index(item.Title, scan.StartSeq);
                            }
                            scan.Position++;
                        }
                        if (scan.Position == scan.Ids.Count)
                        {
                            // The complete ids census is definitive membership, never an empty timeout result.
                            scan.Removals ??= journal.State.Index.Values.Where(t => t.Folders.Contains(scan.Library.Id) && !scan.Present.Contains(t.Id)).ToArray();
                            while (scan.RemovalPosition < scan.Removals.Length && engine.BudgetAvailable())
                            {
                                var title = scan.Removals[scan.RemovalPosition];
                                if (journal.State.Index.TryGetValue(title.Id, out var currentTitle) && currentTitle.Folders.Contains(scan.Library.Id) && !HasSignalledAdd(title.Id))
                                {
                                    var item = lanes.Read(title.Id, now, now);
                                    if (item is null) engine.Remove(title.Id, Identity.EventId(), now, setupHash, scan.WasCovered && now - scan.Library.CoveredUntil <= 1800 && Volatile.Read(ref covered).Contains(scan.Library.Id) ? "live" : "recovery");
                                    else engine.Index(item.Title, scan.StartSeq);
                                }
                                scan.RemovalPosition++;
                            }
                            if (scan.RemovalPosition != scan.Removals.Length) throw new LibraryPendingException();
                            if (journal.State.Libraries.TryGetValue(scan.Library.DirectoryKey, out var current))
                                journal.Commit(new Change { Kind = "library", Library = current with { BaselineComplete = true, CoveredUntil = now } });
                            if (!TryCover(scan.Library.Id, scan.Epoch)) reconciliation.Request();
                            scan = null;
                            if (inventoryComplete && !lanes.Stuck && error is "libraryStalled" or "requiredCallTimeout" or "reconcileFailed" or "inventoryIncomplete" or "channelDropped") SetError("none");
                        }
                    }
                    catch (LibraryPendingException) { }
                    catch (Exception ex) when (ex is not (StatePersistenceException or OutOfMemoryException or OperationCanceledException))
                    { Uncover(ex is LibraryCallException ? "requiredCallTimeout" : "reconcileFailed", scan?.Library.Id); scan = null; }
                }
                if ((Due(readyAt, 30) || readyRequested) && engine.BudgetAvailable())
                {
                    engine.ResetPendingWork(); bool rosterPending = false;
                    try
                    {
                        lanes.Users(); tooManyUsers = false;
                        if (error == "tooManyUsers") SetError("none");
                        engine.AdvanceAdds(now, code => { if (code == "requiredCallTimeout") Uncover(code); else SetError(code); });
                        engine.AdvanceRemovals(now, code => { if (code == "requiredCallTimeout") Uncover(code); else SetError(code); });
                    }
                    catch (LibraryPendingException) { rosterPending = true; }
                    catch (TooManyUsersException) { SetError("tooManyUsers"); }
                    catch (LibraryCallException) { Uncover("requiredCallTimeout"); }
                    catch (Exception ex) when (ex is not (StatePersistenceException or OutOfMemoryException or OperationCanceledException)) { SetError("adapterUnavailable"); }
                    engine.Sweep(now); readyAt = Mono; readyRequested = rosterPending || engine.WorkPending;
                }
                bool censusComplete = inventoryComplete && scan is null && journal.State.Libraries.Values.All(l => Volatile.Read(ref covered).Contains(l.Id));
                if (censusComplete) uncoveredStamp = null; else uncoveredStamp ??= Mono;
                var pendingSetups = pendingSignals.Where(s => s.Op == "add").Select(s => s.Setup).ToHashSet(StringComparer.Ordinal);
                var recoveringSetups = journal.State.Notices.Values.Where(n => n.Stage == "pendingAdd" && (n.Origin == "recovery" || n.Folded || n.EnqueuedSeq <= journal.State.Health.QuietThroughSeq)).Select(n => n.SetupHash).ToHashSet(StringComparer.Ordinal);
                // Freeze only candidates already present when this outage becomes eligible.
                // Later churn cannot extend the finite approval barrier.
                var liveRecoveries = journal.State.Recoveries.Values.Where(r => !r.Emitted).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                foreach (string id in recoveryBarriers.Keys.Where(id => !liveRecoveries.Contains(id)).ToArray()) recoveryBarriers.Remove(id);
                foreach (var recovery in journal.State.Recoveries.Values.Where(r => !r.Emitted && r.HasAddition))
                    if ((censusComplete && !pendingSetups.Contains(recovery.SetupHash) && !recoveringSetups.Contains(recovery.SetupHash)
                        || recovery.Complete || now - recovery.CutoffAt >= 600) && !recoveryBarriers.ContainsKey(recovery.Id))
                        recoveryBarriers[recovery.Id] = (Interlocked.Read(ref bindSequence), recovery.SetupHash,
                            candidates.Values.Where(b => b.SetupHash == recovery.SetupHash).Select(ReportKey).ToHashSet(StringComparer.Ordinal));
                foreach (var hash in credentials.Keys) if (!schedules.ContainsKey(hash)) schedules[hash] = new();
                while (sentAt.TryPeek(out long prior) && timeProvider.GetElapsedTime(prior) >= TimeSpan.FromMinutes(1)) sentAt.Dequeue();
                while (bindingSentAt.TryPeek(out long bindingAt) && timeProvider.GetElapsedTime(bindingAt) >= TimeSpan.FromMinutes(1)) bindingSentAt.Dequeue();
                foreach (string key in bindWaiters.Keys.ToArray())
                {
                    bindWaiters[key].RemoveAll(w => w.Task.IsCompleted);
                    if (bindWaiters[key].Count == 0) bindWaiters.Remove(key);
                }
                foreach (var b in candidates.Values.ToArray())
                    if (!bindWaiters.ContainsKey(ReportKey(b))) candidates.Remove(b.Key);
                if (Math.Max(Math.Max(reportedBindings.Count, refreshBindings.Count), bindingReportOrder.Count) > journal.State.Bindings.Count + 64)
                {
                    var retainedReports = journal.State.Bindings.Values.Select(ReportKey).ToHashSet(StringComparer.Ordinal);
                    reportedBindings.IntersectWith(retainedReports); refreshBindings.IntersectWith(retainedReports); resolvedBindings.IntersectWith(retainedReports);
                    foreach (string key in bindingReportOrder.Keys.Where(k => !retainedReports.Contains(k)).ToArray()) bindingReportOrder.Remove(key);
                }
                var bindingGroup = journal.State.Bindings.Values.Where(b => !candidates.ContainsKey(b.Key)).Concat(candidates.Values)
                    .Where(b => credentials.ContainsKey(b.SetupHash) && (!reportedBindings.Contains(ReportKey(b)) && !resolvedBindings.Contains(ReportKey(b)) || refreshBindings.Contains(ReportKey(b))))
                    .OrderBy(b => bindingReportOrder.GetValueOrDefault(ReportKey(b))).ThenBy(b => b.Sequence).ThenBy(b => b.Key, StringComparer.Ordinal)
                    .GroupBy(b => b.SetupHash).Where(g => schedules[g.Key].Due(timeProvider)).OrderBy(g => schedules[g.Key].LastAttempt).FirstOrDefault();
                bool eventsWaiting = journal.State.Notices.Values.Any(n => n.Stage == "outbox");
                if (bindingGroup is not null && sentAt.Count < 20 && (!eventsWaiting || bindingSentAt.Count < 10) && engine.BudgetAvailable())
                {
                    var report = bindingGroup.Take(64).ToArray();
                    byte[] body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { v = 3, server = host.SystemId, bindings = report.Select(b => new { registration = b.Registration, profile = b.Profile, userHash = b.UserHash }) });
                    sentAt.Enqueue(Mono); bindingSentAt.Enqueue(Mono); schedules[bindingGroup.Key].LastAttempt = Mono;
                    foreach (var b in report) bindingReportOrder[ReportKey(b)] = ++reportOrder;
                    long reportStarted = Mono;
                    var result = await relay.Post(config.RelayOrigin, Identity.ServerKey(host.SystemId), credentials[bindingGroup.Key], "bindings", body, [], ct);
                    // Network waiting consumes the HTTP deadline, not the loop's bounded processing budget.
                    slice += Mono - reportStarted;
                    if (result.Success && result.CurrentRegistrations is { } roster)
                    {
                        // Absence from the authenticated roster resolves a stale summary prerequisite
                        // without granting approval or deleting its retained tuple before capacity pressure.
                        foreach (var retained in journal.State.Bindings.Values.Where(b => b.SetupHash == bindingGroup.Key))
                            if (!roster.Contains(Identity.RegistrationRosterKey(retained.SetupHash, retained.Registration))) resolvedBindings.Add(ReportKey(retained));
                    }
                    foreach (var b in report)
                    {
                        if (result.Success)
                        {
                            refreshBindings.Remove(ReportKey(b));
                            if (!result.Accepted.Contains(b.Registration)) resolvedBindings.Add(ReportKey(b));
                        }
                        bool candidate = candidates.Remove(b.Key);
                        var currentConfig = configuration();
                        bool currentSetup = currentConfig.Enabled && Identity.Hash(currentConfig.Secret) == b.SetupHash;
                        bool accepted = result.Success && result.Accepted.Contains(b.Registration) && currentConfig.Enabled
                            && (candidate ? currentSetup : Credentials.Available(currentConfig, WallNow).ContainsKey(b.SetupHash));
                        if (candidate && accepted)
                        {
                            var retained = journal.State.Bindings.Values.Where(old => old.SetupHash == b.SetupHash).ToArray();
                            List<Change> changes = [];
                            if (!journal.State.Bindings.ContainsKey(b.Key) && retained.Length >= 500)
                            {
                                var oldest = retained.OrderBy(old => result.CurrentRegistrations!.Contains(Identity.RegistrationRosterKey(old.SetupHash, old.Registration)) ? 1 : 0)
                                    .ThenBy(old => old.Sequence).ThenBy(old => old.BoundAt).ThenBy(old => old.Key, StringComparer.Ordinal).First();
                                changes.Add(new() { Kind = "unbinding", Key = oldest.Key });
                            }
                            changes.Add(new() { Kind = "binding", Binding = b with { Sequence = journal.State.Seq + 1 } });
                            journal.Commit(changes.ToArray());
                        }
                        if (accepted) { reportedBindings.Add(ReportKey(b)); resolvedBindings.Remove(ReportKey(b)); refreshBindings.Remove(ReportKey(b)); }
                        if (bindWaiters.Remove(ReportKey(b), out var waiters))
                            foreach (var waiter in waiters) waiter.TrySetResult(accepted && currentSetup);
                    }
                    if (!result.Success) schedules[bindingGroup.Key].Delay(timeProvider, ProbeDelay(result, ++schedules[bindingGroup.Key].Attempts));
                }
                foreach (string hash in credentials.Keys)
                {
                    var recovery = journal.State.Recoveries.GetValueOrDefault(hash);
                    bool waiting = recovery is not null && recoveryBarriers.TryGetValue(recovery.Id, out var barrier)
                        && (bindAdmittedThrough < barrier.Through || candidates.Values.Any(b => barrier.Keys.Contains(ReportKey(b))));
                    engine.AdvanceRecovery(hash, now, censusComplete && !pendingSetups.Contains(hash) && !recoveringSetups.Contains(hash), waiting);
                }
                // A summary waits for its profile's approvals present at creation. Later registration
                // churn cannot move that finite barrier; legacy events do not depend on bindings.
                var groups = engine.SubmittableNotices().Where(n => n.Stage == "outbox" && (n.Op != "summary" || journal.State.Recoveries.Values.Any(r => r.Id == n.OutageId && r.Emitted) &&
                        !journal.State.Bindings.Values.Any(b => b.SetupHash == n.SetupHash && b.Profile == n.Profile && b.Sequence <= n.EnqueuedSeq && !reportedBindings.Contains(ReportKey(b)) && !resolvedBindings.Contains(ReportKey(b)))) &&
                        !Rules.Expired(n, now) && credentials.ContainsKey(n.SetupHash))
                    .GroupBy(n => n.SetupHash).ToArray();
                // Fixed one-second micro-batches start with the first eligible notice per setup.
                // Later signals/readiness cannot slide the deadline; backoff and the shared
                // request limiter still control handoff. The loop awaits every POST serially.
                var readySetups = groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
                foreach (string hash in batchStarted.Keys.Where(hash => !readySetups.Contains(hash)).ToArray()) batchStarted.Remove(hash);
                foreach (var readyGroup in groups) batchStarted.TryAdd(readyGroup.Key, Mono);
                var group = groups.Where(g => schedules[g.Key].Due(timeProvider) && Due(batchStarted[g.Key], 1))
                    .OrderBy(g => schedules[g.Key].LastAttempt).FirstOrDefault();
                var pending = group?.OrderBy(n => n.EnqueuedSeq <= journal.State.Health.QuietThroughSeq ? 0 : 1).ThenBy(n => n.QueuedAt).ToArray() ?? [];
                if (!tooManyUsers && pending.Length != 0 && engine.BudgetAvailable())
                {
                    var schedule = schedules[group!.Key];
                    while (sentAt.TryPeek(out long sent) && timeProvider.GetElapsedTime(sent) >= TimeSpan.FromMinutes(1)) sentAt.Dequeue();
                    while (eventSentAt.TryPeek(out long sentEvent) && timeProvider.GetElapsedTime(sentEvent) >= TimeSpan.FromMinutes(1)) eventSentAt.Dequeue();
                    // Preserve the binding lane's existing ten-call allowance so frequent
                    // event batches cannot strand approvals past their 15-second deadline.
                    if (sentAt.Count < 20 && eventSentAt.Count < 10)
                    {
                        try
                        {
                            var users = lanes.Users(); List<Notice> fresh = [];
                            foreach (var n in pending.Take(50))
                            {
                                if (!engine.BudgetAvailable()) break;
                                try { if (engine.Freshen(n, now) is { } evaluated) fresh.Add(evaluated); }
                                catch (LibraryPendingException) { }
                                catch (LibraryCallException) { Uncover("requiredCallTimeout"); }
                            }
                            long handoffAt = Now;
                            for (int i = fresh.Count - 1; i >= 0; i--)
                            {
                                if (fresh[i].Op == "summary" || fresh[i].Quiet || Rules.Audible(fresh[i], handoffAt)) continue;
                                try { if (engine.Freshen(fresh[i], handoffAt) is { } final) fresh[i] = final; else fresh.RemoveAt(i); }
                                catch (LibraryPendingException) { fresh.RemoveAt(i); }
                                catch (LibraryCallException) { Uncover("requiredCallTimeout"); fresh.RemoveAt(i); }
                            }
                            if (Wire.Split(host.SystemId, users, fresh).FirstOrDefault() is { } batch)
                            {
                                sentAt.Enqueue(Mono); schedule.LastAttempt = Mono;
                                eventSentAt.Enqueue(Mono);
                                batchStarted.Remove(group.Key);
                                var result = await relay.Post(config.RelayOrigin, Identity.ServerKey(host.SystemId), credentials[group.Key], "events", batch.Body, batch.Events.Select(n => n.EventId), ct);
                                if (result.Success)
                                { engine.Acknowledge(result.Accepted, Now); SetError("none"); schedule.Attempts = 0; schedule.Delay(timeProvider, TimeSpan.FromSeconds(result.Accepted.Count == batch.Events.Length ? 0 : 30)); }
                                else
                                { SetError(result.ErrorCode); schedule.Delay(timeProvider, ProbeDelay(result, ++schedule.Attempts)); }
                            }
                        }
                        catch (LibraryPendingException) { }
                        catch (TooManyUsersException) { SetError("tooManyUsers"); }
                        catch (InvalidDataException) { SetError("invalidConfiguration"); schedule.Delay(timeProvider, TimeSpan.FromMinutes(15)); }
                    }
                }
            }
            if (lanes.Stuck) { SetError(lanes.Stalled ? "jellyfinStalled" : "libraryStalled"); UncoverLibraries(); uncoveredStamp ??= Mono; }
            int outbox = journal.State.Notices.Values.Count(n => n.Stage == "outbox");
            if (outbox == 0) { oldestStamp = null; oldestId = null; }
            else
            {
                var oldest = journal.State.Notices.Values.Where(n => n.Stage == "outbox").MinBy(n => n.ReadyAt)!;
                long age = Math.Clamp(now - oldest.ReadyAt, 0, 86400);
                long observed = Mono - age * timeProvider.TimestampFrequency;
                if (oldestId != oldest.EventId) { oldestStamp = observed; oldestId = oldest.EventId; }
            }
            var before = Volatile.Read(ref snapshot);
            Volatile.Write(ref snapshot, before with { SetupHash = setupHash, ProgressStamp = Mono, ErrorCode = error, StorageUnavailable = false, RestartRequired = false, OutboxDepth = outbox, OldestReadyStamp = oldestStamp, UncoveredStamp = uncoveredStamp,
                Dropped = journal.State.Health.Dropped - journal.State.Health.ReportedDropped, DroppedTotal = journal.State.Health.Dropped, LastAckAt = journal.State.Health.LastAckAt, IndexFull = journal.State.Health.IndexFull, TooManyUsers = tooManyUsers, IndexCount = journal.State.Index.Count, StateBytes = journal.DiskBytes,
                BaselineLibrariesRemaining = journal.State.Libraries.Values.Count(l => !l.BaselineComplete), Bindings = journal.State.Bindings.Values.ToImmutableArray() });
            journal.MaybeCompact(); healthy();
        }, SetError, ct, (pause, token) => Task.Delay(lanes.Pending && pause == TimeSpan.FromSeconds(1) ? TimeSpan.FromMilliseconds(25) : pause, timeProvider, token));
    }
    public override void Dispose() { base.Dispose(); lanes.Dispose(); diagnostics.Dispose(); }
    internal static TimeSpan ProbeDelay(SendResult result, int attempts) => result.Retryable ? Responses.RetryDelay(attempts, result.RetryAfter, 0.5) : result.RetryAfter is { } hint && hint > TimeSpan.FromMinutes(15) ? hint : TimeSpan.FromMinutes(15);
    private static bool ReconcileInventory(Journal journal, Engine engine, InventoryResult poll, long now, string setup)
    {
        bool changed = false;
        foreach (var removed in poll.Removed)
        {
            changed = true;
            if (journal.State.Notices.Values.Count(n => n.Stage == "outbox") >= State.Capacity)
                engine.EvictOutbox();
            journal.Commit(new Change { Kind = "unlibrary", Key = removed.DirectoryKey },
                new() { Kind = "unroute", Key = removed.Id },
                new() { Kind = "notice", Notice = new()
                {
                    EventId = Identity.EventId(), Op = "rmlibrary", Stage = "outbox", Folders = [removed.Id],
                    At = now, QueuedAt = now, ReadyAt = now, FirstSeenAt = now, Origin = "recovery", Quiet = true, Life = engine.Life(removed.Id) + 1, SetupHash = setup
                } },
                new() { Kind = "remove", Tombstone = new(removed.Id, journal.State.Seq + 1, now, engine.Life(removed.Id) + 1) });
        }
        foreach (var current in poll.Current)
        {
            var old = journal.State.Libraries.GetValueOrDefault(current.DirectoryKey) ??
                journal.State.Libraries.Values.FirstOrDefault(l => !poll.Current.Any(n => n.DirectoryKey == l.DirectoryKey) && l.Locations.Length != 0 && l.Locations.SequenceEqual(current.Locations));
            if (old is null || old.DirectoryKey != current.DirectoryKey || old.Enabled != current.Enabled || !old.Locations.SequenceEqual(current.Locations) || old.Id != current.Id)
            {
                List<Change> changes = [];
                if (old is not null && old.DirectoryKey != current.DirectoryKey) changes.Add(new() { Kind = "unlibrary", Key = old.DirectoryKey });
                if (old is not null && old.Id != current.Id) changes.Add(new() { Kind = "reroute", Key = old.Id + ":" + current.Id });
                changes.Add(new() { Kind = "library", Library = current }); journal.Commit(changes.ToArray()); changed = true;
            }
        }
        return changed;
    }
    private sealed class PendingBindingOwner(Dictionary<string, List<TaskCompletionSource<bool>>> waiters) : IDisposable
    {
        public void Dispose()
        {
            foreach (var group in waiters.Values)
                foreach (var waiter in group) waiter.TrySetResult(false);
        }
    }

    private sealed class SendSchedule
    {
        private long? at;
        private TimeSpan wait;
        public long LastAttempt { get; set; }
        public int Attempts { get; set; }
        public bool Due(TimeProvider clock) => at is null || clock.GetElapsedTime(at.Value) >= wait;
        public void Delay(TimeProvider clock, TimeSpan delay) { at = clock.GetTimestamp(); wait = delay; }
    }
    private sealed class Scan(Library library, long seq, State state, bool wasCovered, long epoch)
    {
        public string PassId { get; } = Identity.EventId();
        public Library Library { get; } = library;
        public IReadOnlyList<Guid>? Ids { get; set; }
        public HashSet<string>? Present { get; set; }
        public Title[]? Removals { get; set; }
        public int RemovalPosition { get; set; }
        public HashSet<string> Known { get; } = state.DiscoveryKnowledge();
        public long StartSeq { get; } = seq;
        public bool WasCovered { get; } = wasCovered;
        public long Epoch { get; } = epoch;
        public int Position { get; set; }
    }
}
