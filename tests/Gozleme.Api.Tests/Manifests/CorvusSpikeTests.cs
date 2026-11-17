using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Corvus.Json;
using Corvus.Json.Validator;

namespace Gozleme.Api.Tests.Manifests;

// T002 throwaway spike (research R2 "verify at vendoring"). Deleted or folded into
// ManifestSchemaPinTests once the maintainer has reviewed the recorded results.
public sealed class CorvusSpikeTests
{
    private static readonly string Upstream = Path.Combine(
        FindRepoRoot(), "src", "Gozleme.Api", "Manifests", "Schemas", "upstream");

    private static readonly StringBuilder Report = new();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gozleme.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static void Log(string line)
    {
        lock (Report)
        {
            Report.AppendLine(line);
            var path = Environment.GetEnvironmentVariable("CORVUS_SPIKE_OUT");
            if (path is not null)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
    }

    private static PrepopulatedDocumentResolver Resolver(params (string Uri, string File)[] docs)
    {
        var resolver = new PrepopulatedDocumentResolver();
        foreach (var (uri, file) in docs)
        {
            resolver.AddDocument(uri, JsonDocument.Parse(File.ReadAllText(Path.Combine(Upstream, file))));
        }

        return resolver;
    }

    private static JsonSchema.Options Offline(IDocumentResolver resolver) =>
        new(resolver, allowFileSystemAndHttpResolution: false, fallbackVocabulary: null, alwaysAssertFormat: false);

    private static void Describe(string name, ValidationContext result)
    {
        Log($"  [{name}] valid={result.IsValid} results={result.Results.Count}");
        foreach (var r in result.Results.Where(r => !r.Valid).Take(8))
        {
            Log($"    keywordPath={r.Location?.ValidationLocation} schema={r.Location?.SchemaLocation} instance={r.Location?.DocumentLocation} msg={r.Message}");
        }
    }

    [Fact(Explicit = true)]
    public void A_RuntimeCompileCostAndRoslyn()
    {
        var before = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).ToHashSet();
        var sw = Stopwatch.StartNew();
        var oas31 = JsonSchema.FromText(
            File.ReadAllText(Path.Combine(Upstream, "oas-3.1-2026-11-05.json")),
            "https://spec.openapis.org/oas/3.1/schema/2026-11-05",
            Offline(Resolver()));
        var first = sw.Elapsed;
        sw.Restart();
        try
        {
            _ = JsonSchema.FromText(
                File.ReadAllText(Path.Combine(Upstream, "asyncapi-3.0.0.json")),
                "https://gozleme.invalid/upstream/asyncapi-3.0.0.json",
                Offline(Resolver()));
            Log($"(a) compile asyncapi-3.0.0: {sw.Elapsed.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex)
        {
            var lines = ex.Message.Split('\n');
            Log($"(a) compile asyncapi-3.0.0 FAILED after {sw.Elapsed.TotalMilliseconds:F0} ms: {ex.GetType().Name}, {lines.Length} message lines; first: {lines[0]}");
            foreach (var l in lines.Where(l => l.Contains("error CS", StringComparison.Ordinal)).Select(l => l.Trim()).Distinct().Take(6))
            {
                Log($"    {l}");
            }
        }

        var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name!).Where(n => !before.Contains(n)).Order().ToList();
        Log($"(a) compile oas-3.1 (first, cold): {first.TotalMilliseconds:F0} ms");
        Log($"(a) assemblies loaded by compile: {string.Join(", ", loaded)}");
        Log($"(a) roslyn loaded: {loaded.Any(n => n.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))}; dynamic assemblies: {AppDomain.CurrentDomain.GetAssemblies().Count(a => a.IsDynamic)}; collectible: {AppDomain.CurrentDomain.GetAssemblies().Count(a => a.IsCollectible)}");

        sw.Restart();
        var r = oas31.Validate(JsonDocument.Parse("""{"openapi":"3.1.2","info":{"title":"x","version":"1"},"paths":{}}""").RootElement, ValidationLevel.Detailed);
        Log($"(a) first validate oas-3.1: {sw.Elapsed.TotalMilliseconds:F1} ms valid={r.IsValid}");
        Assert.True(r.IsValid);
    }

    [Fact(Explicit = true)]
    public void B_FailuresExposeKeywordAndInstanceLocation()
    {
        var oas31 = JsonSchema.FromText(
            File.ReadAllText(Path.Combine(Upstream, "oas-3.1-2026-11-05.json")),
            "https://spec.openapis.org/oas/3.1/schema/2026-11-05",
            Offline(Resolver()));
        var doc = JsonDocument.Parse("""{"openapi":"3.1.2","info":{"title":5,"version":"1","bogus":true},"paths":{"/a":{"get":{"responses":{"200":{"description":"ok"}},"nope":1}}}}""");
        var flag = oas31.Validate(doc.RootElement, ValidationLevel.Flag);
        var basic = oas31.Validate(doc.RootElement, ValidationLevel.Basic);
        var detailed = oas31.Validate(doc.RootElement, ValidationLevel.Detailed);
        var verbose = oas31.Validate(doc.RootElement, ValidationLevel.Verbose);
        Log("(b) OAS 3.1 invalid document:");
        Describe("flag", flag);
        Describe("basic", basic);
        Describe("detailed", detailed);
        Log($"  [verbose] results={verbose.Results.Count} failing={verbose.Results.Count(x => !x.Valid)}");
        Assert.False(detailed.IsValid);
    }

    [Fact(Explicit = true)]
    public void C_MixedDialectOverlay()
    {
        const string overlay = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://gozleme.invalid/spike/overlay.json",
          "type": "object",
          "properties": {
            "remotes": { "type": "array", "items": { "$ref": "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json#/definitions/StreamableHttpTransport", "unevaluatedProperties": false } },
            "serverInfo": { "$ref": "https://gozleme.invalid/upstream/mcp-2026-07-28.json#/$defs/Implementation", "unevaluatedProperties": false }
          },
          "unevaluatedProperties": false
        }
        """;
        var resolver = Resolver(
            ("https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json", "mcp-registry-2025-12-11.json"),
            ("https://gozleme.invalid/upstream/mcp-2026-07-28.json", "mcp-2026-07-28.json"));
        var schema = JsonSchema.FromText(overlay, "https://gozleme.invalid/spike/overlay.json", Offline(resolver));
        var ok = schema.Validate(JsonDocument.Parse("""{"remotes":[{"type":"streamable-http","url":"https://example.com/mcp"}],"serverInfo":{"name":"x","version":"1.0.0"}}""").RootElement, ValidationLevel.Detailed);
        var extraRemote = schema.Validate(JsonDocument.Parse("""{"remotes":[{"type":"streamable-http","url":"https://example.com/mcp","extra":1}]}""").RootElement, ValidationLevel.Detailed);
        var extraInfo = schema.Validate(JsonDocument.Parse("""{"serverInfo":{"name":"x","version":"1.0.0","extra":1}}""").RootElement, ValidationLevel.Detailed);
        var wrongType = schema.Validate(JsonDocument.Parse("""{"remotes":[{"type":"streamable-http","url":5}]}""").RootElement, ValidationLevel.Detailed);
        Log("(c) 2020-12 overlay over draft-07 server.json defs and 2020-12 MCP defs:");
        Describe("ok", ok);
        Describe("extra field on draft-07 remote", extraRemote);
        Describe("extra field on 2020-12 Implementation", extraInfo);
        Describe("wrong type on draft-07 remote", wrongType);
        Assert.True(ok.IsValid);
    }

    [Fact(Explicit = true)]
    public void D_Draft04OpenApi30()
    {
        var oas30 = JsonSchema.FromText(
            File.ReadAllText(Path.Combine(Upstream, "oas-3.0-2024-10-18.json")),
            "https://spec.openapis.org/oas/3.0/schema/2024-10-18",
            Offline(Resolver()));
        var ok = oas30.Validate(JsonDocument.Parse("""{"openapi":"3.0.4","info":{"title":"x","version":"1"},"paths":{}}""").RootElement, ValidationLevel.Detailed);
        var bad = oas30.Validate(JsonDocument.Parse("""{"openapi":"3.0.4","info":{"title":"x","version":"1"},"paths":{},"bogus":1}""").RootElement, ValidationLevel.Detailed);
        Log("(d) draft-04 OAS 3.0:");
        Describe("ok", ok);
        Describe("unknown root field", bad);
        Assert.True(ok.IsValid);
        Assert.False(bad.IsValid);
    }

    [Fact(Explicit = true)]
    public void E_UnbundledRefDoesNotFetch()
    {
        const string schema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","$ref":"https://example.com/not-bundled.json"}""";
        Exception? caught = null;
        try
        {
            var s = JsonSchema.FromText(schema, "https://gozleme.invalid/spike/unbundled.json", Offline(Resolver()));
            var r = s.Validate(JsonDocument.Parse("{}").RootElement, ValidationLevel.Detailed);
            Log($"(e) unbundled $ref compiled; valid={r.IsValid}");
        }
        catch (Exception ex)
        {
            caught = ex;
            Log($"(e) unbundled $ref refused: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }

        Assert.NotNull(caught);
    }
}
