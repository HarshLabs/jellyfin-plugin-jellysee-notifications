using System.Reflection;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;
using static Jellyfin.Plugin.JellySeeNotifications.Tests.ProtocolTests;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public class Stub : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = (_, _) => throw new NotSupportedException();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = Create<T, Stub>(); ((Stub)(object)proxy).Call = call; return proxy;
    }
}

public sealed class CompatTests
{
    private static IUserManager Users(IEnumerable<User>? users = null) => Stub.Create<IUserManager>((m, _) => m.Name is "GetUsers" or "get_Users" ? users ?? [] : throw new NotSupportedException(m.Name));
    [Fact] public void IdQueryUsesPhysicalRootsUnpagedAcrossDeepTenThousandItemLibrary()
    {
        var folder = new CollectionFolder { Id = Guid.ParseExact(Id(8), "N"), PhysicalFolderIds = [Guid.ParseExact(Id(9), "N")] };
        var ids = Enumerable.Range(1, 10001).Select(i => Guid.ParseExact(Id(i), "N")).ToArray();
        int queries = 0;
        var library = Stub.Create<ILibraryManager>((method, args) => method.Name switch
        {
            "GetItemById" => folder,
            "GetItemIds" => Query((InternalItemsQuery)args![0]!),
            _ => throw new NotSupportedException(method.Name)
        });
        object Query(InternalItemsQuery query)
        {
            queries++; Assert.Equal(folder.PhysicalFolderIds, query.TopParentIds); Assert.Equal(Guid.Empty, query.ParentId);
            Assert.True(query.Recursive); Assert.Null(query.Limit); Assert.Null(query.StartIndex); Assert.Null(query.User);
            Assert.Equal(6, query.IncludeItemTypes.Length); return ids;
        }
        var compat = new JellyfinCompat(library, Users(), Stub.Create<IServerApplicationPaths>((_, _) => "unused"));
        Assert.Equal(ids, compat.Ids(Id(8))); Assert.Equal(1, queries);
    }
    [Fact] public void InventoryDetectsSwallowedShortcutFailureAndMissingConfigurationDirectory()
    {
        using var temp = new TempDirectory(); string root = System.IO.Path.Combine(temp.Path, "libraries"); Directory.CreateDirectory(root);
        string folder = System.IO.Path.Combine(root, "Books"); Directory.CreateDirectory(folder); File.WriteAllText(System.IO.Path.Combine(folder, "location.mblink"), "/private/media");
        var info = new VirtualFolderInfo { Name = "Books", ItemId = Id(8), Locations = [], LibraryOptions = new LibraryOptions() };
        var library = Stub.Create<ILibraryManager>((m, _) => m.Name == "GetVirtualFolders" ? new List<VirtualFolderInfo> { info } : throw new NotSupportedException(m.Name));
        var compat = new JellyfinCompat(library, Users(), Stub.Create<IServerApplicationPaths>((m, _) => m.Name == "get_DefaultUserViewsPath" ? root : throw new NotSupportedException(m.Name)));
        Assert.Throws<IOException>(() => compat.Inventory());
        info.Locations = ["/private/media"];
        var inventory = Assert.Single(compat.Inventory()); Assert.Equal(Id(8), inventory.Id);
        Assert.DoesNotContain("/private/media", string.Join("", inventory.Locations));
        Directory.Delete(root, true); Assert.Throws<DirectoryNotFoundException>(() => compat.Inventory());
    }
    [Fact] public void AudienceUsesBothJellyfinPredicatesAndRejectsDisabledUsers()
    {
        var allowed = new User("allowed", "auth", "reset") { Id = Guid.ParseExact(Id(1), "N") };
        var disabled = new User("disabled", "auth", "reset") { Id = Guid.ParseExact(Id(2), "N") }; disabled.SetPermission(PermissionKind.IsDisabled, true);
        var folder = new VisibleFolder { Visible = true };
        var item = new VisibleMovie { Visible = true };
        var library = Stub.Create<ILibraryManager>((m, _) => m.Name == "GetCollectionFolders" ? new List<Folder> { folder } : throw new NotSupportedException(m.Name));
        var compat = new JellyfinCompat(library, Users([allowed, disabled]), Stub.Create<IServerApplicationPaths>((_, _) => "unused"));
        Assert.Single(compat.Audience(item, [], "key"));
        folder.Visible = false; Assert.Empty(compat.Audience(item, [], "key"));
        folder.Visible = true; item.Visible = false; Assert.Empty(compat.Audience(item, [], "key"));
        Assert.Equal(3, item.Checks); // includes the last three evaluations, disabled user never reaches the predicate
    }
    [Fact] public void MoreThanTwoThousandUsersStopsBeforeAudienceEnumeration()
    {
        var user = new User("local", "auth", "reset");
        var compat = new JellyfinCompat(Stub.Create<ILibraryManager>((_, _) => throw new InvalidOperationException()), Users(Enumerable.Repeat(user, 2001)), Stub.Create<IServerApplicationPaths>((_, _) => "unused"));
        Assert.Throws<TooManyUsersException>(() => compat.Users("key"));
    }
    private sealed class VisibleFolder : CollectionFolder
    {
        public bool Visible { get; set; }
        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => Visible;
    }
    private sealed class VisibleMovie : Movie
    {
        public bool Visible { get; set; }
        public int Checks { get; private set; }
        public override bool IsVisibleStandalone(User user) { Checks++; return Visible; }
    }
}
