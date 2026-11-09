using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class VersionHygieneTests
{
    private static readonly Guid Ours = Guid.Parse(Plugin.PluginId);

    private static LocalPlugin Installed(string root, string version, PluginStatus status, Guid? id = null) =>
        new(Path.Combine(root, "JellySee Notifications_" + version), true, new PluginManifest { Id = id ?? Ours, Name = "JellySee Notifications", Version = version, Status = status });

    // Nori 2026-11-08: 1.0.1.1 stayed on disk as Deleted after a locked-DLL
    // in-process restart, and a catalog refresh can rewrite it to Active.
    [Theory]
    [InlineData(PluginStatus.Deleted)]
    [InlineData(PluginStatus.Active)]
    [InlineData(PluginStatus.Superseded)]
    [InlineData(PluginStatus.Malfunctioned)]
    public void OlderCopiesThatCouldBeRevivedAreDisabled(PluginStatus status)
    {
        string root = Path.Combine(Path.GetTempPath(), "plugins");
        var running = Installed(root, "1.1.0.1", PluginStatus.Active);
        var stale = Installed(root, "1.0.1.1", status);
        var result = VersionHygiene.Stale([running, stale], Ours, new Version(1, 1, 0, 1), running.Path);
        Assert.Same(stale, Assert.Single(result));
    }

    [Fact]
    public void NeverTouchesRunningNewerDisabledOrForeignPlugins()
    {
        string root = Path.Combine(Path.GetTempPath(), "plugins");
        var running = Installed(root, "1.1.0.1", PluginStatus.Active);
        var newer = Installed(root, "1.2.0.1", PluginStatus.Active);
        var disabled = Installed(root, "1.0.0.1", PluginStatus.Disabled);
        var foreign = Installed(root, "0.9.0.0", PluginStatus.Active, Guid.NewGuid());
        var sameFolder = new LocalPlugin(running.Path + Path.DirectorySeparatorChar, true, new PluginManifest { Id = Ours, Version = "1.0.0.0", Status = PluginStatus.Active });
        var result = VersionHygiene.Stale([running, newer, disabled, foreign, sameFolder], Ours, new Version(1, 1, 0, 1), running.Path);
        Assert.Empty(result);
    }

    [Fact]
    public void OlderSchemaRefusesToOpenWhenNewerMigratedStateExists()
    {
        using var temp = new TempDirectory();
        using (var current = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
            current.Commit(new Change { Kind = "health", Health = new(Dropped: 3) });
        string future = Path.Combine(temp.Path, "v" + (Journal.SchemaVersion + 1));
        Directory.CreateDirectory(future);
        // An unfinished migration is not authoritative yet: v6 still opens.
        using (var stillCurrent = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
            Assert.Equal(3, stillCurrent.State.Health.Dropped);
        File.WriteAllBytes(Path.Combine(future, "migration.complete"), [1]);
        var error = Assert.ThrowsAny<Exception>(() => new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true));
        Assert.IsType<NewerStateException>(StateFailure.Cause(error));
        // Unrelated or older siblings never block.
        Directory.Delete(future, true);
        Directory.CreateDirectory(Path.Combine(temp.Path, "v5"));
        File.WriteAllBytes(Path.Combine(temp.Path, "v5", "migration.complete"), [1]);
        Directory.CreateDirectory(Path.Combine(temp.Path, "vNext"));
        using var reopened = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true);
        Assert.Equal(3, reopened.State.Health.Dropped);
    }
}
