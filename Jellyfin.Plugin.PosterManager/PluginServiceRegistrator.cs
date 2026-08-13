using Jellyfin.Plugin.PosterManager.Providers;
using Jellyfin.Plugin.PosterManager.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.PosterManager;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Singletons: MediuxSource keeps an in-memory set cache across requests.
        serviceCollection.AddSingleton<IPosterSource, ThePosterDbSource>();
        serviceCollection.AddSingleton<IPosterSource, MediuxSource>();
        serviceCollection.AddHostedService<WebInjectionService>();
    }
}
