using Jellyfin.Plugin.StreamGenerator.Decorators;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.StreamGenerator;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.Decorate<IAuthorizationContext, CustomStreamTokensAuthorizationContext>();
        serviceCollection.Configure<MvcOptions>(opts => opts.Filters.Add<MasterHlsInterceptionFilter>());  
        serviceCollection.AddSingleton<MasterHlsInterceptionFilter>(); 
    }
}
