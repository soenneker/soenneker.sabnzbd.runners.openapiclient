using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.AngleSharp.Parser.Abstract;
using Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.OpenApi.Abstract;
using Soenneker.Utils.HttpClientCache.Abstract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Sabnzbd.Runners.OpenApiClient.Utils.OpenApi;

public sealed class SabnzbdOpenApiDocumentGenerator : ISabnzbdOpenApiDocumentGenerator
{
    public const string DefaultDocumentationUrl = "https://sabnzbd.org/wiki/configuration/5.0/api";

    private const string _httpClientCacheKey = nameof(SabnzbdOpenApiDocumentGenerator);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex OptionalSuffixRegex = new(@"\s+optional$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DateKeyRegex = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private readonly IConfiguration _configuration;
    private readonly ILogger<SabnzbdOpenApiDocumentGenerator> _logger;
    private readonly IAngleSharpParser _angleSharpParser;
    private readonly IHttpClientCache _httpClientCache;

    public SabnzbdOpenApiDocumentGenerator(IConfiguration configuration, ILogger<SabnzbdOpenApiDocumentGenerator> logger,
        IAngleSharpParser angleSharpParser, IHttpClientCache httpClientCache)
    {
        _configuration = configuration;
        _logger = logger;
        _angleSharpParser = angleSharpParser;
        _httpClientCache = httpClientCache;
    }

    public async ValueTask Generate(string destinationFilePath, CancellationToken cancellationToken = default)
    {
        string documentationUrl = _configuration["Sabnzbd:ApiDocumentationUrl"] ?? DefaultDocumentationUrl;

        _logger.LogInformation("Downloading SABnzbd API documentation from {DocumentationUrl} ...", documentationUrl);

        HttpClient client = await _httpClientCache.Get(_httpClientCacheKey, cancellationToken);

        using HttpRequestMessage request = new(HttpMethod.Get, documentationUrl);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        string html = await response.Content.ReadAsStringAsync(cancellationToken);
        string openApiJson = await GenerateFromHtml(html, documentationUrl, cancellationToken);

        await File.WriteAllTextAsync(destinationFilePath, openApiJson, cancellationToken);

        _logger.LogInformation("Generated SABnzbd OpenAPI document at {DestinationFilePath}", destinationFilePath);
    }

    /// <summary>
    /// Builds an OpenAPI document from the HTML of the SABnzbd API reference.
    /// </summary>
    public async ValueTask<string> GenerateFromHtml(string html, string documentationUrl = DefaultDocumentationUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(html))
            throw new ArgumentException("SABnzbd API documentation HTML cannot be empty.", nameof(html));

        HtmlParser parser = await _angleSharpParser.Get(cancellationToken);
        IDocument document = await parser.ParseDocumentAsync(html, cancellationToken);
        IElement content = document.QuerySelector(".wiki-content")
                           ?? throw new InvalidOperationException("The SABnzbd API documentation did not contain the expected .wiki-content element.");

        List<string> codeSamples = content.QuerySelectorAll("pre code")
                                          .Select(element => element.TextContent.Trim())
                                          .Where(text => text.Length > 0)
                                          .ToList();

        List<string> requestExamples = codeSamples.Where(IsApiRequestExample).Distinct(StringComparer.Ordinal).ToList();
        List<FunctionDescriptor> functions = ParseFunctionTables(content);
        SortedSet<string> modes = CollectModes(requestExamples, functions);

        if (modes.Count == 0)
            throw new InvalidOperationException("No SABnzbd API modes were discovered in the documentation.");

        Dictionary<string, string> parameterDescriptions = ParseInputParameterTables(content);
        SortedSet<string> parameterNames = CollectParameterNames(requestExamples, parameterDescriptions.Keys);
        JsonObject responseSchema = BuildResponseSchema(codeSamples);

        JsonObject openApiDocument = BuildOpenApiDocument(documentationUrl, modes, parameterNames, parameterDescriptions, responseSchema,
            requestExamples, functions, content.TextContent.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase));

