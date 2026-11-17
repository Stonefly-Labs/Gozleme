using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;

using System.Text.RegularExpressions;
using Corvus.Text.Json;
using Corvus.Text.Json.RuntimeEvaluator;
using CorvusSchema = Corvus.Text.Json.Validator.JsonSchema;

namespace Gozleme.Api.Manifests;

/// <summary>Root schema URIs the adapters validate against (all bundled; see <c>Schemas/SOURCES.md</c>).</summary>
internal static class SchemaIds
{
    public const string McpServerManifest = "https://schemas.gozleme.dev/manifest/mcp-server-manifest/1.json";
    public const string A2aStrict = "https://schemas.gozleme.dev/manifest/a2a-strict/1.0.1.json";
    public const string OpenApi31 = "https://spec.openapis.org/oas/3.1/schema/2026-11-05";
    public const string OpenApi30 = "https://spec.openapis.org/oas/3.0/schema/2024-10-18";
    public const string AsyncApi300 = "https://schemas.gozleme.invalid/upstream/asyncapi-3.0.0.json";
    public const string AgentSkills = "https://schemas.gozleme.dev/manifest/agent-skills/2026-11-17.json";
    public const string ModelCard = "https://schemas.gozleme.dev/manifest/model-card/1.json";
    public const string Envelope = "https://schemas.gozleme.dev/manifest/envelope/1.json";
    public const string Projection = "https://schemas.gozleme.dev/manifest/projection/1.json";

    public static readonly IReadOnlyList<string> Roots = [McpServerManifest, A2aStrict, OpenApi31, OpenApi30, AsyncApi300, AgentSkills, ModelCard, Envelope, Projection];
}

/// <summary>
/// The offline schema registry (research R2, FR-006). Every embedded schema is registered under its absolute
/// <c>$id</c> (or <c>id</c>), or, when it has none (MCP, AsyncAPI) or a relative one (the A2A per-message files),
/// under <c>https://schemas.gozleme.invalid/&lt;path&gt;</c>. The Corvus resolver is an in-memory delegate with
/// file-system and HTTP resolution off, so a URI that is not bundled fails compilation and is never fetched.
/// Each schema compiles once. Failures are reduced to <c>(keyword, instance location)</c> pairs and mapped to the
/// catalogue: <c>additionalProperties</c>/<c>unevaluatedProperties</c>/<c>propertyNames</c> → <c>unknown_field</c>
/// (<c>native.unknown-field</c>), any other keyword → <c>invalid_value</c> (<c>native.schema</c>). No schema message
/// text is ever returned.
/// </summary>
internal sealed class SchemaRegistry
{
    public const string ResourcePrefix = "Manifests/Schemas/";
    public const string LocalBase = "https://schemas.gozleme.invalid/";

