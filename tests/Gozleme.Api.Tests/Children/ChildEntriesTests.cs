using System.Text.Json.Nodes;
using Gozleme.Api.Children;
using Gozleme.Api.Manifests;
using Gozleme.Api.Manifests.Projection;
using Gozleme.Api.Tests.Manifests;

namespace Gozleme.Api.Tests.Children;

/// <summary>
/// GOZ-0008 T014 (FR-005a, research R3, contracts/publish-delta.md): the publish-time duplicate rule reports every
/// entry of a duplicate group, the first included, by index, at a <c>projection</c> location, with a fixed message,
/// capped at 50 with <c>truncated</c>, and never echoes a name. Validate still accepts such bodies (GOZ-0006-IF-07).
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
        Assert.Equal(["publish.duplicate_entry"], PublishRules.All.Select(r => r.Id));
        Assert.Equal("1", PublishRules.CatalogueVersion);
        Assert.Equal(40, ManifestRules.All.Count);
        Assert.DoesNotContain(ManifestRules.All, r => r.Id.StartsWith("publish.", StringComparison.Ordinal));
        Assert.Equal("1", ManifestRules.CatalogueVersion);
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
