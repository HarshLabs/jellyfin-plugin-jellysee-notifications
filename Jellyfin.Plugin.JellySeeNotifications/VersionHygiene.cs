using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellySeeNotifications;

// Jellyfin marks an older version folder it cannot delete (Windows keeps the
// loaded DLL locked across an in-process restart) as Deleted. Every dashboard
// catalog refresh then rewrites that folder's meta.json through
// PluginManager.PopulateManifest, which keeps only Disabled and turns every
// other state, Deleted included, back into Active. If the newer folder is ever
// missing on a cold start, the stale version loads and diffs its pre-upgrade
// snapshot (seen on Jellyfin 12.4: a bogus "2 titles were removed" alert).
//
// The running version therefore disables every older copy of itself through
// the plugin manager, which updates the in-memory record and meta.json
// together. Disabled survives catalog refreshes and never loads. Jellyfin's
// own discovery still deletes the folder on the next cold start, once no
// process holds its DLL. We never delete it here: the manager keeps the record,
// and a catalog refresh writing into a vanished folder would throw.
internal sealed class VersionHygiene(IPluginManager plugins, ILogger<VersionHygiene> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var running = typeof(VersionHygiene).Assembly.GetName().Version;
            string runningDirectory = Path.GetDirectoryName(typeof(VersionHygiene).Assembly.Location) ?? "";
            if (running is null) return Task.CompletedTask;
            foreach (var stale in Stale(plugins.Plugins, Guid.Parse(Plugin.PluginId), running, runningDirectory))
            {
                try
                {
                    var previous = stale.Manifest.Status;
                    plugins.DisablePlugin(stale);
                    logger.LogInformation("[JellySee Notifications] Disabled older version {Version} (was {Status}) so it cannot be revived over {Running}", stale.Version, previous, running);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogWarning("[JellySee Notifications] Could not disable older version {Version}; exceptionType={ExceptionType}", stale.Version, ex.GetType().FullName);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("[JellySee Notifications] Version hygiene skipped; exceptionType={ExceptionType}", ex.GetType().FullName);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Only strictly older, unloaded copies of this plugin; never the running
    // folder, never a newer version, never anything already Disabled.
    internal static IReadOnlyList<LocalPlugin> Stale(IEnumerable<LocalPlugin> installed, Guid id, Version running, string runningDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string own = Normalize(runningDirectory);
        return installed.Where(p => p.Id.Equals(id) && p.Version < running && p.Instance is null
                && p.Manifest.Status != PluginStatus.Disabled
                && !string.IsNullOrEmpty(p.Path) && !string.Equals(Normalize(p.Path), own, comparison))
            .ToList();
    }

    private static string Normalize(string path) => path.Length == 0 ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
