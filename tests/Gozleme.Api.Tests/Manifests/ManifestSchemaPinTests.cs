using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Corvus.Text.Json.RuntimeEvaluator;
using Gozleme.Api.Manifests;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// GOZ-0006 research R1–R2 (FR-006): every embedded schema matches the SHA-256 recorded in <c>Schemas/SOURCES.md</c>,
/// the table and the embedded set agree, every root compiles offline, and an unbundled reference fails rather than
/// being fetched.
/// </summary>
public sealed partial class ManifestSchemaPinTests
{
    private static readonly SchemaRegistry Registry = new();

    private static Dictionary<string, string> SourcesTable()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Gozleme.Api/Manifests/Schemas/SOURCES.md"));
        return Row().Matches(text).ToDictionary(m => m.Groups["file"].Value, m => m.Groups["sha"].Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, byte[]> Embedded()
    {
        var assembly = typeof(SchemaRegistry).Assembly;
        return assembly.GetManifestResourceNames().Where(n => n.StartsWith(SchemaRegistry.ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(n => n[SchemaRegistry.ResourcePrefix.Length..], n => SchemaRegistry.ReadResource(assembly, n), StringComparer.Ordinal);
    }

    [Fact]
    public void Every_embedded_schema_has_a_sources_row_and_vice_versa()
    {
        var table = SourcesTable();
        var embedded = Embedded();
        Assert.Equal(61, embedded.Count);
        Assert.Equal(table.Keys.Order(StringComparer.Ordinal), embedded.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_embedded_schema_matches_its_recorded_digest()
    {
        var table = SourcesTable();
        foreach (var (path, bytes) in Embedded())
        {
            Assert.True(table[path] == Convert.ToHexStringLower(SHA256.HashData(bytes)), path);
        }
    }

    [Fact]
    public void The_overlay_copies_equal_their_contract_files()
    {
        var root = RepositoryRoot();
        foreach (var (bundled, contract) in new[]
        {
            ("envelope-1.json", "manifest-envelope-v1.schema.json"),
            ("mcp-server-manifest-1.json", "mcp-server-manifest-v1.schema.json"),
            ("model-card-1.json", "model-card-v1.schema.json"),
        })
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(root, "docs/specs/006-typed-manifests/contracts", contract)),
                File.ReadAllBytes(Path.Combine(root, "src/Gozleme.Api/Manifests/Schemas/gozleme", bundled)));
        }
    }

    [Fact]
    public void Every_root_schema_compiles_from_bundled_documents_only() => Registry.WarmUp();

    [Theory]
    [InlineData("https://example.com/not-bundled.json")]
    [InlineData("https://spec.openapis.org/oas/3.2/schema/2025-09-17")]
    [InlineData("file:///etc/passwd")]
    public void An_unbundled_reference_is_refused_and_not_fetched(string reference)
    {
        var schema = $$"""{"$schema":"https://json-schema.org/draft/2020-12/schema","$ref":"{{reference}}"}""";
        Assert.Throws<JsonSchemaCompilationException>(() => Registry.CompileText(schema, $"https://schemas.gozleme.invalid/test/{Guid.NewGuid():N}.json"));
    }

    [Fact]
    public void Failures_reduce_to_unknown_field_and_schema_problems_without_messages()
    {
        var problems = Registry.Validate(SchemaIds.OpenApi31,
            Encoding.UTF8.GetBytes("""{"openapi":"3.1.2","info":{"title":5,"version":"1"},"paths":{"/a":{"get":{"responses":{"200":{"description":"ok"}},"nope":1}}}}"""), "native");
        Assert.Equal(
            ["native.schema /info/title", "native.unknown-field /paths/~1a/get/nope"],
            problems.Select(p => $"{p.Rule.Id} {p.Location!.Pointer}").Order(StringComparer.Ordinal));
    }

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"^\| `(?<file>[^`]+)` \|[^|]*\|[^|]*\| `(?<sha>[0-9a-f]{64})` \|", RegexOptions.Multiline)]
    private static partial Regex Row();
}
