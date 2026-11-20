using System.Text.Json.Nodes;
using Gozleme.Api.Children;
using Gozleme.Api.Manifests;
using Gozleme.Api.Manifests.Projection;
using Gozleme.Api.Revisions;
using Gozleme.Api.Tests.Manifests;

namespace Gozleme.Api.Tests.Children;

/// <summary>
/// GOZ-0008 T014 (FR-005a, research R3, contracts/publish-delta.md): the publish-time duplicate rule reports every
/// entry of a duplicate group, the first included, by index, at a <c>projection</c> location, with a fixed message,
/// capped at 50 with <c>truncated</c>, and never echoes a name. Validate still accepts such bodies (GOZ-0006-IF-07).
/// T069 (maintainer ruling F3): <c>publish.nul_character</c> reports every projection string holding U+0000 at its
/// exact pointer, in document order, without the value.
/// </summary>
public sealed class ChildEntriesTests
{
    private static ProjectionEntry E(string kind, string name) => new(kind, name, null);

    [Fact]
    public void Each_entry_of_a_duplicate_group_is_reported_by_index()
    {
        var problems = ChildEntries.Duplicates([E("tool", "a"), E("tool", "b"), E("tool", "a"), E("resource", "a"), E("tool", "b"), E("tool", "c")]);
        Assert.Equal(["projection /entries/0", "projection /entries/1", "projection /entries/2", "projection /entries/4"], problems.Select(p => ManifestTestKit.Where(p.Location)));
        Assert.All(problems, p => Assert.Same(PublishRules.DuplicateEntry, p.Rule));
    }

    [Fact]
    public void No_duplicate_means_no_problem()
    {
        Assert.Empty(ChildEntries.Duplicates([E("tool", "a"), E("resource", "a"), E("tool", "A")]));
        Assert.Empty(ChildEntries.Duplicates([]));
    }

