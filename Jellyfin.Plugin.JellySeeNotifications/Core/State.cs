using System.Text.Json;

namespace Jellyfin.Plugin.JellySeeNotifications.Core;

public sealed record Title(string Id, string Type, string[] Folders, string? ProviderDigest, long Seq, int Life);
public sealed record Lifecycle(string Id, int Life);
public sealed record Tombstone(string Id, long Seq, long At, int Life);
public sealed record Addition(string Id, string Type, string[] Folders, string? ProviderDigest, long At, string SetupHash = "");
// Directory and location fingerprints preserve rename detection without persisting filesystem paths.
public sealed record Library(string DirectoryKey, string Id, string[] Locations, bool Enabled, bool BaselineComplete);
public sealed record Health(long Dropped = 0, long? LastAckAt = null, bool IndexFull = false);
public sealed record Change
{
    public string Kind { get; init; } = "";
    public Notice? Notice { get; init; }
    public Title? Title { get; init; }
    public Lifecycle? Lifecycle { get; init; }
    public Tombstone? Tombstone { get; init; }
    public Addition? Addition { get; init; }
    public Library? Library { get; init; }
    public Health? Health { get; init; }
    public string? Key { get; init; }
}

public sealed class State
{
    public const int Capacity = 5000;
    public const int IndexCapacity = 200_000;
    public const int LifecycleCapacity = 1_000_000;
    public Dictionary<string, int> Lifecycles { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Notice> Notices { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Title> Index { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Tombstone> Tombstones { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Addition> Additions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Library> Libraries { get; } = new(StringComparer.Ordinal);
    public Health Health { get; private set; } = new();
    public long Seq { get; set; }
    public HashSet<string> DiscoveryKnowledge() => Index.Keys.Concat(Notices.Values
        .Where(n => n.Op == "add" && n.Id is not null && n.Life >= Lifecycles.GetValueOrDefault(n.Id))
        .Select(n => n.Id!)).ToHashSet(StringComparer.Ordinal);
    public void Transaction(Change[] changes, bool dryRun = false)
    {
        // Save only touched keys. Bulk and library-route rewrites are rare and touch a bounded set.
        var noticeKeys = changes.SelectMany(c => c.Kind == "bulk" ? Notices.Keys.Append(c.Notice!.EventId).ToArray() : c.Kind is "notice" ? new[] { c.Notice!.EventId } : c.Kind == "retire" ? new[] { c.Key! } : Array.Empty<string>()).Distinct().ToArray();
        if (changes.Any(c => c.Kind == "bulk"))
            noticeKeys = noticeKeys.Concat(Notices.Values.Where(n => n.Stage == "pendingRemove").Select(n => Identity.Hash(n.EventId + ":" + changes.First(c => c.Kind == "bulk").Notice!.Folders[0])[..32])).Distinct().ToArray();
        var titleKeys = changes.SelectMany(c => c.Kind is "unroute" or "reroute" ? Index.Keys.ToArray() : c.Title is not null ? new[] { c.Title.Id } : c.Tombstone is not null ? new[] { c.Tombstone.Id } : Array.Empty<string>()).Distinct().ToArray();
        var tombKeys = changes.Where(c => c.Kind is "remove" or "untombstone").Select(c => c.Tombstone?.Id ?? c.Key!).Distinct().ToArray();
        var addKeys = changes.Where(c => c.Kind is "addition" or "unaddition").Select(c => c.Addition?.Id ?? c.Key!).Distinct().ToArray();
        var libKeys = changes.Where(c => c.Kind is "library" or "unlibrary").Select(c => c.Library?.DirectoryKey ?? c.Key!).Distinct().ToArray();
        var lifeKeys = changes.Where(c => c.Kind is "remove" or "life").Select(c => c.Tombstone?.Id ?? c.Lifecycle!.Id).Distinct().ToArray();
        var lifeSaved = lifeKeys.ToDictionary(k => k, k => Lifecycles.TryGetValue(k, out int value) ? (int?)value : null);
        void RestoreLives() { foreach (var pair in lifeSaved) { if (pair.Value is { } life) Lifecycles[pair.Key] = life; else Lifecycles.Remove(pair.Key); } }
        var ns = Capture(Notices, noticeKeys); var ts = Capture(Index, titleKeys); var ds = Capture(Tombstones, tombKeys);
        var adds = Capture(Additions, addKeys); var libs = Capture(Libraries, libKeys); var health = Health;
        try { foreach (var c in changes) Apply(c); }
        catch { Restore(Notices, ns); Restore(Index, ts); Restore(Tombstones, ds); Restore(Additions, adds); Restore(Libraries, libs); Health = health; RestoreLives(); throw; }
        if (dryRun) { Restore(Notices, ns); Restore(Index, ts); Restore(Tombstones, ds); Restore(Additions, adds); Restore(Libraries, libs); Health = health; RestoreLives(); }
    }
    private static Dictionary<string, T?> Capture<T>(Dictionary<string, T> source, IEnumerable<string> keys) where T : class => keys.ToDictionary(k => k, k => source.GetValueOrDefault(k), StringComparer.Ordinal);
    private static void Restore<T>(Dictionary<string, T> target, Dictionary<string, T?> saved) where T : class
    { foreach (var pair in saved) { if (pair.Value is null) target.Remove(pair.Key); else target[pair.Key] = pair.Value; } }
    public void Apply(Change c)
    {
        switch (c.Kind)
        {
            case "notice":
                if (c.Notice is null) throw new InvalidDataException();
                Wire.Validate(c.Notice);
                if (!Digest(c.Notice.ProviderDigest)) throw new InvalidDataException();
                if (c.Notice.SetupHash is null || (c.Notice.SetupHash.Length != 0 && (c.Notice.SetupHash.Length != 64 || !c.Notice.SetupHash.All(Uri.IsHexDigit))) || c.Notice.Stage is not ("pendingAdd" or "pendingRemove" or "outbox")) throw new InvalidDataException();
                Notices[c.Notice.EventId] = c.Notice;
                if (Notices.Values.Count(n => n.Stage == "outbox") > Capacity || Notices.Values.Count(n => n.Stage != "outbox") > Capacity) throw new InvalidDataException("Queue cap");
                break;
            case "bulk":
                var bulk = c.Notice ?? throw new InvalidDataException();
                Wire.Validate(bulk);
                if (bulk.Op != "rmbulk" || !long.TryParse(c.Key, out long end)) throw new InvalidDataException();
                foreach (var member in Notices.Values.Where(n => n.Stage == "pendingRemove" && n.SetupHash == bulk.SetupHash && n.QueuedAt >= bulk.QueuedAt && n.QueuedAt <= end && n.Folders.Contains(bulk.Folders[0])).ToArray())
                {
                    Notices.Remove(member.EventId);
                    var rest = member.Folders.Where(f => f != bulk.Folders[0]).ToArray();
                    if (rest.Length != 0)
                    {
                        var replacement = member with { EventId = Identity.Hash(member.EventId + ":" + bulk.Folders[0])[..32], Folders = rest };
                        Notices[replacement.EventId] = replacement;
                    }
                }
                Notices[bulk.EventId] = bulk;
                break;
            case "retire": Notices.Remove(RequireId(c.Key)); break;
            case "title":
                var t = c.Title ?? throw new InvalidDataException();
                if (!Identity.IsId(t.Id) || !Wire.Types.Contains(t.Type) || t.Folders.Length > 4 || t.Folders.Any(f => !Identity.IsId(f)) || t.Life < 0 || t.Seq < 0 || !Digest(t.ProviderDigest)) throw new InvalidDataException();
                if (!Tombstones.TryGetValue(t.Id, out var dead) || dead.Seq < t.Seq) Index[t.Id] = t;
                if (Index.Count > IndexCapacity) throw new InvalidDataException("Index cap");
                break;
            case "life":
                var lifecycle = c.Lifecycle ?? throw new InvalidDataException();
                RequireId(lifecycle.Id); if (lifecycle.Life < 1) throw new InvalidDataException();
                if (!Lifecycles.ContainsKey(lifecycle.Id) && Lifecycles.Count >= LifecycleCapacity) throw new InvalidDataException("Lifecycle cap");
                Lifecycles[lifecycle.Id] = lifecycle.Life;
                break;
            case "remove":
                var tomb = c.Tombstone ?? throw new InvalidDataException();
                RequireId(tomb.Id); if (tomb.Life < 0 || tomb.Seq < 0 || tomb.At < 0) throw new InvalidDataException();
                if (!Lifecycles.ContainsKey(tomb.Id) && Lifecycles.Count >= LifecycleCapacity) throw new InvalidDataException("Lifecycle cap");
                Lifecycles[tomb.Id] = Math.Max(Lifecycles.GetValueOrDefault(tomb.Id), tomb.Life);
                Tombstones[tomb.Id] = tomb; Index.Remove(tomb.Id);
                if (Tombstones.Count > IndexCapacity) throw new InvalidDataException("Tombstone cap");
                break;
            case "untombstone": Tombstones.Remove(RequireId(c.Key)); break;
            case "addition":
                var a = c.Addition ?? throw new InvalidDataException();
                RequireId(a.Id); if (!Wire.Types.Contains(a.Type) || !Digest(a.ProviderDigest) || a.Folders.Length > 4 || a.Folders.Any(f => !Identity.IsId(f))) throw new InvalidDataException();
                Additions[a.Id] = a; if (Additions.Count > Capacity) throw new InvalidDataException("History cap"); break;
            case "unaddition": Additions.Remove(RequireId(c.Key)); break;
            case "library":
                var lib = c.Library ?? throw new InvalidDataException();
                RequireId(lib.Id);
                if (lib.DirectoryKey.Length != 64 || !lib.DirectoryKey.All(Uri.IsHexDigit) || lib.Locations.Length > 1024 || lib.Locations.Any(l => l.Length != 64 || !l.All(Uri.IsHexDigit))) throw new InvalidDataException();
                Libraries[lib.DirectoryKey] = lib;
                if (Libraries.Count > 2000) throw new InvalidDataException("Library cap"); break;
            case "reroute":
                var pair = (c.Key ?? "").Split(':');
                if (pair.Length != 2) throw new InvalidDataException();
                RequireId(pair[0]); RequireId(pair[1]);
                foreach (var title in Index.Values.Where(t => t.Folders.Contains(pair[0])).ToArray())
                    Index[title.Id] = title with { Folders = title.Folders.Select(f => f == pair[0] ? pair[1] : f).Distinct().Order().ToArray() };
                break;
            case "unroute":
                string folder = RequireId(c.Key);
                foreach (var title in Index.Values.Where(t => t.Folders.Contains(folder)).ToArray())
                {
                    string[] rest = title.Folders.Where(f => f != folder).ToArray();
                    if (rest.Length == 0) Index.Remove(title.Id);
                    else Index[title.Id] = title with { Folders = rest };
                }
                break;
            case "unlibrary": Libraries.Remove(c.Key ?? throw new InvalidDataException()); break;
            case "health": Health = c.Health ?? throw new InvalidDataException(); if (Health.Dropped < 0) throw new InvalidDataException(); break;
            default: throw new InvalidDataException("Unknown journal operation");
        }
    }
    private static bool Digest(string? d) => d is null || d.Length == 16 && d.All(Uri.IsHexDigit);
    private static string RequireId(string? id) => Identity.IsId(id) ? id! : throw new InvalidDataException();
    public IEnumerable<Change> Snapshot()
    {
        yield return new() { Kind = "health", Health = Health };
        foreach (var life in Lifecycles) yield return new() { Kind = "life", Lifecycle = new(life.Key, life.Value) };
        foreach (var t in Tombstones.Values) yield return new() { Kind = "remove", Tombstone = t };
        foreach (var t in Index.Values) yield return new() { Kind = "title", Title = t };
        foreach (var n in Notices.Values) yield return new() { Kind = "notice", Notice = n };
        foreach (var a in Additions.Values) yield return new() { Kind = "addition", Addition = a };
        foreach (var l in Libraries.Values) yield return new() { Kind = "library", Library = l };
    }
}

public static class Rules
{
    public static bool Ready(string type, bool providers, bool refreshed, long queuedAt, long now) =>
        now - queuedAt >= 900 || refreshed && (type is "Book" or "AudioBook" || providers);
    public static bool Stale(long at, long now) => at < now - 1200 || at > now + 120;
    public static bool Expired(Notice n, long now) => now - n.QueuedAt >= 86400;
    public static bool AudienceExpired(Notice n, long now) => now - n.QueuedAt >= 1800;
    public static bool Upgrade(Notice removal, Addition add) => removal.Type == add.Type &&
        !string.IsNullOrEmpty(removal.ProviderDigest) && removal.ProviderDigest == add.ProviderDigest &&
        removal.Folders.Order().SequenceEqual(add.Folders.Order()) && Math.Abs(removal.QueuedAt - add.At) <= 600 && removal.SetupHash == add.SetupHash;
    public static bool Bulk(IEnumerable<Notice> removals, string folder) => removals.Count(n => n.Op == "rm" && n.Folders.Contains(folder)) > 200;
}

public sealed record InventoryResult(bool Complete, int EmptyPolls, Library[] Current, Library[] Removed);
public static class InventoryRules
{
    public static InventoryResult Evaluate(IReadOnlyCollection<Library> previous, Library[] current, bool strictComplete, int emptyPolls)
    {
        if (!strictComplete || current.Select(l => l.DirectoryKey).Distinct().Count() != current.Length || current.Select(l => l.Id).Distinct().Count() != current.Length)
            return new(false, 0, [], []);
        if (current.Length == 0 && previous.Count != 0 && emptyPolls == 0) return new(false, 1, [], []);
        var unchanged = current.Where(n => previous.Any(o => o.DirectoryKey == n.DirectoryKey)).ToArray();
        var missing = previous.Where(o => !current.Any(n => n.DirectoryKey == o.DirectoryKey)).ToArray();
        var added = current.Where(n => !previous.Any(o => o.DirectoryKey == n.DirectoryKey)).ToArray();
        var renames = new Dictionary<string, Library>(StringComparer.Ordinal);
        foreach (var candidate in added)
        {
            var matches = missing.Where(o => candidate.Locations.Length != 0 && o.Locations.Order().SequenceEqual(candidate.Locations.Order())).ToArray();
            // Only a unique disappearing/new pair is a rename. An unchanged library sharing
            // the same paths must never hide the deletion of its neighbour.
            if (matches.Length == 1 && added.Count(n => n.Locations.Order().SequenceEqual(candidate.Locations.Order())) == 1)
                renames[candidate.DirectoryKey] = matches[0];
        }
        var removed = missing.Where(old => !renames.Values.Contains(old)).ToArray();
        var normalized = current.Select(n =>
        {
            var old = previous.FirstOrDefault(o => o.DirectoryKey == n.DirectoryKey) ?? renames.GetValueOrDefault(n.DirectoryKey);
            return old is null ? n : n with { Id = Identity.IsId(n.Id) ? n.Id : old.Id, BaselineComplete = old.BaselineComplete };
        }).ToArray();
        if (normalized.Any(n => !Identity.IsId(n.Id)) || normalized.Select(n => n.Id).Distinct().Count() != normalized.Length) return new(false, 0, [], []);
        return new(true, 0, normalized, removed);
    }
}
