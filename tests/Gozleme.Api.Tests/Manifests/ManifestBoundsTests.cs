using System.Text;
using System.Text.Json.Nodes;
using Gozleme.Api.Manifests;
using static Gozleme.Api.Tests.Manifests.ManifestTestKit;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// GOZ-0006 FR-016 (contracts/manifest-validation.md "Bounds"): each bound accepts its limit and refuses one past it as
/// <c>too_large</c> with its rule and a null location; <c>too_large</c> and <c>malformed</c> stop with one problem.
/// </summary>
public sealed class ManifestBoundsTests
{
    private const string ModelHead = """{"name":"m","description":"d","modalities":{"input":["text"],"output":["text"]},"x-pad":""";

    private static string[] Rules(ValidationResult result) => result is RejectedResult r ? [.. r.Problems.Select(p => p.Rule)] : [];

    private static void TooLarge(ValidationResult result, string rule)
    {
        var rejected = Assert.IsType<RejectedResult>(result);
        var problem = Assert.Single(rejected.Problems);
        Assert.Equal(("too_large", rule), (problem.Category, problem.Rule));
        Assert.Null(problem.Location);
    }

    private static string Model(int totalBytes)
    {
        var pad = totalBytes - Encoding.UTF8.GetByteCount(ModelHead) - 3; // the quotes and the closing brace
        return ModelHead + "\"" + new string('a', pad) + "\"}";
    }

    [Fact]
    public void The_native_document_may_be_512_KiB_and_not_one_byte_more()
    {
        var atLimit = Model(ManifestValidator.MaxNativeBytes);
        Assert.Equal(ManifestValidator.MaxNativeBytes, Encoding.UTF8.GetByteCount(atLimit));
        Accepted(Validate("model", "gozleme-model-card", "1", "json", atLimit));
        TooLarge(Validate("model", "gozleme-model-card", "1", "json", Model(ManifestValidator.MaxNativeBytes + 1)), "bound.native");
    }

    [Fact]
    public void The_envelope_without_its_document_may_be_16_KiB_and_not_one_byte_more()
    {
        byte[] Body(int envelopeBytes)
        {
            var bare = Envelope("model", "gozleme-model-card", "1", "json", Model(200), e => e["zpad"] = string.Empty);
            var documentToken = Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(Model(200))) - 2;
            var pad = envelopeBytes - (bare.Length - documentToken);
            return Envelope("model", "gozleme-model-card", "1", "json", Model(200), e => e["zpad"] = new string('p', pad));
        }

        Assert.DoesNotContain("bound.envelope", Rules(Validate(Body(EnvelopeReader.MaxEnvelopeBytes))));
        Assert.Equal(["envelope.unknown-field"], Rules(Validate(Body(EnvelopeReader.MaxEnvelopeBytes))));
        TooLarge(Validate(Body(EnvelopeReader.MaxEnvelopeBytes + 1)), "bound.envelope");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_part_may_nest_64_levels_and_not_65(bool envelope)
    {
        // Root object = level 1; the padding member adds nested arrays below it.
        static string Nested(int levels) => new string('[', levels) + new string(']', levels);
        ValidationResult Run(int arrays) => envelope
            ? Validate(Envelope("model", "gozleme-model-card", "1", "json", Model(200), e => e["zpad"] = JsonNode.Parse(Nested(arrays))))
            : Validate("model", "gozleme-model-card", "1", "json", ModelHead + "\"\",\"x-deep\":" + Nested(arrays) + "}");
        Assert.DoesNotContain("bound.depth", Rules(Run(63)));
        TooLarge(Run(64), "bound.depth");
    }

    [Fact]
    public void A_native_tree_may_hold_50000_values_and_not_50001()
    {
        // root + modalities object + 2 arrays + 2 items + 4 strings/members... count exactly: build an x- array of n numbers.
        string Doc(int numbers) => """{"name":"m","description":"d","modalities":{"input":["text"],"output":["text"]},"x-n":[""" + string.Join(',', Enumerable.Repeat('0', numbers)) + "]}";
        const int Fixed = 10; // root, name, description, modalities, input, "text", output, "text", x-n
        Assert.DoesNotContain("bound.nodes", Rules(Validate("model", "gozleme-model-card", "1", "json", Doc(50_000 - Fixed + 1))));
        TooLarge(Validate("model", "gozleme-model-card", "1", "json", Doc(50_000 - Fixed + 2)), "bound.nodes");
    }

