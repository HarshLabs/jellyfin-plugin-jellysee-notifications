using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySeeNotifications;

// Why: a stale older copy of this plugin must never load again.
// Nori 2026-11-08 (reproduced on a disposable server, source-verified):
// 1. Windows keeps an older version's DLL locked across Jellyfin's in-process
//    restart. Discovery's recursive delete removes meta.json and LICENSE but not
//    the DLL, and if the folder was already Deleted, ChangePluginState writes no
//    new meta.json (PluginManager.DiscoverPlugins :850/:866). The folder is DLL-only.
// 2. On the next cold start LoadManifest auto-generates an Active manifest whose
//    id is the MD5 of the name (:781, 50935107-...). LocalPlugin.Compare orders
//    same-name plugins by id before version (LocalPlugin.cs:106), and that id
//    sorts above ours (467b751b-...). The stale copy wins discovery, Jellyfin
//    deletes the NEWER folder as "older" and loads the stale one, which then
//    sent a bogus removal summary from its pre-upgrade snapshot.
// Also: a copy the manager tracks is rewritten Active by catalog refreshes
// (PopulateManifest keeps only Disabled); on 10.11 DisablePlugin itself leaves
// the in-memory status at Restart, which the refresh also persists as Active.
//
// What runs at every start, for strictly older copies only (never the running
// folder, newer versions, other plugins, the state folder, or links):
// - Untracked folder: first stamp meta.json Disabled with our real guid (so
//   Jellyfin compares versions again and the newer copy always wins), then delete
//   its DLLs and, once none is left, the folder. If a DLL is locked the stamp
//   stays. Disabled, not Deleted, on purpose: discovery rewrites meta.json when it
//   changes Disabled to Deleted, so the folder can never become DLL-only again;
//   we re-stamp it each start and Jellyfin deletes it on the next cold start.
// - Tracked folder: never delete (a catalog refresh would write into the missing
//   folder and throw). Disable it through the manager and pin the in-memory
//   status to Disabled so a refresh keeps it.
// Limits: an orphan that already exists when Jellyfin first discovers plugins
// (left by a version without this code) is resolved by Jellyfin before any plugin
// runs and needs a one-time manual cleanup. A stamped copy that remains after the
// newer version is uninstalled while still locked shows as Disabled and loads
// only if an admin enables it.
internal sealed class VersionHygiene(IPluginManager plugins, ILogger<VersionHygiene> logger) : IHostedService
{
    internal const string FolderPrefix = "JellySee Notifications_";
    internal Action<string> DeleteFile { get; init; } = File.Delete;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var running = typeof(VersionHygiene).Assembly.GetName().Version;
            string runningDirectory = Path.GetDirectoryName(typeof(VersionHygiene).Assembly.Location) ?? "";
            if (running is not null && runningDirectory.Length != 0) Run(Guid.Parse(Plugin.PluginId), running, runningDirectory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("[JellySee Notifications] Version hygiene skipped; exceptionType={ExceptionType}", ex.GetType().FullName);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal void Run(Guid id, Version running, string runningDirectory)
    {
        string own = Normalize(runningDirectory);
        string? root = Path.GetDirectoryName(own);
        if (root is null || !Directory.Exists(root)) return;
        var tracked = plugins.Plugins.Where(p => !string.IsNullOrEmpty(p.Path)).ToList();
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string path = Normalize(directory);
                if (string.Equals(path, own, Comparison)) continue;
                var info = new DirectoryInfo(path);
                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (Identify(path, id) is not { } version || version >= running) continue;
                var record = tracked.FirstOrDefault(p => string.Equals(Normalize(p.Path), path, Comparison));
                if (record is not null) { DisableTracked(record, version, running); continue; }
                Clean(path, id, version, running);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning("[JellySee Notifications] Could not clean an older plugin folder; exceptionType={ExceptionType}", ex.GetType().FullName);
            }
        }
    }

    private void DisableTracked(LocalPlugin record, Version version, Version running)
    {
        if (record.Instance is not null) return;
        if (record.Manifest.Status != PluginStatus.Disabled) plugins.DisablePlugin(record);
        // 10.11 ProcessAlternative overwrites the stale record's in-memory status
        // with Restart; PopulateManifest would then persist Active again.
        record.Manifest.Status = PluginStatus.Disabled;
        logger.LogInformation("[JellySee Notifications] Disabled older version {Version} so it cannot be revived over {Running}", version, running);
    }

    private void Clean(string path, Guid id, Version version, Version running)
    {
        // Persist Disabled before anything destructive: a failure part-way (locked
        // DLL, unreadable child) must still leave a folder that cannot load. It also
        // gives a DLL-only folder our real guid. Without it Jellyfin auto-generates a
        // manifest whose MD5 guid sorts above ours (LocalPlugin.Compare compares ids
        // before versions), so the stale copy wins discovery, Jellyfin deletes the
        // newer folder as "older", and loads the stale one: the Nori sequence.
        StampDisabled(path, id, version);
        bool locked = false;
        foreach (string dll in Dlls(path))
        {
            try { DeleteFile(dll); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { locked = true; }
        }
        if (locked)
        {
            logger.LogInformation("[JellySee Notifications] Older version {Version} is locked; marked it Disabled so it cannot load over {Running}", version, running);
            return;
        }
        Directory.Delete(path, true);
        logger.LogInformation("[JellySee Notifications] Removed older version {Version} left behind by an earlier update", version);
    }

    // Never follow links below the folder: a nested junction could point at the
    // running or another plugin's folder.
    private static IEnumerable<string> Dlls(string path) => Directory.EnumerateFiles(path, "*.dll", new EnumerationOptions
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    });

    // Our copies only: meta.json with our guid, or (meta.json lost) our folder name.
    internal static Version? Identify(string path, Guid id)
    {
        string meta = Path.Combine(path, "meta.json");
        if (File.Exists(meta) && ReadObject(meta) is { } node && node["guid"] is not null)
        {
            if (node["guid"] is not JsonValue g || !g.TryGetValue<string>(out var guid) || !Guid.TryParse(guid, out var parsed) || parsed != id) return null;
            return node["version"] is JsonValue v && v.TryGetValue<string>(out var text) && Version.TryParse(text, out var declared) ? declared : null;
        }
        // No meta.json, one Jellyfin can't parse (non-atomic SaveManifest; it then
        // auto-generates the MD5-id manifest), or one without a guid: a folder with
        // our name is ours.
        string name = Path.GetFileName(path);
        return name.StartsWith(FolderPrefix, StringComparison.Ordinal) && Version.TryParse(name[FolderPrefix.Length..], out var folder) ? folder : null;
    }

    private static JsonObject? ReadObject(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    // Always a fresh, complete manifest: patching an existing one could keep a
    // field Jellyfin rejects (LoadManifest would then fall back to the Active
    // MD5-id manifest). Nothing in an old copy's metadata is worth keeping.
    internal static void StampDisabled(string path, Guid id, Version version)
    {
        string meta = Path.Combine(path, "meta.json");
        var node = new JsonObject
        {
            ["guid"] = id.ToString(),
            ["name"] = "JellySee Notifications",
            ["version"] = version.ToString(),
            ["targetAbi"] = "10.11.0.0",
            ["status"] = nameof(PluginStatus.Disabled),
            ["autoUpdate"] = false,
            ["assemblies"] = new JsonArray("Jellyfin.Plugin.JellySeeNotifications.dll"),
        };
        string text = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (File.Exists(meta) && File.ReadAllText(meta) == text) return;
        string temp = meta + ".tmp." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        File.WriteAllText(temp, text);
        File.Move(temp, meta, overwrite: true);
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
