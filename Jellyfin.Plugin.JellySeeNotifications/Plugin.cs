using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.JellySeeNotifications.Core;

namespace Jellyfin.Plugin.JellySeeNotifications;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string Secret { get; set; } = "";
    public string RelayOrigin { get; set; } = RelayClient.Origin;
    public List<RetiredCredential> RetiredSecrets { get; set; } = [];
    // Status contains only bounded health values; the elevated config API is the only reader.
    public string Status { get; set; } = "notConfigured";
    public bool IndexFull { get; set; }
    public bool TooManyUsers { get; set; }
    public int IndexCount { get; set; }
    public long StateBytes { get; set; }
    public int OutboxDepth { get; set; }
    public int BaselineLibrariesRemaining { get; set; }
}
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginId = "467b751b-71c7-4e3d-a688-8ef46193d251";
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override string Name => "JellySee Notifications";
    public override Guid Id => Guid.Parse(PluginId);
    public override string Description => "Private, audience-scoped library addition and removal notifications for JellySee.";
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var next = (PluginConfiguration)configuration;
        next.RetiredSecrets = Credentials.Rotate(Configuration, next.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        base.UpdateConfiguration(next);
    }
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = Name, EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" }];
}
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<IJellyfinCompat, JellyfinCompat>();
        services.AddSingleton<RelayClient>();
        services.AddHostedService<NotificationService>();
    }
}

// Credentials remain only in Jellyfin's elevated configuration, never in event persistence.
public sealed class RetiredCredential
{
    public string Secret { get; set; } = "";
    public long RetiredAt { get; set; }
}
public static class Credentials
{
    public static List<RetiredCredential> Rotate(PluginConfiguration old, string nextSecret, long now)
    {
        var retained = (old.RetiredSecrets ?? []).Where(c => c.Secret.Length is > 0 and <= 4096 && c.RetiredAt <= now + 120 && now - c.RetiredAt < 86400 && c.Secret != nextSecret).ToList();
        if (old.Secret.Length != 0 && old.Secret != nextSecret)
        {
            retained.RemoveAll(c => c.Secret == old.Secret);
            retained.Add(new() { Secret = old.Secret, RetiredAt = now });
        }
        return retained.OrderByDescending(c => c.RetiredAt).Take(20).ToList();
    }
    public static Dictionary<string, string> Available(PluginConfiguration config, long now)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var credential in (config.RetiredSecrets ?? []).Where(c => c.Secret.Length is > 0 and <= 4096 && c.RetiredAt <= now + 120 && now - c.RetiredAt < 86400).Take(20))
            result[Identity.Hash(credential.Secret)] = credential.Secret;
        if (config.Secret.Length is > 0 and <= 4096) result[Identity.Hash(config.Secret)] = config.Secret;
        return result;
    }
}
