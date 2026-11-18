using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gozleme.Api.Manifests.Rules;
using static Gozleme.Api.Tests.Manifests.ManifestTestKit;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// GOZ-0006 contracts/secret-patterns.md (research R7, FR-010): one synthetic positive per pattern, near misses, the
/// accepted forms in secret-bearing fields, <c>secret.name</c> at the parent pointer, and a timeout as a match.
/// </summary>
[Collection(TimedTests.Name)]
public sealed class SecretPatternTests
{
    // Prefixes are concatenated so the repository's own secret scan never sees a contiguous credential shape.
    public static TheoryData<string, string> Positives => new()
    {
        { "pem-private-key", "-----BEGIN RSA PRIVATE KEY-----" },
        { "auth-header", "Authorization: Bearer SYNTHETICSYNTHETIC" },
        { "jwt", "eyJSYNTHETIC0.eyJSYNTHETIC0.SYNTHETIC" },
        { "url-userinfo", "postgres://demo:SYNTHETIC@db.example.test/x" },
        { "aws-access-key-id", "AKIA" + "EXAMPLEAAAAAAAAA" },
        { "github-token", "ghp_" + new string('A', 36) },
        { "gitlab-token", "glpat-" + "SYNTHETICSYNTHETIC00" },
        { "slack-token", "xoxb-" + "1111111111-SYNTHETIC" },
        { "stripe-key", "sk_test_" + new string('0', 24) },
        { "google-api-key", "AIza" + new string('A', 35) },
        { "anthropic-key", "sk-ant-" + "SYNTHETICSYNTHETIC0000" },
        { "openai-key", "sk-proj-" + "SYNTHETICSYNTHETIC0000" },
        { "npm-token", "npm_" + new string('E', 36) },
        { "azure-connection-secret", "DefaultEndpointsProtocol=https;AccountKey=SYNTHETICSYNTHETIC==" },
    };

    [Theory]
    [MemberData(nameof(Positives))]
    public void Each_pattern_matches_its_synthetic_example(string id, string value)
    {
        Assert.Equal(id, SecretPatterns.Match(value)?.Id);
        Assert.Matches("EXAMPLE|SYNTHETIC|AAAA|0000|1111|EEEE|BEGIN", value);
    }

    [Fact]
    public void The_list_is_the_contract_list_in_order()
    {
        Assert.Equal(
            ["pem-private-key", "auth-header", "jwt", "url-userinfo", "aws-access-key-id", "github-token", "gitlab-token", "slack-token",
             "stripe-key", "google-api-key", "anthropic-key", "openai-key", "npm-token", "azure-connection-secret"],
            SecretPatterns.All.Select(p => p.Id));
        Assert.All(SecretPatterns.All, p => Assert.Equal(RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, p.Pattern.Options));
    }

    [Theory]
    [InlineData("0193a000-0000-7000-8000-000000000011")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("Bearer token")]
    [InlineData("Use a Bearer header.")]
    [InlineData("https://forecasts.example.test/mcp")]
    [InlineData("https://user@forecasts.example.test/")]
    [InlineData("sk-short")]
    [InlineData("secretref:vault/forecasts/api-key")]
    [InlineData("{FORECAST_API_KEY}")]
    public void Near_misses_do_not_match(string value) => Assert.Null(SecretPatterns.Match(value));

    [Theory]
    [InlineData("", true)]
    [InlineData("secretref:vault/forecasts/api-key", true)]
    [InlineData("secretref:a/" + "k", true)]
    [InlineData("{FORECAST_API_KEY}", true)]
    [InlineData("{_x1}", true)]
    [InlineData("secretref:Vault/key", false)]
    [InlineData("secretref:vault/", false)]
    [InlineData("secretref:vault/key with space", false)]
    [InlineData("{1X}", false)]
    [InlineData("plain-value", false)]
    public void Accepted_forms(string value, bool accepted) => Assert.Equal(accepted, SecretPatterns.IsAccepted(value));

