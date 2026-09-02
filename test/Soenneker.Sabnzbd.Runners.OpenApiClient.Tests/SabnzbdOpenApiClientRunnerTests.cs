using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.OpenApi.Abstract;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Sabnzbd.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class SabnzbdOpenApiClientRunnerTests : HostedUnitTest
{
    private readonly ISabnzbdOpenApiDocumentGenerator _generator;

    public SabnzbdOpenApiClientRunnerTests(Host host) : base(host)
    {
        _generator = Resolve<ISabnzbdOpenApiDocumentGenerator>();
    }

    [Test]
    public async Task Documentation_html_generates_openapi_document(CancellationToken cancellationToken)
    {
        const string html = """
                            <div class="wiki-content">
                              <table>
                                <tr><th>Function</th><th>Description</th></tr>
                                <tr><td>queue</td><td>Full Queue output</td></tr>
                                <tr><td>history</td><td>Full history output</td></tr>
                              </table>
                              <table>
                                <tr><th>Input parameter</th><th>Description</th></tr>
                                <tr><td>start optional</td><td>Index of job to start at</td></tr>
                              </table>
                              <pre><code>api?mode=queue&amp;start=START</code></pre>
                              <pre><code>api?mode=history&amp;start=START</code></pre>
                              <pre><code>{"queue":{"status":"Downloading","slots":[]}}</code></pre>
                              <p>Upload using POST multipart/form-data.</p>
                            </div>
                            """;

        string json = await _generator.GenerateFromHtml(html, "https://sabnzbd.org/wiki/configuration/5.0/api", cancellationToken: cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement root = document.RootElement;
        JsonElement apiPath = root.GetProperty("paths").GetProperty("/api");
        JsonElement modeValues = root.GetProperty("components")
                                     .GetProperty("parameters")
                                     .GetProperty("Mode")
                                     .GetProperty("schema")
                                     .GetProperty("enum");

        string[] modes = modeValues.EnumerateArray().Select(element => element.GetString()!).ToArray();

        await Assert.That(root.GetProperty("openapi").GetString()).IsEqualTo("3.0.3");
        await Assert.That(apiPath.TryGetProperty("get", out _)).IsTrue();
        await Assert.That(apiPath.TryGetProperty("post", out _)).IsTrue();
        await Assert.That(modes.Contains("queue", StringComparer.Ordinal)).IsTrue();
        await Assert.That(modes.Contains("history", StringComparer.Ordinal)).IsTrue();
        await Assert.That(root.GetProperty("components").GetProperty("parameters").TryGetProperty("Start", out _)).IsTrue();
        await Assert.That(root.GetProperty("components").GetProperty("schemas").GetProperty("ApiCommandResponse")
                              .GetProperty("properties").TryGetProperty("queue", out _)).IsTrue();
    }
}
