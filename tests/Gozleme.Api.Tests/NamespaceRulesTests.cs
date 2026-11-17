using System.Globalization;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;

namespace Gozleme.Api.Tests;

/// <summary>
/// GOZ-0005 US4 (FR-001, FR-016–FR-018, research R6–R7, contracts/namespace.md): one ordinal rule, never normalized,
/// run under the repository-wide <c>InvariantGlobalization=true</c>, and the same rule in the table <c>CHECK</c>s.
/// </summary>
public sealed class NamespaceRulesTests
{
    public static TheoryData<string> ValidHandles => ["ab", "a1", "acme-tools", "a-1-b-2", "x" + new string('9', 38)];

    public static TheoryData<string> InvalidValues =>
    [
        string.Empty, "a", "Acme", "ACME", "acİme", "acıme", "аcme", "сap", "ａcme", "acme tools", " acme", "acme ", "acme\t",
        "-acme", "acme-", "acme--tools", "1acme", "9", "acme_tools", "acme.tools", "acme/tools", "acme­", "é", "é",
    ];

    [Fact]
    public void The_suite_runs_with_invariant_globalization() =>
        Assert.True(AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) && invariant, CultureInfo.CurrentCulture.Name);

    [Theory]
    [MemberData(nameof(ValidHandles))]
    public void Valid_values_pass_as_handles_and_as_names(string value)
    {
        Assert.True(NamespaceRules.IsValidHandle(value));
        Assert.True(NamespaceRules.IsValidCapabilityName(value));
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void Invalid_values_are_refused_never_normalized(string value)
    {
        Assert.False(NamespaceRules.IsValidHandle(value));
        Assert.False(NamespaceRules.IsValidCapabilityName(value));
    }

    [Fact]
    public void Lengths_are_2_to_39_for_handles_and_2_to_64_for_names()
    {
        Assert.True(NamespaceRules.IsValidHandle(new string('a', 39)));
        Assert.False(NamespaceRules.IsValidHandle(new string('a', 40)));
        Assert.True(NamespaceRules.IsValidCapabilityName(new string('a', 64)));
        Assert.False(NamespaceRules.IsValidCapabilityName(new string('a', 65)));
        Assert.False(NamespaceRules.IsValidHandle(null));
        Assert.False(NamespaceRules.IsValidCapabilityName(null));
    }

    [Fact]
    public void Every_reserved_handle_is_refused_as_a_handle_but_not_as_a_name()
    {
        string[] expected = ["admin", "api", "capabilities", "capability", "gozleme", "operator", "publisher", "publishers", "registry", "root", "security", "support", "system", "www"];
        Assert.Equal(expected, NamespaceRules.ReservedHandles.Order(StringComparer.Ordinal));
        Assert.All(expected, h => Assert.False(NamespaceRules.IsValidHandle(h)));
        Assert.All(expected, h => Assert.True(NamespaceRules.IsValidCapabilityName(h)));
    }

    [Fact]
    public void The_table_checks_carry_the_same_rule_and_reserved_list()
    {
        Assert.Contains($"handle ~ '{NamespaceRules.Pattern}'", GozlemeDbContext.HandleCheck, StringComparison.Ordinal);
        Assert.Contains($"BETWEEN {NamespaceRules.MinLength} AND {NamespaceRules.MaxHandleLength}", GozlemeDbContext.HandleCheck, StringComparison.Ordinal);
        Assert.Contains($"name ~ '{NamespaceRules.Pattern}'", GozlemeDbContext.CapabilityNameCheck, StringComparison.Ordinal);
        Assert.Contains($"BETWEEN {NamespaceRules.MinLength} AND {NamespaceRules.MaxCapabilityNameLength}", GozlemeDbContext.CapabilityNameCheck, StringComparison.Ordinal);
        var reserved = string.Join(", ", NamespaceRules.ReservedHandles.Order(StringComparer.Ordinal).Select(h => $"'{h}'"));
        Assert.Contains($"handle <> ALL (ARRAY[{reserved}])", GozlemeDbContext.HandleCheck, StringComparison.Ordinal);
    }

    private static readonly string Longest = string.Join('.', Enumerable.Repeat(new string('a', 63), 3)) + "." + new string('b', 61);

    public static TheoryData<string> ValidOrigins =>
    [
        "registry.demo.gozleme.test", "localhost", "a", "x1", "registry-1.example.com", "0.example",
        Longest, // exactly 253
        new string('a', 63) + ".test",
    ];

    public static TheoryData<string> InvalidOrigins =>
    [
        string.Empty, "Registry.example.com", "https://registry.example.com", "registry.example.com:443", "registry.example.com/path",
        "user@registry.example.com", "registry.example.com.", ".registry.example.com", "registry..example.com", "-registry.example.com",
        "registry-.example.com", "registry.example.com-", "regi_stry.example.com", "registry example.com", "régistry.example.com",
        Longest + "b", // 254
        new string('a', 64) + ".test",
    ];

    [Theory]
    [MemberData(nameof(ValidOrigins))]
    public void Valid_origins_pass(string origin) => Assert.True(RegistryOrigin.IsValid(origin), $"{origin.Length}: {origin}");

    [Theory]
    [MemberData(nameof(InvalidOrigins))]
    public void Invalid_origins_are_refused(string origin) => Assert.False(RegistryOrigin.IsValid(origin), $"{origin.Length}: {origin}");

    [Fact]
    public void The_origin_boundaries_are_253_characters_and_63_per_label()
    {
        Assert.Equal(253, Longest.Length);
        Assert.True(RegistryOrigin.IsValid(Longest));
        Assert.False(RegistryOrigin.IsValid(Longest + "b"));
        Assert.True(RegistryOrigin.IsValid(new string('a', 63)));
        Assert.False(RegistryOrigin.IsValid(new string('a', 64)));
        Assert.False(RegistryOrigin.IsValid(null));
    }
}