    [Theory]
    [InlineData("secretref:vault/forecasts/api-key", null)]
    [InlineData("{FORECAST_API_KEY}", null)]
    [InlineData("", null)]
    [InlineData("plain-value", "secret_value secret.field native /remotes/0/headers/0/value")]
    public void A_secret_header_value_must_be_a_reference(string value, string? expected)
    {
        var doc = JsonNode.Parse(Mcp(extra: ""","remotes":[{"type":"streamable-http","url":"https://forecasts.example.test/mcp","headers":[{"name":"Authorization","isSecret":true}]}]"""))!;
        Accepted(Validate("mcp", "mcp-server-manifest", "1", "json", Text(doc))); // isSecret with no value
        doc["remotes"]![0]!["headers"]![0]!["value"] = value;
        var result = Validate("mcp", "mcp-server-manifest", "1", "json", Text(doc));
        if (expected is null)
        {
            Accepted(result);
        }
        else
        {
            Assert.Equal([expected], Problems(result));
        }
    }

    [Fact]
    public void A_credential_shaped_name_is_reported_at_its_parent_and_never_in_a_pointer()
    {
        var key = "ghp_" + new string('A', 36);
        var doc = """{"name":"m","description":"d","modalities":{"input":["text"],"output":["text"]},"x-tokens":{"KEY":1}}""".Replace("KEY", key, StringComparison.Ordinal);
        var problems = Problems(Validate("model", "gozleme-model-card", "1", "json", doc));
        Assert.Equal(["secret_value secret.name native /x-tokens"], problems);
        Assert.DoesNotContain(key, Json(Validate("model", "gozleme-model-card", "1", "json", doc)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_skill_body_secret_is_located_by_line_and_column()
    {
        var skill = "---\nname: demo\ndescription: d\n---\nline\n  token: ghp_" + new string('A', 36) + "\n";
        Assert.Equal(["secret_value secret.pattern native 6:10"], Problems(Validate("skill", "agent-skills", "2026-11-17", "markdown", skill)));
    }

    /// <summary>T078 (maintainer decision 2026-11-17): YAML comments are scanned and located by line and column, never echoed.</summary>
    [Fact]
    public void A_yaml_comment_secret_is_located_by_line_and_column()
    {
        var key = "ghp_" + new string('A', 36);
        var yaml = "openapi: 3.1.0\ninfo:\n  title: t\n  version: '1'\npaths: {}  # deploy with " + key + "\n# key: " + key + "\n";
        var result = Validate("api", "openapi", "3.1", "yaml", yaml);
        Assert.Equal(["secret_value secret.pattern native 5:26", "secret_value secret.pattern native 6:8"], Problems(result));
        Assert.DoesNotContain(key, Json(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_skill_front_matter_comment_secret_is_located_in_the_document()
    {
        var skill = "---\nname: demo\n# token " + "ghp_" + new string('A', 36) + "\ndescription: d\n---\nbody\n";
        Assert.Equal(["secret_value secret.pattern native 3:9"], Problems(Validate("skill", "agent-skills", "2026-11-17", "markdown", skill)));
    }

    [Fact]
    public void A_hash_inside_a_yaml_string_is_not_a_comment()
    {
        var yaml = "openapi: 3.1.0\ninfo:\n  title: 'a # ghp_" + new string('A', 36) + "'\n  version: '1'\npaths: {}\n";
        Assert.Equal(["secret_value secret.pattern native /info/title"], Problems(Validate("api", "openapi", "3.1", "yaml", yaml)));
    }

    [Fact]
    public void Envelope_strings_are_scanned_too()
    {
        var problems = Problems(Validate("model", "gozleme-model-card", "1", "json",
            """{"name":"m","description":"d","modalities":{"input":["text"],"output":["text"]}}""",
            e => e["summary"] = "Call with Bearer SYNTHETICSYNTHETIC"));
        Assert.Equal(["secret_value secret.pattern envelope /summary"], problems);
    }

    [Fact]
    public void A_timeout_counts_as_a_match()
    {
        var slow = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        Assert.Equal(("slow", 0), SecretPatterns.Match(new string('a', 40) + "!", [("slow", slow)]));
    }

    private static string OpenApiWith(string extension) =>
        """{"openapi":"3.1.0","info":{"title":"t","version":"1"},"paths":{},"x-z":""" + extension + "}";

    /// <summary>T091 (maintainer decision "reject and trim"): a credential that only appears once names are joined.</summary>
    [Fact]
    public void A_credential_split_across_nested_names_is_rejected_at_the_deepest_clean_parent()
    {
        var problems = Problems(Validate("api", "openapi", "3.1", "json", OpenApiWith("""{"https:":{"":{"u:pw@host":{"verified":1}}}}""")));
        Assert.Equal(["secret_value secret.name native /x-z/https:/"], problems);
        Assert.DoesNotContain(problems, p => p.Contains("pw@", StringComparison.Ordinal));
    }

    [Fact]
    public void Names_that_do_not_join_into_a_credential_stay_accepted() =>
        Accepted(Validate("api", "openapi", "3.1", "json", OpenApiWith("""{"https:":{"x":{"u:pw@host":1}},"bearer":{"short":1}}""")));

    /// <summary>Every pattern that can match across a joined name boundary (its source allows / or ~) is either checked on joined locations or deliberately exempt.</summary>
    [Fact]
    public void Every_pattern_that_can_span_names_is_joined_or_exempt()
    {
        var joined = SecretPatterns.Joined.Select(p => p.Id).ToHashSet();
        foreach (var (id, pattern) in SecretPatterns.All)
        {
            var source = pattern.ToString();
            Assert.True(!source.Contains('/') && !source.Contains('~') || joined.Contains(id) || SecretPatterns.NotJoined.Contains(id), $"{id} can span names but is neither joined nor exempt");
        }

        Assert.Equal(["url-userinfo", "azure-connection-secret"], SecretPatterns.Joined.Select(p => p.Id));

        // SecretPatterns.JoinedReach is worked out from these two sources; a change here means revisiting it.
        Assert.Equal(
            [@"(?i)\b[a-z][a-z0-9+.-]{1,31}://[^\s/:@]{1,256}:[^\s/@]{1,256}@", @"(?i)(?:AccountKey|SharedAccessKey|SharedAccessSignature)=[A-Za-z0-9+/%=&:-]{16,}"],
            SecretPatterns.Joined.Select(p => p.Pattern.ToString()));
    }

    /// <summary>T092 (maintainer decision "drop the header pattern"): honest names that only look like a header once joined stay accepted.</summary>
    [Fact]
    public void Header_words_in_names_are_not_joined_into_a_credential()
    {
        Accepted(Validate("api", "openapi", "3.1", "json",
            """{"openapi":"3.1.0","info":{"title":"t","version":"1"},"paths":{},"components":{"schemas":{"S":{"type":"object","properties":{"basic info":{"type":"string","description":"d"}}}}}}"""));
        Accepted(Validate("api", "openapi", "3.1", "json", OpenApiWith("""{"Bearer tokens":{"description":"how to obtain one"}}""")));
    }

    /// <summary>T092 (confirmation security review): the Kelvin sign and array positions.</summary>
    [Theory]
    [InlineData("""{"Account\u212Aey=abcdef":{"ghijklmnop":{"qrstuvwx":1}}}""", "/x-z/Account\u212Aey=abcdef")]
    [InlineData("""{"AccountKey=abcdefgh":[[[[{"q":1}]]]]}""", "/x-z/AccountKey=abcdefgh/0/0/0")] // 16 characters after = complete at the fourth index
    public void Joined_credentials_are_caught_through_case_folding_and_array_positions(string extension, string at)
    {
        var problems = Problems(Validate("api", "openapi", "3.1", "json", OpenApiWith(System.Text.RegularExpressions.Regex.Unescape(extension))));
        Assert.Equal([$"secret_value secret.name native {System.Text.RegularExpressions.Regex.Unescape(at)}"], problems);
    }

    /// <summary>T092: cost at the tree bounds with every location anchored (SC-006).</summary>
    [Fact]
    public void Joined_location_checks_stay_within_budget_at_the_bounds()
    {
        // Anchored at the top but never completed ('!' is outside the key's characters), so every location is scanned.
        var chain = new System.Text.StringBuilder("{\"AccountKey=\":");
        for (var d = 0; d < 3; d++)
        {
            chain.Append("{\"").Append('!', 1000).Append("\":");
        }

        chain.Append('[').Append(string.Join(',', Enumerable.Repeat("{\"k\":1}", 24_000))).Append(']').Append('}', 4);
        var doc = OpenApiWith(chain.ToString());
        Validate("api", "openapi", "3.1", "json", doc); // warm
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Validate("api", "openapi", "3.1", "json", doc);
        watch.Stop();
        Accepted(result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1L << 29, "allocated over 512 MiB");
    }
}
