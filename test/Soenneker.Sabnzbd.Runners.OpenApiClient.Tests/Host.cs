using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Soenneker.TestHosts.Unit;
using Soenneker.Utils.Test;
using Soenneker.AngleSharp.Parser.Registrars;
using Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.OpenApi;
using Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.OpenApi.Abstract;
using Soenneker.Utils.HttpClientCache.Registrar;

namespace Soenneker.Sabnzbd.Runners.OpenApiClient.Tests;

public sealed class Host : UnitTestHost
{
    public override Task InitializeAsync()
    {
        SetupIoC(Services);

        return base.InitializeAsync();
    }

    private static void SetupIoC(IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.AddSerilog(dispose: false);
        });

        IConfiguration config = TestUtil.BuildConfig();
        services.AddSingleton(config);
        services.AddAngleSharpParserAsSingleton()
                .AddHttpClientCacheAsSingleton()
                .AddSingleton<ISabnzbdOpenApiDocumentGenerator, SabnzbdOpenApiDocumentGenerator>();
    }
}