    /// <summary>A pattern in a bundled schema may run this long on one value; a timeout is a schema failure (fail closed).</summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly FrozenSet<string> UnknownFieldKeywords =
        new[] { "additionalProperties", "unevaluatedProperties", "propertyNames" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Keywords whose next evaluation-path segment is a member name or index, not a keyword.</summary>
    private static readonly FrozenSet<string> NamedApplicators =
        new[] { "properties", "patternProperties", "$defs", "definitions", "dependentSchemas", "dependencies", "allOf", "anyOf", "oneOf", "prefixItems" }
            .ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, byte[]> _documents;
    private readonly ConcurrentDictionary<string, Lazy<CorvusSchema>> _compiled = new(StringComparer.Ordinal);
    private readonly CorvusSchema.Options _options;

    public SchemaRegistry()
    {
        _documents = Load().ToFrozenDictionary(StringComparer.Ordinal);
        JsonSchemaDocumentResolver resolver = (string uri, out ReadOnlyMemory<byte> json) =>
        {
            var found = _documents.TryGetValue(uri, out var bytes);
            json = bytes;
            return found;
        };
        _options = new CorvusSchema.Options([], false, JsonSchemaDialect.Draft202012, false, resolver, JsonSchemaCodeGeneration.Disabled, PatternTimeout);
    }

    /// <summary>The URI of every bundled document.</summary>
    public IReadOnlyCollection<string> Uris => _documents.Keys;

    /// <summary>Compiles (once) the schema at <paramref name="uri"/>; an unbundled URI or reference throws <see cref="JsonSchemaCompilationException"/>.</summary>
    public CorvusSchema Compile(string uri) =>
        _compiled.GetOrAdd(uri, u => new Lazy<CorvusSchema>(() => CompileUncached(u), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Compiles any schema text against the bundled documents only (tests use this to prove the resolver is offline).</summary>
    public CorvusSchema CompileText(string schema, string uri) => CorvusSchema.FromText(schema, uri, _options, true);

    public void WarmUp()
    {
        foreach (var root in SchemaIds.Roots)
        {
            Compile(root);
        }
    }

    /// <summary>Validates <paramref name="instanceJson"/> against the schema at <paramref name="uri"/>; problems point into <paramref name="part"/>.</summary>
    public List<ManifestProblem> Validate(string uri, byte[] instanceJson, string part)
    {
        var schema = Compile(uri);
        var failures = new List<(string Evaluation, string Instance)>();
        using (var collector = JsonSchemaResultsCollector.Create(JsonSchemaResultsLevel.Detailed, 32))
        {
            try
            {
                if (schema.Validate(instanceJson, collector))
                {
                    return [];
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return [new ManifestProblem(ManifestRules.NativeSchema, ProblemLocation.At(part, string.Empty))];
            }

            foreach (var result in collector.EnumerateResults())
            {
                if (!result.IsMatch)
                {
                    failures.Add((result.GetEvaluationLocationText(), Pointer(result.GetDocumentEvaluationLocationText())));
                }
            }
        }

        return Reduce(failures, part);
    }

    /// <summary>
    /// Keeps leaf failures only (no other failure lies beneath their evaluation path), then maps them. For
    /// <c>additionalProperties</c>/<c>unevaluatedProperties</c> the object-level summary entry is dropped in favour of
    /// the member entries. An <c>unevaluatedProperties</c> finding is also dropped when another schema failure lies at
    /// or beneath the same object: a failed subschema discards its annotations, so its members would otherwise be
    /// reported as unknown when they are merely invalid (verified at implementation, T013). Such a member is reported
    /// on the next attempt once the invalid value is fixed.
    /// </summary>
    internal static List<ManifestProblem> Reduce(List<(string Evaluation, string Instance)> failures, string part)
    {
        var distinct = failures.Distinct().ToList();
        var leaves = distinct.Where(f => !distinct.Any(o => o.Evaluation.Length > f.Evaluation.Length
            && o.Evaluation.StartsWith(f.Evaluation, StringComparison.Ordinal) && o.Evaluation[f.Evaluation.Length] == '/')).ToList();

        var mapped = new List<(string Keyword, string Instance)>();
        foreach (var (evaluation, instance) in leaves)
        {
            var keyword = Keyword(evaluation);
            if (keyword is "additionalProperties" or "unevaluatedProperties"
                && leaves.Any(o => o.Evaluation == evaluation && IsBeneath(o.Instance, instance)))
            {
                continue;
            }

            mapped.Add((keyword, instance));
        }

        var invalid = mapped.Where(m => !UnknownFieldKeywords.Contains(m.Keyword)).Select(m => m.Instance).ToList();
        var problems = new List<ManifestProblem>();
        foreach (var (keyword, instance) in mapped.Distinct())
        {
            if (keyword == "unevaluatedProperties" && invalid.Any(i => i == Parent(instance) || IsBeneath(i, Parent(instance))))
            {
                continue;
            }

            var rule = UnknownFieldKeywords.Contains(keyword) ? ManifestRules.NativeUnknownField : ManifestRules.NativeSchema;
            problems.Add(new ManifestProblem(rule, ProblemLocation.At(part, instance)));
        }

        return problems;
    }

    /// <summary>The keyword an evaluation path ends in: its last segment that is not a member name or index of an applicator.</summary>
    internal static string Keyword(string evaluation)
    {
        var segments = evaluation.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var keyword = string.Empty;
        for (var i = 0; i < segments.Length; i++)
        {
            keyword = segments[i];
            if (NamedApplicators.Contains(segments[i]))
            {
                i++;
            }
        }

        return keyword;
    }

    private static bool IsBeneath(string pointer, string ancestor) =>
        pointer.Length > ancestor.Length && pointer.StartsWith(ancestor, StringComparison.Ordinal) && pointer[ancestor.Length] == '/';

    private static string Parent(string pointer) => pointer.LastIndexOf('/') is var i and > 0 ? pointer[..i] : string.Empty;

    private static string Pointer(string location) => location.StartsWith('#') ? location[1..] : location;

    private CorvusSchema CompileUncached(string uri)
    {
        if (!_documents.TryGetValue(uri, out var bytes))
        {
            throw new JsonSchemaCompilationException($"Schema '{uri}' is not bundled.");
        }

        return CorvusSchema.FromText(System.Text.Encoding.UTF8.GetString(bytes), uri, _options);
    }

    /// <summary>Embedded schema bytes by registration URI.</summary>
    internal static IEnumerable<KeyValuePair<string, byte[]>> Load()
    {
        var assembly = typeof(SchemaRegistry).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var bytes = ReadResource(assembly, name);
            yield return new(UriOf(name[ResourcePrefix.Length..], bytes), bytes);
        }
    }

    internal static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>An absolute <c>$id</c>/<c>id</c>, else the local base plus the path under <c>Schemas/</c>.</summary>
    internal static string UriOf(string path, byte[] bytes)
    {
        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        foreach (var key in (string[])["$id", "id"])
        {
            if (document.RootElement.TryGetProperty(key, out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
                && Uri.TryCreate(id.GetString(), UriKind.Absolute, out var absolute))
            {
                return absolute.OriginalString;
            }
        }

        return LocalBase + path;
    }
}

/// <summary>Compiles every root schema once at start, off the start path so health does not wait (research R2).</summary>
internal sealed partial class SchemaWarmUp(SchemaRegistry registry, ILogger<SchemaWarmUp> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
    {
        try
        {
            registry.WarmUp();
            LogWarm(logger, SchemaIds.Roots.Count);
        }
        catch (Exception ex) when (ex is JsonSchemaCompilationException or System.Text.Json.JsonException)
        {
            LogFailed(logger, ex);
            throw;
        }
    }, stoppingToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compiled {Count} bundled manifest schemas")]
    private static partial void LogWarm(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Critical, Message = "A bundled manifest schema failed to compile")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
