using Jellyfin.Data;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.JellySeeNotifications.Core;

namespace Jellyfin.Plugin.JellySeeNotifications;

public interface IJellyfinCompat
{
    BaseItem? Get(string id);
    string? TitleType(BaseItem item);
    bool IsTitle(BaseItem item);
    Title Describe(BaseItem item, long seq, int life);
    bool Ready(BaseItem item, long queuedAt, long now);
    string? Series(BaseItem item);
    string[] Users(string serverKey);
    string[] Audience(BaseItem? item, string[] folders, string serverKey);
    Library[] Inventory();
    IReadOnlyList<Guid> Ids(string libraryId);
}

public sealed class JellyfinCompat(ILibraryManager library, IUserManager users, IServerApplicationPaths paths) : IJellyfinCompat
{
    private static readonly BaseItemKind[] Kinds = Wire.Types.Select(Enum.Parse<BaseItemKind>).ToArray();
    private IEnumerable<User> AllUsers()
    {
#if JELLYFIN_12
        return users.GetUsers();
#else
        return users.Users;
#endif
    }
    public BaseItem? Get(string id) => library.GetItemById(Guid.ParseExact(id, "N"));
    public string? TitleType(BaseItem item)
    {
        // Exact runtime types match Jellyfin's six title kinds, never Season/Folder/UserView.
        string name = item.GetType().Name;
        return Wire.Types.Contains(name) ? name : null;
    }
    public bool IsTitle(BaseItem item) => TitleType(item) is not null && !item.IsVirtualItem && item.SourceType == SourceType.Library;
    public Title Describe(BaseItem item, long seq, int life)
    {
        string[] folders = library.GetCollectionFolders(item).OfType<CollectionFolder>().Select(f => f.Id.ToString("N")).Distinct().Order().Take(4).ToArray();
        string? digest = item.ProviderIds.Count == 0 ? null : Identity.Hash(System.Text.Json.JsonSerializer.Serialize(item.ProviderIds.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new[] { p.Key.ToLowerInvariant(), p.Value })))[..16];
        return new(item.Id.ToString("N"), TitleType(item)!, folders, digest, seq, life);
    }
    public bool Ready(BaseItem item, long queuedAt, long now) => Rules.Ready(TitleType(item)!, item.ProviderIds.Count > 0, item.DateLastRefreshed != default, queuedAt, now);
    public string? Series(BaseItem item) => item is Episode e && e.SeriesId != Guid.Empty ? e.SeriesId.ToString("N") : null;
    private User[] BoundedUsers()
    {
        var result = AllUsers().Take(Wire.MaxUsers + 1).ToArray();
        if (result.Length > Wire.MaxUsers) throw new TooManyUsersException();
        return result;
    }
    public string[] Users(string serverKey) => BoundedUsers().Select(u => Identity.UserHash(serverKey, u.Id)).Order(StringComparer.Ordinal).ToArray();
    public string[] Audience(BaseItem? item, string[] folders, string key)
    {
        var routes = item is not null ? library.GetCollectionFolders(item).OfType<CollectionFolder>().ToArray() :
            folders.Select(Get).OfType<CollectionFolder>().ToArray();
        // No independent folder allowlist/blocklist predicate: Jellyfin defines precedence.
        return BoundedUsers().Where(u => !u.HasPermission(PermissionKind.IsDisabled) &&
            (item is null || item.IsVisibleStandalone(u)) && routes.Any(f => f.IsVisible(u)))
            .Select(u => Identity.UserHash(key, u.Id)).Order(StringComparer.Ordinal).ToArray();
    }
    public Library[] Inventory()
    {
        string root = paths.DefaultUserViewsPath;
        // EnumerationOptions defaults must not silently skip inaccessible entries.
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        string[] directories = Directory.GetDirectories(root, "*", options);
        var virtualFolders = library.GetVirtualFolders();
        if (directories.Length != virtualFolders.Count || directories.Length > 2000) throw new IOException("Incomplete library inventory");
        List<Library> inventory = [];
        foreach (var dir in directories)
        {
            var info = virtualFolders.SingleOrDefault(f => string.Equals(f.Name, Path.GetFileName(dir), StringComparison.Ordinal)) ?? throw new IOException("Incomplete library inventory");
            int links = Directory.GetFiles(dir, "*", options).Count(p => Path.GetExtension(p).Equals(".mblink", StringComparison.OrdinalIgnoreCase));
            if (info.Locations is null || links != info.Locations.Length || info.Locations.Length > 1024) throw new IOException("Incomplete library locations");
            string logicalId = Guid.TryParse(info.ItemId, out var id) ? id.ToString("N") : "";
            inventory.Add(new(Identity.Hash(Path.GetFileName(dir)), logicalId, info.Locations.Select(Identity.Hash).Order().ToArray(), info.LibraryOptions?.Enabled ?? true, false));
        }
        // Detect a mutation during either enumeration; never mistake a partial poll for a deletion.
        if (!directories.Order().SequenceEqual(Directory.GetDirectories(root, "*", options).Order())) throw new IOException("Unstable library inventory");
        return inventory.ToArray();
    }
    public IReadOnlyList<Guid> Ids(string libraryId)
    {
        if (Get(libraryId) is not CollectionFolder folder) throw new IOException("Library is not resolved");
        // GetItemIds bypasses GetItemsResult's ParentId -> ancestor conversion on both ABIs.
        // Match LibraryManager.GetTopParentIdsForQuery(CollectionFolder) explicitly.
        if (folder.PhysicalFolderIds.Length == 0) return [];
        return library.GetItemIds(new InternalItemsQuery
        {
            TopParentIds = folder.PhysicalFolderIds, Recursive = true, IncludeItemTypes = Kinds,
            IsVirtualItem = false, EnableTotalRecordCount = false
        });
    }
}
public sealed class TooManyUsersException : Exception;