    [Fact]
    public void A_member_name_may_have_1024_characters_and_not_1025()
    {
        string Doc(int length) => ModelHead + "\"\",\"x-" + new string('n', length - 2) + "\":1}";
        Accepted(Validate("model", "gozleme-model-card", "1", "json", Doc(1024)));
        TooLarge(Validate("model", "gozleme-model-card", "1", "json", Doc(1025)), "bound.name");
    }

    /// <summary>T079 (maintainer decision 2026-11-17): a number may have 1,024 characters and not 1,025, in JSON and YAML, refused before conversion.</summary>
    [Theory]
    [InlineData("json", "", 1024, true)]
    [InlineData("json", "", 1025, false)]
    [InlineData("yaml", "", 1024, true)]
    [InlineData("yaml", "", 1025, false)]
    [InlineData("yaml", "0o", 1025, false)]
    [InlineData("yaml", "0x", 1025, false)]
    public void A_number_may_have_1024_characters_and_not_1025(string encoding, string prefix, int length, bool accepted)
    {
        var number = prefix + "1" + new string('7', length - prefix.Length - 1);
        var doc = encoding == "json"
            ? """{"openapi":"3.1.0","info":{"title":"t","version":"1"},"paths":{},"x-n":""" + number + "}"
            : "openapi: 3.1.0\ninfo:\n  title: t\n  version: '1'\npaths: {}\nx-n: " + number + "\n";
        var result = Validate("api", "openapi", "3.1", encoding, doc);
        if (accepted)
        {
            Accepted(result);
        }
        else
        {
            TooLarge(result, "bound.number");
        }
    }

    [Fact]
    public void A_huge_yaml_number_is_refused_quickly()
    {
        var doc = "openapi: 3.1.0\ninfo:\n  title: t\n  version: '1'\npaths: {}\nx-n: 0o" + new string('7', 400_000) + "\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TooLarge(Validate("api", "openapi", "3.1", "yaml", doc), "bound.number");
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_family_may_declare_1000_entries_and_not_1001()
    {
        string Doc(int tools) => Mcp(extra: string.Empty).Replace(
            """[{"name":"get_forecast","inputSchema":{"type":"object"}}]""",
            "[" + string.Join(',', Enumerable.Range(0, tools).Select(i => "{\"name\":\"t" + i + "\",\"inputSchema\":{\"type\":\"object\"}}")) + "]",
            StringComparison.Ordinal);
        Accepted(Validate("mcp", "mcp-server-manifest", "1", "json", Doc(1000)));
        TooLarge(Validate("mcp", "mcp-server-manifest", "1", "json", Doc(1001)), "bound.entries");
    }

    [Fact]
    public void At_most_50_problems_are_returned_then_truncated()
    {
        string Doc(int unknown) => ModelHead + "\"\"" + string.Concat(Enumerable.Range(0, unknown).Select(i => $",\"u{i:D2}\":1")) + "}";
        var fifty = Assert.IsType<RejectedResult>(Validate("model", "gozleme-model-card", "1", "json", Doc(50)));
        Assert.Equal((50, false), (fifty.Problems.Count, fifty.Truncated));
        var more = Assert.IsType<RejectedResult>(Validate("model", "gozleme-model-card", "1", "json", Doc(60)));
        Assert.Equal((50, true), (more.Problems.Count, more.Truncated));
        Assert.Equal("/u00", more.Problems[0].Location!.Pointer);
        Assert.Equal("/u49", more.Problems[49].Location!.Pointer);
    }

    [Fact]
    public void Malformed_and_too_large_stop_with_exactly_one_problem()
    {
        var malformed = Assert.IsType<RejectedResult>(Validate("model", "gozleme-model-card", "1", "json", "{\"name\":", e => e["extra"] = 1));
        Assert.Equal(["malformed parse.syntax native 1:9"], Problems(malformed));
        var body = Encoding.UTF8.GetBytes("{\"envelopeVersion\":1,\"family\":\"model\",");
        Assert.Equal(["malformed parse.syntax envelope 1:38"], Problems(Validate(body)));
    }
}
