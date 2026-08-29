using Microsoft.Extensions.DependencyInjection;
using Soenneker.Kiota.Util.Registrars;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.OpenApi.Fixer.Registrars;
using Soenneker.Sabnzbd.Runners.OpenApiClient.Utils;
using Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.OpenApi;
using Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.OpenApi.Abstract;
using Soenneker.AngleSharp.Parser.Registrars;
using Soenneker.Utils.HttpClientCache.Registrar;

namespace Soenneker.Sabnzbd.Runners.OpenApiClient;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    /// <summary>
    /// Registers the services required by the application host.
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    /// <summary>
    /// Registers the services required by the application.
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddSingleton<IFileOperationsUtil, FileOperationsUtil>()
                .AddSingleton<ISabnzbdOpenApiDocumentGenerator, SabnzbdOpenApiDocumentGenerator>()
                .AddAngleSharpParserAsSingleton()
                .AddHttpClientCacheAsSingleton()
                .AddRunnersManagerAsSingleton()
                .AddOpenApiFixerAsSingleton()
                .AddKiotaUtilAsSingleton();

        return services;
    }
}
