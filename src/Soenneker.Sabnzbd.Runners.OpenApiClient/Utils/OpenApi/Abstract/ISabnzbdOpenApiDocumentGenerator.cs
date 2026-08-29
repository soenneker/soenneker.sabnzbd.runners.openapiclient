using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.OpenApi.Abstract;

public interface ISabnzbdOpenApiDocumentGenerator
{
    /// <summary>
    /// Generates sabnzbd OpenAPI Document Generator for the Sabnzbd OpenAPI Document Generator.
    /// </summary>
    /// <param name="destinationFilePath">Path of the destination file to use.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the generate operation is complete.</returns>
    ValueTask Generate(string destinationFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates from HTML.
    /// </summary>
    /// <param name="html">Rendered page HTML to inspect.</param>
    /// <param name="documentationUrl">URL of the documentation to target.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the text returned by generate From HTML.</returns>
    ValueTask<string> GenerateFromHtml(string html, string documentationUrl, CancellationToken cancellationToken = default);
}
