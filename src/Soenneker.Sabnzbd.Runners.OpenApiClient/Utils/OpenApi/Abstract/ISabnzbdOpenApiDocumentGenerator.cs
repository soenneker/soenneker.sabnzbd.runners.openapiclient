using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.OpenApi.Abstract;

public interface ISabnzbdOpenApiDocumentGenerator
{
    ValueTask Generate(string destinationFilePath, CancellationToken cancellationToken = default);

    ValueTask<string> GenerateFromHtml(string html, string documentationUrl, CancellationToken cancellationToken = default);
}
