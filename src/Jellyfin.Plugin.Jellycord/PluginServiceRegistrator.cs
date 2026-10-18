using Jellyfin.Plugin.Jellycord.Discord;
using Jellyfin.Plugin.Jellycord.Playback;
using Jellyfin.Plugin.Jellycord.Presence;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellycord;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<CredentialStore>();
        serviceCollection.AddSingleton<DiscordClient>();
        serviceCollection.AddSingleton<HeadlessSessionSink>();
        serviceCollection.AddSingleton<IPresenceSink>(sp => sp.GetRequiredService<HeadlessSessionSink>());
        serviceCollection.AddSingleton<PlaybackTracker>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PlaybackTracker>());
    }
}
