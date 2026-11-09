using System.Reflection;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Jellyfin.Extensions.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class VersionHygieneTests
{
    private static readonly Guid Ours = Guid.Parse(Plugin.PluginId);
    private static readonly Version Running = new(1, 1, 1, 1);

    // Records DisablePlugin and mimics Jellyfin 10.11.8 ProcessAlternative, which
    // leaves the disabled record's in-memory status at Restart.
    public class FakeManager : DispatchProxy
    {
        internal List<LocalPlugin> Installed = [];
        internal List<LocalPlugin> Disabled = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Plugins": return Installed;
                case "DisablePlugin": var p = (LocalPlugin)args![0]!; Disabled.Add(p); p.Manifest.Status = PluginStatus.Restart; return null;
                default: throw new InvalidOperationException("Hygiene must not call " + method.Name);
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "jsn-hygiene-" + Guid.NewGuid().ToString("N"));
        internal readonly IPluginManager Manager = DispatchProxy.Create<IPluginManager, FakeManager>();
        internal FakeManager Fake => (FakeManager)(object)Manager;
        internal readonly HashSet<string> Locked = new(StringComparer.Ordinal);
        internal string Folder(string name, string? version, Guid? id = null, string status = "Active", bool dll = true)
        {
            string path = Path.Combine(Root, name); Directory.CreateDirectory(path);
            if (dll) File.WriteAllBytes(Path.Combine(path, "Jellyfin.Plugin.JellySeeNotifications.dll"), [1]);
            File.WriteAllText(Path.Combine(path, "LICENSE"), "MIT");
            if (version is not null)
                File.WriteAllText(Path.Combine(path, "meta.json"), new JsonObject { ["guid"] = (id ?? Ours).ToString(), ["name"] = "JellySee Notifications", ["version"] = version, ["status"] = status, ["changelog"] = "kept", ["targetAbi"] = "12.0.0.0" }.ToJsonString());
            return path;
        }
        internal void Run(string running) => new VersionHygiene(Manager, NullLogger<VersionHygiene>.Instance)
        {
            DeleteFile = f => { if (Locked.Contains(f)) throw new IOException("locked"); File.Delete(f); }
        }.Run(Ours, Running, running);
        internal static JsonObject Meta(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(path, "meta.json")))!;
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [Fact]
    public void UnlockedLeftoverOlderCopyIsRemoved()
    {
        using var f = new Fixture();
        string running = f.Folder(VersionHygiene.FolderPrefix + "1.1.1.1", "1.1.1.1");
        string stale = f.Folder(VersionHygiene.FolderPrefix + "1.0.1.1", "1.0.1.1", status: "Deleted");
        f.Run(running);
        Assert.False(Directory.Exists(stale));
        Assert.True(File.Exists(Path.Combine(running, "Jellyfin.Plugin.JellySeeNotifications.dll")));
        Assert.Empty(f.Fake.Disabled);
    }

    // Nori 2026-11-08: Windows kept 1.0.1.1's DLL locked across an in-process restart.
    [Fact]
    public void LockedOlderCopyIsStampedDisabledAndKeepsEveryFile()
    {
        using var f = new Fixture();
        string running = f.Folder(VersionHygiene.FolderPrefix + "1.1.1.1", "1.1.1.1");
        string stale = f.Folder(VersionHygiene.FolderPrefix + "1.0.1.1", "1.0.1.1", status: "Deleted");
        f.Locked.Add(Path.Combine(stale, "Jellyfin.Plugin.JellySeeNotifications.dll"));
        f.Run(running);
        var meta = Fixture.Meta(stale);
        Assert.Equal("Disabled", meta["status"]!.GetValue<string>());
        var parsed = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(stale, "meta.json")), JsonDefaults.Options)!;
        Assert.Equal((Ours, PluginStatus.Disabled, "1.0.1.1"), (parsed.Id, parsed.Status, parsed.Version));
        Assert.True(File.Exists(Path.Combine(stale, "LICENSE")));
        Assert.Empty(Directory.EnumerateFiles(stale, "meta.json.tmp.*"));
    }

    // The exact leftover found on Nori at 23:37: meta.json gone, locked DLL kept.
    // Jellyfin would auto-generate an Active manifest for it.
    [Fact]
    public void LockedDllOnlyFolderGetsADisabledManifest()
    {
        using var f = new Fixture();
        string running = f.Folder(VersionHygiene.FolderPrefix + "1.1.1.1", "1.1.1.1");
        string stale = f.Folder(VersionHygiene.FolderPrefix + "1.0.1.1", null);
        f.Locked.Add(Path.Combine(stale, "Jellyfin.Plugin.JellySeeNotifications.dll"));
        f.Run(running);
        var meta = Fixture.Meta(stale);
        Assert.Equal("Disabled", meta["status"]!.GetValue<string>());
        Assert.Equal(Ours, Guid.Parse(meta["guid"]!.GetValue<string>()));
        Assert.Equal("1.0.1.1", meta["version"]!.GetValue<string>());
        // Parsed with Jellyfin's own serializer options, as PluginManager.LoadManifest does.
        var parsed = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(stale, "meta.json")), JsonDefaults.Options)!;
        Assert.Equal(Ours, parsed.Id);
        Assert.Equal(PluginStatus.Disabled, parsed.Status);
        Assert.Equal("1.0.1.1", parsed.Version);
        Assert.Equal("JellySee Notifications", parsed.Name);
    }

    // A truncated meta.json (Jellyfin's SaveManifest is not atomic) makes Jellyfin
    // fall back to the MD5-id manifest; treat our folder as ours and repair it.
    [Theory]
    [InlineData("{\"guid\": \"467b")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"guid\": \"467b751b-71c7-4e3d-a688-8ef46193d251\", \"version\": \"1.0.1.1\", \"assemblies\": \"broken\"}")]
    public void CorruptManifestInOurFolderIsReplacedWithDisabled(string content)
    {
        using var f = new Fixture();
        string running = f.Folder(VersionHygiene.FolderPrefix + "1.1.1.1", "1.1.1.1");
        string stale = f.Folder(VersionHygiene.FolderPrefix + "1.0.1.1", null);
        File.WriteAllText(Path.Combine(stale, "meta.json"), content);
        f.Locked.Add(Path.Combine(stale, "Jellyfin.Plugin.JellySeeNotifications.dll"));
        f.Run(running);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(stale, "meta.json")), JsonDefaults.Options)!;
        Assert.Equal(Ours, parsed.Id);
        Assert.Equal(PluginStatus.Disabled, parsed.Status);
        Assert.Equal("1.0.1.1", parsed.Version);
    }

    // A copy the manager tracks is disabled through it and pinned to Disabled in
    // memory, so a catalog refresh (PopulateManifest) keeps Disabled even on 10.11.
    [Fact]
    public void TrackedOlderCopyIsDisabledInMemoryAndNeverDeleted()
    {
        using var f = new Fixture();
        string running = f.Folder(VersionHygiene.FolderPrefix + "1.1.1.1", "1.1.1.1");
        string stale = f.Folder(VersionHygiene.FolderPrefix + "1.0.1.1", "1.0.1.1", status: "Deleted");
        var record = new LocalPlugin(stale, true, new PluginManifest { Id = Ours, Name = "JellySee Notifications", Version = "1.0.1.1", Status = PluginStatus.Deleted });
        f.Fake.Installed.Add(record);
        f.Run(running);
        Assert.Same(record, Assert.Single(f.Fake.Disabled));
        Assert.Equal(PluginStatus.Disabled, record.Manifest.Status);
        Assert.True(File.Exists(Path.Combine(stale, "Jellyfin.Plugin.JellySeeNotifications.dll")));
    }

    [Fact]
    public void NeverTouchesRunningNewerForeignOrLinkedFolders()
    {
        using var f = new Fixture();
        string running = f.Folder(VersionHygiene.FolderPrefix + "1.1.1.1", "1.1.1.1");
        string newer = f.Folder(VersionHygiene.FolderPrefix + "1.2.0.1", "1.2.0.1", status: "Disabled");
        string foreign = f.Folder("Other Plugin_0.9.0.0", "0.9.0.0", id: Guid.NewGuid());
        string foreignNoMeta = f.Folder("Other Plugin_0.1.0.0", null);
        string state = Path.Combine(f.Root, "Jellyfin.Plugin.JellySeeNotifications", "v6"); Directory.CreateDirectory(state);
        File.WriteAllBytes(Path.Combine(state, "snapshot.bin"), [1]);
        string outside = Path.Combine(Path.GetTempPath(), "jsn-hygiene-link-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "meta.json"), new JsonObject { ["guid"] = Ours.ToString(), ["version"] = "1.0.0.0", ["status"] = "Active" }.ToJsonString());
        File.WriteAllBytes(Path.Combine(outside, "Jellyfin.Plugin.JellySeeNotifications.dll"), [1]);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(f.Root, VersionHygiene.FolderPrefix + "1.0.0.0"), outside);
            string Listing() => string.Join("|", Directory.EnumerateFileSystemEntries(f.Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Order().Select(e => e + ":" + (File.Exists(e) ? File.ReadAllText(e) : "")));
            string before = Listing();
            f.Run(running);
            Assert.Equal(before, Listing());
            Assert.True(File.Exists(Path.Combine(outside, "Jellyfin.Plugin.JellySeeNotifications.dll")));
            Assert.Equal("Active", JsonNode.Parse(File.ReadAllText(Path.Combine(outside, "meta.json")))!["status"]!.GetValue<string>());
        }
        finally { Directory.Delete(outside, true); }
        Assert.True(Directory.Exists(newer) && Directory.Exists(foreign) && Directory.Exists(foreignNoMeta) && Directory.Exists(running));
        Assert.Empty(f.Fake.Disabled);
    }

    // A link nested inside a stale folder must never lead deletion into the
    // running (or any other) folder.
    [Fact]
    public void NestedLinkInsideStaleFolderIsNeverFollowed()
    {
        using var f = new Fixture();
        string running = f.Folder(VersionHygiene.FolderPrefix + "1.1.1.1", "1.1.1.1");
        string stale = f.Folder(VersionHygiene.FolderPrefix + "1.0.1.1", "1.0.1.1", status: "Deleted");
        Directory.CreateSymbolicLink(Path.Combine(stale, "shared"), running);
        f.Locked.Add(Path.Combine(stale, "Jellyfin.Plugin.JellySeeNotifications.dll"));
        f.Run(running);
        Assert.True(File.Exists(Path.Combine(running, "Jellyfin.Plugin.JellySeeNotifications.dll")));
        Assert.Equal("Active", Fixture.Meta(running)["status"]!.GetValue<string>());
        Assert.Equal("Disabled", Fixture.Meta(stale)["status"]!.GetValue<string>());
        f.Locked.Clear();
        f.Run(running);
        Assert.False(Directory.Exists(stale));
        Assert.True(File.Exists(Path.Combine(running, "Jellyfin.Plugin.JellySeeNotifications.dll")));
    }

    [Fact]
    public void OlderSchemaRefusesToOpenWhenNewerMigratedStateExists()
    {
        using var temp = new TempDirectory();
        using (var current = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
            current.Commit(new Change { Kind = "health", Health = new(Dropped: 3) });
        string future = Path.Combine(temp.Path, "v" + (Journal.SchemaVersion + 1));
        Directory.CreateDirectory(future);
        // No marker, or an empty one, is an unfinished migration: v6 still opens.
        using (var noMarker = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
            Assert.Equal(3, noMarker.State.Health.Dropped);
        File.WriteAllBytes(Path.Combine(future, "migration.complete"), []);
        using (var emptyMarker = new Journal(temp.Path, Path.GetDirectoryName(temp.Path)!, default, TimeSpan.FromSeconds(3), versioned: true))
            Assert.Equal(3, emptyMarker.State.Health.Dropped);
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