    [Fact]
    public void The_rejected_body_has_the_fixed_rule_category_and_message_and_echoes_no_name()
    {
        const string secretish = "do-not-echo-this-name";
        var body = ManifestTestKit.Json(ChildEntries.Rejected(ChildEntries.Duplicates([E("skill", secretish), E("skill", secretish)])));
        Assert.Equal(
            """{"outcome":"rejected","problems":[{"category":"invalid_value","rule":"publish.duplicate_entry","location":{"part":"projection","pointer":"/entries/0"},"message":"Two or more projection entries have the same kind and native name."},{"category":"invalid_value","rule":"publish.duplicate_entry","location":{"part":"projection","pointer":"/entries/1"},"message":"Two or more projection entries have the same kind and native name."}],"truncated":false}""",
            body);
        Assert.DoesNotContain(secretish, body, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_50_problems_are_capped_and_marked_truncated()
    {
        var rejected = ChildEntries.Rejected(ChildEntries.Duplicates([.. Enumerable.Repeat(E("tool", "same"), 60)]));
        Assert.Equal(50, rejected.Problems.Count);
        Assert.True(rejected.Truncated);
        Assert.Equal("/entries/49", rejected.Problems[^1].Location!.Pointer);
    }

    [Fact]
    public void The_publish_catalogue_is_separate_and_the_GOZ_0006_catalogue_is_unchanged()
    {
        Assert.Equal(["publish.duplicate_entry", "publish.nul_character"], PublishRules.All.Select(r => r.Id));
        Assert.Equal("1", PublishRules.CatalogueVersion);
        Assert.Equal(40, ManifestRules.All.Count);
        Assert.DoesNotContain(ManifestRules.All, r => r.Id.StartsWith("publish.", StringComparison.Ordinal));
        Assert.Equal("1", ManifestRules.CatalogueVersion);
    }

    [Fact]
    public void Every_projection_string_with_U0000_is_reported_at_its_pointer_without_the_value()
    {
        const string document = """
            {"protocolRevision":"2026-07-28","serverInfo":{"name":"io.example/nul","version":"1.0.0","description":"D\u0000"},
             "capabilities":{"tools":{}},"tools":[{"name":"ok","inputSchema":{"type":"object"}},{"name":"a\u0000b","title":"T\u0000","inputSchema":{"type":"object"}}]}
            """;
        var accepted = ManifestTestKit.Accepted(ManifestTestKit.Validate("mcp", "mcp-server-manifest", "1", "json", document));
        var problems = ChildEntries.NulCharacters(ManifestJson.ProjectionNode(accepted.Projection));
        Assert.Equal(["projection /description", "projection /entries/1/nativeName", "projection /entries/1/title"], problems.Select(p => ManifestTestKit.Where(p.Location)));
        Assert.All(problems, p => Assert.Equal(PublishRules.NulCharacter, p.Rule));
        var body = ManifestTestKit.Json(ChildEntries.Rejected(problems));
        Assert.DoesNotContain("\\u0000", body, StringComparison.Ordinal);
        Assert.Contains("\"rule\":\"publish.nul_character\"", body, StringComparison.Ordinal);

        Assert.True(ChildEntries.HasNulCharacter(CanonicalJson.Canonicalize(ManifestJson.ProjectionNode(accepted.Projection))));
        Assert.False(ChildEntries.HasNulCharacter("""{"entries":[{"kind":"tool","nativeName":"a0b"}],"title":"\\u0000 is text"}"""u8));
        Assert.Empty(ChildEntries.NulCharacters(JsonNode.Parse("""{"entries":[],"title":null}""")));
    }

    [Fact]
    public void Every_envelope_string_with_U0000_is_reported_at_part_envelope_without_the_value()
    {
        var envelope = new EnvelopeSummary(1, "mcp", ManifestTestKit.CanonicalId, "1.0.0", "T\0", "S\0", new NativeDeclaration("mcp-server-manifest", "1", "json"));
        var problems = ChildEntries.EnvelopeNulCharacters(envelope);
        Assert.Equal(["envelope /title", "envelope /summary"], problems.Select(p => ManifestTestKit.Where(p.Location)));
        Assert.All(problems, p => Assert.Equal(PublishRules.NulCharacter, p.Rule));
        Assert.DoesNotContain("\\u0000", ManifestTestKit.Json(ChildEntries.Rejected(problems)), StringComparison.Ordinal);

        Assert.Empty(ChildEntries.EnvelopeNulCharacters(envelope with { Title = "T", Summary = null }));
    }

    [Fact]
    public void An_a2a_card_with_two_skills_of_one_id_is_accepted_by_validate_and_has_a_duplicate_group()
    {
        var sample = ManifestSamples.Get("valid-agent-card");
        var envelope = sample.Envelope(ManifestTestKit.CanonicalId);
        var card = JsonNode.Parse(envelope["native"]!["document"]!.GetValue<string>())!.AsObject();
        var skills = card["skills"]!.AsArray();
        var copy = skills[0]!.DeepClone().AsObject();
        copy["name"] = "Another skill title";
        skills.Add(copy);
        envelope["native"]!["document"] = card.ToJsonString();

        var accepted = ManifestTestKit.Accepted(ManifestTestKit.Validate(System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString())));
        var problems = ChildEntries.Duplicates(accepted.Projection.Entries);
        Assert.Equal(["projection /entries/0", $"projection /entries/{skills.Count - 1}"], problems.Select(p => ManifestTestKit.Where(p.Location)));
    }

    [Fact]
    public void An_api_derived_method_and_path_name_equal_to_another_operation_id_is_a_duplicate()
    {
        const string document = """
            {"openapi":"3.1.1","info":{"title":"Synthetic","version":"1.0.0"},"paths":{
              "/a":{"get":{"responses":{"200":{"description":"ok"}}}},
              "/b":{"get":{"operationId":"GET /a","responses":{"200":{"description":"ok"}}}}}}
            """;
        var accepted = ManifestTestKit.Accepted(ManifestTestKit.Validate("api", "openapi", "3.1", "json", document));
        Assert.Equal(["GET /a", "GET /a"], accepted.Projection.Entries.Select(e => e.NativeName));
        Assert.Equal(2, ChildEntries.Duplicates(accepted.Projection.Entries).Count);
    }

    [Fact]
    public void Stored_projection_entries_are_read_back_with_absent_titles_as_null()
    {
        var entries = ChildEntries.FromProjection("""{"entries":[{"kind":"tool","nativeName":"a","title":"A"},{"kind":"prompt","nativeName":"p"}],"kind":"mcp-server"}"""u8);
        Assert.Equal([new ProjectionEntry("tool", "a", "A"), new ProjectionEntry("prompt", "p", null)], entries);
        Assert.Empty(ChildEntries.FromProjection("{}"u8));
    }
}