        return openApiDocument.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildOpenApiDocument(string documentationUrl, SortedSet<string> modes, SortedSet<string> parameterNames,
        IReadOnlyDictionary<string, string> parameterDescriptions, JsonObject responseSchema, IReadOnlyCollection<string> requestExamples,
        IReadOnlyCollection<FunctionDescriptor> functions, bool supportsMultipartUpload)
    {
        JsonObject parameters = new();
        JsonArray operationParameters = new();

        foreach (string parameterName in parameterNames.OrderBy(name => name.Equals("mode", StringComparison.Ordinal) ? string.Empty : name, StringComparer.Ordinal))
        {
            JsonObject parameter = BuildParameter(parameterName, modes, parameterDescriptions.GetValueOrDefault(parameterName));
            string componentName = ToComponentName(parameterName);
            parameters[componentName] = parameter;
            operationParameters.Add(new JsonObject { ["$ref"] = $"#/components/parameters/{componentName}" });
        }

        JsonObject responses = new()
        {
            ["200"] = new JsonObject
            {
                ["description"] = "Response for the query-selected SABnzbd command.",
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/ApiCommandResponse" }
                    },
                    ["application/xml"] = new JsonObject
                    {
                        ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/ApiCommandResponse" }
                    },
                    ["application/zip"] = new JsonObject
                    {
                        ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "binary" }
                    }
                }
            }
        };

        JsonObject getOperation = new()
        {
            ["tags"] = new JsonArray("Api"),
            ["summary"] = "Execute a SABnzbd API command",
            ["description"] = "Executes the command selected by mode and, where applicable, name. The response shape depends on those values. version and auth do not require an API key.",
            ["operationId"] = "executeApiCommand",
            ["security"] = new JsonArray(new JsonObject { ["apiKey"] = new JsonArray() }, new JsonObject()),
            ["parameters"] = operationParameters,
            ["responses"] = responses,
            ["x-sabnzbd-request-examples"] = new JsonArray(requestExamples.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["x-sabnzbd-functions"] = BuildFunctionExtension(functions)
        };

        JsonObject apiPath = new() { ["get"] = getOperation };

        if (supportsMultipartUpload)
            apiPath["post"] = BuildUploadOperation();

        JsonObject schemas = new()
        {
            ["ApiCommandResponse"] = responseSchema,
            ["NzbUploadRequest"] = BuildUploadRequestSchema()
        };

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = "SABnzbd API",
                ["version"] = "5.0",
                ["description"] = "Generated from the official SABnzbd 5.0 API reference. SABnzbd selects commands using mode and name query parameters."
            },
            ["externalDocs"] = new JsonObject { ["description"] = "Official SABnzbd 5.0 API reference", ["url"] = documentationUrl },
            ["servers"] = new JsonArray(new JsonObject
            {
                ["url"] = "http://localhost:8080",
                ["description"] = "Replace the generated client's base URL with the URL of the SABnzbd instance."
            }),
            ["tags"] = new JsonArray(new JsonObject { ["name"] = "Api" }),
            ["paths"] = new JsonObject { ["/api"] = apiPath },
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["apiKey"] = new JsonObject
                    {
                        ["type"] = "apiKey",
                        ["in"] = "query",
                        ["name"] = "apikey",
                        ["description"] = "API key from Config > General. The restricted NZB key can modify queue jobs only."
                    }
                },
                ["parameters"] = parameters,
                ["schemas"] = schemas
            },
            ["x-source-url"] = documentationUrl
        };
    }

    private static JsonObject BuildParameter(string name, IReadOnlyCollection<string> modes, string? description)
    {
        JsonObject schema = name switch
        {
            "mode" => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(modes.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) },
            "output" => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("json", "xml"), ["default"] = "json" },
            "start" or "limit" or "size" or "connections" => new JsonObject { ["type"] = "integer", ["format"] = "int32", ["minimum"] = 0 },
            "pp" => new JsonObject { ["type"] = "integer", ["format"] = "int32", ["minimum"] = -1, ["maximum"] = 3 },
            "last_history_update" => new JsonObject { ["type"] = "integer", ["format"] = "int64" },
            "del_files" or "archive" or "failed_only" or "skip_dashboard" or "calculate_performance" =>
                new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(0, 1) },
            "keyword" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            _ => new JsonObject { ["type"] = "string" }
        };

        JsonObject parameter = new()
        {
            ["name"] = name,
            ["in"] = "query",
            ["required"] = name.Equals("mode", StringComparison.Ordinal),
            ["description"] = description ?? GetDefaultParameterDescription(name),
            ["schema"] = schema
        };

        if (name.Equals("keyword", StringComparison.Ordinal))
        {
            parameter["style"] = "form";
            parameter["explode"] = true;
        }

        return parameter;
    }

    private static JsonObject BuildResponseSchema(IEnumerable<string> codeSamples)
    {
        JsonNode? combinedSchema = null;
        var jsonOptions = new JsonDocumentOptions { AllowTrailingCommas = true };

        foreach (string codeSample in codeSamples)
        {
            if (!codeSample.StartsWith('{') || !codeSample.EndsWith('}'))
                continue;

            try
            {
                using JsonDocument example = JsonDocument.Parse(codeSample, jsonOptions);
                JsonNode inferred = InferSchema(example.RootElement);
                combinedSchema = combinedSchema == null ? inferred : MergeSchemas(combinedSchema, inferred);
            }
            catch (JsonException)
            {
                // The wiki also contains non-JSON brace syntax in request examples.
            }
        }

        if (combinedSchema is not JsonObject responseSchema || responseSchema["type"]?.GetValue<string>() != "object")
            throw new InvalidOperationException("No JSON response examples could be parsed from the SABnzbd API documentation.");

        responseSchema["description"] =
            "Combined response envelope inferred from the JSON examples in the SABnzbd API reference. Only properties returned by the selected command are populated.";
        responseSchema["additionalProperties"] = true;
        return responseSchema;
    }

    private static JsonNode InferSchema(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                JsonProperty[] properties = element.EnumerateObject().ToArray();

                if (properties.Length == 0)
                    return new JsonObject { ["type"] = "object", ["additionalProperties"] = true };

                if (properties.All(property => IsDynamicKey(property.Name)))
                {
                    JsonNode? itemSchema = null;
                    foreach (JsonProperty property in properties)
                    {
                        JsonNode inferred = InferSchema(property.Value);
                        itemSchema = itemSchema == null ? inferred : MergeSchemas(itemSchema, inferred);
                    }

                    return new JsonObject { ["type"] = "object", ["additionalProperties"] = itemSchema ?? new JsonObject() };
                }

                JsonObject propertySchemas = new();
                foreach (JsonProperty property in properties)
                    propertySchemas[property.Name] = InferSchema(property.Value);

                return new JsonObject { ["type"] = "object", ["properties"] = propertySchemas, ["additionalProperties"] = true };
            }
            case JsonValueKind.Array:
            {
                JsonNode? itemSchema = null;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    JsonNode inferred = InferSchema(item);
                    itemSchema = itemSchema == null ? inferred : MergeSchemas(itemSchema, inferred);
                }

                return new JsonObject { ["type"] = "array", ["items"] = itemSchema ?? new JsonObject() };
            }
            case JsonValueKind.String:
                return new JsonObject { ["type"] = "string" };
            case JsonValueKind.Number:
                return element.TryGetInt64(out _)
                    ? new JsonObject { ["type"] = "integer", ["format"] = "int64" }
                    : new JsonObject { ["type"] = "number", ["format"] = "double" };
            case JsonValueKind.True:
            case JsonValueKind.False:
                return new JsonObject { ["type"] = "boolean" };
            case JsonValueKind.Null:
            default:
                return new JsonObject { ["nullable"] = true };
        }
    }

    private static JsonNode MergeSchemas(JsonNode left, JsonNode right)
    {
        if (left is not JsonObject leftObject || right is not JsonObject rightObject)
            return new JsonObject();

        string? leftType = leftObject["type"]?.GetValue<string>();
        string? rightType = rightObject["type"]?.GetValue<string>();

        if (leftType == null)
        {
            JsonObject result = (JsonObject)rightObject.DeepClone();
            if (leftObject["nullable"]?.GetValue<bool>() == true)
                result["nullable"] = true;
            return result;
        }

        if (rightType == null)
        {
            JsonObject result = (JsonObject)leftObject.DeepClone();
            if (rightObject["nullable"]?.GetValue<bool>() == true)
                result["nullable"] = true;
            return result;
        }

        if (leftType != rightType)
        {
            if ((leftType == "integer" && rightType == "number") || (leftType == "number" && rightType == "integer"))
                return new JsonObject { ["type"] = "number", ["format"] = "double" };

            return new JsonObject();
        }

        JsonObject merged = (JsonObject)leftObject.DeepClone();
        if (leftType == "object")
        {
            if (merged["properties"] is JsonObject mergedProperties && rightObject["properties"] is JsonObject rightProperties)
            {
                foreach ((string propertyName, JsonNode? propertySchema) in rightProperties)
                {
                    if (propertySchema == null)
                        continue;

                    mergedProperties[propertyName] = mergedProperties[propertyName] == null
                        ? propertySchema.DeepClone()
                        : MergeSchemas(mergedProperties[propertyName]!, propertySchema);
                }
            }
            else if (merged["additionalProperties"] is JsonNode leftAdditional && rightObject["additionalProperties"] is JsonNode rightAdditional &&
                     leftAdditional is not JsonValue && rightAdditional is not JsonValue)
            {
                merged["additionalProperties"] = MergeSchemas(leftAdditional, rightAdditional);
            }
        }
        else if (leftType == "array" && merged["items"] is JsonNode leftItems && rightObject["items"] is JsonNode rightItems)
        {
            merged["items"] = MergeSchemas(leftItems, rightItems);
        }

        if (rightObject["nullable"]?.GetValue<bool>() == true)
            merged["nullable"] = true;

        return merged;
    }

    private static List<FunctionDescriptor> ParseFunctionTables(IElement content)
    {
        List<FunctionDescriptor> result = [];

        foreach (IElement table in content.QuerySelectorAll("table"))
        {
            string[] headers = table.QuerySelectorAll("th").Select(header => CleanText(header.TextContent)).ToArray();
            if (headers.Length == 0 || !headers[0].Equals("Function", StringComparison.OrdinalIgnoreCase))
                continue;

            bool functionsAreModes = GetPrecedingSection(table).Equals("Other functions", StringComparison.OrdinalIgnoreCase);

            foreach (IElement row in table.QuerySelectorAll("tr"))
            {
                string[] cells = row.QuerySelectorAll("td").Select(cell => CleanText(cell.TextContent)).ToArray();
                if (cells.Length < 2)
                    continue;

                AddFunctionDescriptors(result, cells[0], cells[1], functionsAreModes);
            }
        }

        return result.Distinct().ToList();
    }

    private static void AddFunctionDescriptors(ICollection<FunctionDescriptor> result, string functionText, string description, bool functionIsMode)
    {
        if (functionText.StartsWith("mode=", StringComparison.OrdinalIgnoreCase))
        {
            Dictionary<string, string> values = functionText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                                  .Select(part => part.Split('=', 2))
                                                                  .Where(parts => parts.Length == 2)
                                                                  .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
            if (values.TryGetValue("mode", out string? mode))
                result.Add(new FunctionDescriptor(functionText, mode, values.GetValueOrDefault("name"), description));
            return;
        }

        if (!functionIsMode)
        {
            result.Add(new FunctionDescriptor(functionText, null, null, description));
            return;
        }

        foreach (string alternative in functionText.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string withoutQualifier = alternative.Split('(', 2)[0].Trim();
            string[] words = withoutQualifier.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                continue;

            string? name = words.Length > 1 ? words[1] : null;
            result.Add(new FunctionDescriptor(functionText, words[0], name, description));
        }
    }

    private static Dictionary<string, string> ParseInputParameterTables(IElement content)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach (IElement table in content.QuerySelectorAll("table"))
        {
            string[] headers = table.QuerySelectorAll("th").Select(header => CleanText(header.TextContent)).ToArray();
            if (headers.Length == 0 || !headers[0].StartsWith("Input parameter", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (IElement row in table.QuerySelectorAll("tr"))
            {
                string[] cells = row.QuerySelectorAll("td").Select(cell => CleanText(cell.TextContent)).ToArray();
                if (cells.Length < 2)
                    continue;

                string names = OptionalSuffixRegex.Replace(cells[0], string.Empty);
                foreach (string name in names.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    result[name] = cells[1];
            }
        }

        return result;
    }

    private static SortedSet<string> CollectModes(IEnumerable<string> requestExamples, IEnumerable<FunctionDescriptor> functions)
    {
        SortedSet<string> result = new(StringComparer.Ordinal);

        foreach (string example in requestExamples)
        {
            IReadOnlyDictionary<string, string> values = ParseQuery(example);
            if (values.TryGetValue("mode", out string? mode) && IsMode(mode))
                result.Add(mode);
        }

        foreach (FunctionDescriptor function in functions)
        {
            if (function.Mode != null && IsMode(function.Mode))
                result.Add(function.Mode);
        }

        foreach (FunctionDescriptor function in functions.Where(item => item.Mode == null && item.Function.Contains('/')))
        {
            string[] aliases = function.Function.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                                .Select(value => value.Split('(', 2)[0].Trim())
                                                .Where(IsMode)
                                                .ToArray();
            if (aliases.Any(result.Contains))
                result.UnionWith(aliases);
        }

        return result;
    }

    private static SortedSet<string> CollectParameterNames(IEnumerable<string> requestExamples, IEnumerable<string> documentedParameters)
    {
        SortedSet<string> result = new(StringComparer.Ordinal) { "mode", "output" };

        foreach (string example in requestExamples)
        {
            foreach (string name in ParseQuery(example).Keys)
            {
                if (!name.Equals("apikey", StringComparison.OrdinalIgnoreCase) && IsIdentifier(name))
                    result.Add(name);
            }
        }

        foreach (string parameter in documentedParameters)
        {
            if (IsIdentifier(parameter))
                result.Add(parameter);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string example)
    {
        int queryStart = example.IndexOf('?');
        if (queryStart < 0 || queryStart == example.Length - 1)
            return new Dictionary<string, string>();

        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in example[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            string name = DecodeQueryValue(pair[0]);
            string value = pair.Length > 1 ? DecodeQueryValue(pair[1]) : string.Empty;
            result[name] = value;
        }

        return result;
    }

    private static string DecodeQueryValue(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static JsonObject BuildUploadOperation() => new()
    {
        ["tags"] = new JsonArray("Api"),
        ["summary"] = "Upload an NZB file or retry a job with an additional NZB",
        ["operationId"] = "uploadNzb",
        ["security"] = new JsonArray(new JsonObject { ["apiKey"] = new JsonArray() }),
        ["requestBody"] = new JsonObject
        {
            ["required"] = true,
            ["content"] = new JsonObject
            {
                ["multipart/form-data"] = new JsonObject
                {
                    ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/NzbUploadRequest" }
                }
            }
        },
        ["responses"] = new JsonObject
        {
            ["200"] = new JsonObject
            {
                ["description"] = "Upload or retry response.",
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/ApiCommandResponse" }
                    }
                }
            }
        }
    };

    private static JsonObject BuildUploadRequestSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("mode"),
        ["properties"] = new JsonObject
        {
            ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("addfile", "retry") },
            ["name"] = new JsonObject { ["type"] = "string", ["format"] = "binary" },
            ["nzbfile"] = new JsonObject { ["type"] = "string", ["format"] = "binary" },
            ["value"] = new JsonObject { ["type"] = "string", ["description"] = "nzo_id when retrying a job." },
            ["nzbname"] = new JsonObject { ["type"] = "string" },
            ["password"] = new JsonObject { ["type"] = "string" },
            ["cat"] = new JsonObject { ["type"] = "string" },
            ["script"] = new JsonObject { ["type"] = "string" },
            ["priority"] = new JsonObject { ["type"] = "integer", ["format"] = "int32" },
            ["pp"] = new JsonObject { ["type"] = "integer", ["format"] = "int32" }
        },
        ["additionalProperties"] = true
    };

    private static JsonArray BuildFunctionExtension(IEnumerable<FunctionDescriptor> functions)
    {
        JsonArray result = new();
        foreach (FunctionDescriptor function in functions.OrderBy(item => item.Function, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            JsonObject item = new() { ["function"] = function.Function, ["description"] = function.Description };
            if (!string.IsNullOrWhiteSpace(function.Mode))
                item["mode"] = function.Mode;
            if (!string.IsNullOrWhiteSpace(function.Name))
                item["name"] = function.Name;
            result.Add(item);
        }
        return result;
    }

    private static bool IsApiRequestExample(string text) =>
        (text.StartsWith("api?", StringComparison.OrdinalIgnoreCase) || text.Contains("/api?", StringComparison.OrdinalIgnoreCase)) && text.Contains('=');

    private static bool IsIdentifier(string value) => value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsMode(string value) => IsIdentifier(value) && value.Equals(value.ToLowerInvariant(), StringComparison.Ordinal);

    private static bool IsDynamicKey(string key) => DateKeyRegex.IsMatch(key) || key.Contains('.') || key.Contains(':');

    private static string CleanText(string value) => WhitespaceRegex.Replace(value, " ").Trim();

    private static string GetPrecedingSection(IElement element)
    {
        IElement? current = element.PreviousElementSibling;
        while (current != null)
        {
            if (current.TagName.Equals("H1", StringComparison.OrdinalIgnoreCase))
                return CleanText(current.TextContent);
            current = current.PreviousElementSibling;
        }

        return string.Empty;
    }

    private static string ToComponentName(string value) => string.Concat(value.Split('_', StringSplitOptions.RemoveEmptyEntries)
                                                                                    .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static string GetDefaultParameterDescription(string name) => name switch
    {
        "mode" => "Command to execute.",
        "output" => "Response format.",
        "name" => "Subcommand, NZB URL, or local file path depending on mode.",
        "value" => "Primary command value, commonly an nzo_id.",
        "value2" => "Secondary command value.",
        "value3" => "Tertiary command value.",
        _ => $"SABnzbd {name} query parameter."
    };

    private sealed record FunctionDescriptor(string Function, string? Mode, string? Name, string Description);
}
