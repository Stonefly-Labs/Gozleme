using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Text.RegularExpressions;
using Corvus.Text.Json;
using Corvus.Text.Json.RuntimeEvaluator;
using Gozleme.Api.Manifests.Parsing;
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
    private readonly SchemaDeclarations _declarations;

    public SchemaRegistry()
    {
        _documents = Load().ToFrozenDictionary(StringComparer.Ordinal);
        _declarations = new SchemaDeclarations(_documents);
        JsonSchemaDocumentResolver resolver = (string uri, out ReadOnlyMemory<byte> json) =>
        {
            var found = _documents.TryGetValue(uri, out var bytes);
            json = bytes;
            return found;
        };
        _options = new CorvusSchema.Options([], false, JsonSchemaDialect.Draft202012, false, resolver, JsonSchemaCodeGeneration.Disabled, PatternTimeout);
    }

    internal SchemaDeclarations Declarations => _declarations;

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
            catch (Exception e) when (e is ArgumentException or OutOfMemoryException)
            {
                // The validator's result buffer cannot grow past 2 GB; a document that gets there is too large, never a 500 (T084).
                throw new Parsing.ValidationStop(ManifestRules.BoundResults, null);
            }

            foreach (var result in collector.EnumerateResults())
            {
                if (!result.IsMatch)
                {
                    failures.Add((result.GetEvaluationLocationText(), Pointer(result.GetDocumentEvaluationLocationText())));
                }
            }
        }

        // One validation's branch checks are memoized per (branch, object) and each object is serialized once (T073),
        // so many unknown members of one object cost one re-validation per alternative, not one per member.
        var instance = new Lazy<System.Text.Json.Nodes.JsonNode?>(() => System.Text.Json.Nodes.JsonNode.Parse(instanceJson));
        var objects = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var matches = new Dictionary<(string Branch, string Object), bool>();
        var problems = Reduce(failures, part, (evaluation, member) =>
        {
            var owner = Parent(member);
            return _declarations.IsDeclared(uri, evaluation, member, branch =>
            {
                if (!matches.TryGetValue((branch, owner), out var match))
                {
                    if (!objects.TryGetValue(owner, out var bytes))
                    {
                        objects[owner] = bytes = ObjectBytes(instance.Value, owner);
                    }

                    matches[(branch, owner)] = match = bytes is not null && MatchesBranch(branch, bytes);
                }

                return match;
            });
        });

        // Never accept an invalid document: if every failure was judged noise, report the unknown members anyway.
        return problems.Count > 0 ? problems
            : Reduce(failures, part, (_, _) => false) is { Count: > 0 } all ? all
            : [new ManifestProblem(ManifestRules.NativeSchema, ProblemLocation.At(part, string.Empty))];
    }

    /// <summary>
    /// Keeps leaf failures only (no other failure lies beneath their evaluation path), then maps them. For
    /// <c>additionalProperties</c>/<c>unevaluatedProperties</c> the object-level summary entry is dropped in favour of
    /// the member entries. An <c>unevaluatedProperties</c> member finding is dropped only when the owning schema
    /// declares that member (<paramref name="declared"/>, <see cref="SchemaDeclarations"/>): a failed subschema discards
    /// its annotations, so a declared member would otherwise be reported as unknown when only a neighbour is invalid.
    /// A member no schema declares is always reported (maintainer decision 2026-11-17).
    /// </summary>
    internal static List<ManifestProblem> Reduce(List<(string Evaluation, string Instance)> failures, string part, Func<string, string, bool> declared)
    {
        var distinct = failures.Distinct().ToList();

        // A failure is a leaf when no other failure's evaluation path extends it; one pass over every path's prefixes (T073).
        var extended = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evaluation in distinct.Select(f => f.Evaluation).Distinct(StringComparer.Ordinal))
        {
            if (evaluation.Length > 0)
            {
                extended.Add(string.Empty);
            }

            for (var i = evaluation.Length > 1 ? evaluation.IndexOf('/', 1) : -1; i > 0; i = evaluation.IndexOf('/', i + 1))
            {
                extended.Add(evaluation[..i]);
            }
        }

        var leaves = distinct.Where(f => !extended.Contains(f.Evaluation)).ToList();

        // An object-level additional/unevaluatedProperties entry gives way to its member entries under the same keyword path.
        var summarized = new HashSet<(string Evaluation, string Instance)>();
        foreach (var (evaluation, instance) in leaves)
        {
            for (var at = instance; at.Length > 0;)
            {
                at = Parent(at);
                if (!summarized.Add((evaluation, at)))
                {
                    break;
                }
            }
        }

        var mapped = new List<(string Keyword, string Instance)>();
        foreach (var (evaluation, instance) in leaves)
        {
            var keyword = Keyword(evaluation);
            if (keyword is "additionalProperties" or "unevaluatedProperties" && summarized.Contains((evaluation, instance)))
            {
                continue;
            }

            if (keyword == "unevaluatedProperties" && declared(evaluation, instance))
            {
                continue;
            }

            mapped.Add((keyword, instance));
        }

        // Sibling locations share one parsed parent chain (T084), so many problems under a deep object cost one chain.
        var parsed = new Dictionary<string, JsonPointer>(StringComparer.Ordinal) { [string.Empty] = JsonPointer.Root };
        JsonPointer Parse(string text)
        {
            if (!parsed.TryGetValue(text, out var pointer))
            {
                var slash = text.LastIndexOf('/');
                var segment = text[(slash + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                parsed[text] = pointer = Parse(slash > 0 ? text[..slash] : string.Empty).Child(segment);
            }

            return pointer;
        }

        var problems = new List<ManifestProblem>();
        foreach (var (keyword, instance) in mapped.Distinct())
        {
            var rule = UnknownFieldKeywords.Contains(keyword) ? ManifestRules.NativeUnknownField : ManifestRules.NativeSchema;
            problems.Add(new ManifestProblem(rule, ProblemLocation.At(part, Parse(instance))));
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

    /// <summary>The value at <paramref name="pointer"/> serialized as UTF-8 JSON, or null when there is none.</summary>
    private static byte[]? ObjectBytes(System.Text.Json.Nodes.JsonNode? root, string pointer)
    {
        var node = root;
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                System.Text.Json.Nodes.JsonObject o => o[name],
                System.Text.Json.Nodes.JsonArray a when int.TryParse(name, System.Globalization.CultureInfo.InvariantCulture, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null)
            {
                return null;
            }
        }

        return node is null ? null : System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    /// <summary>Whether <paramref name="value"/> is valid against the bundled schema at <paramref name="branch"/> alone.</summary>
    private bool MatchesBranch(string branch, byte[] value)
    {
        var wrapper = _compiled.GetOrAdd("branch " + branch, _ => new Lazy<CorvusSchema>(
            () => CompileText(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["$ref"] = branch }), LocalBase + "branch/" + Uri.EscapeDataString(branch)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        try
        {
            return wrapper.Validate(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

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
