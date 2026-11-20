using System.Text.RegularExpressions;
using Gozleme.Api.Publishers;

namespace Gozleme.Api.Tests;

/// <summary>
/// GOZ-0005 FR-004, FR-013 (contracts/namespace.md): identifiers are the origin plus a lowercase, hyphenated,
/// 36-character UUID, and never embed an organization id, a handle, a capability name or a display name. GOZ-0008 adds
/// the child ID (maintainer Q1): the capability ID, the closed entry kind and the child UUID, never a native name.
/// </summary>
public sealed partial class CanonicalIdTests
{
    private const string Origin = "registry.demo.gozleme.test";

    [Fact]
    public void Publisher_and_capability_ids_render_as_origin_kind_and_uuid()
    {
        var id = Guid.CreateVersion7();
        Assert.Equal($"gz://{Origin}/pub/{id:D}", CanonicalId.Publisher(Origin, id));
        Assert.Equal($"gz://{Origin}/cap/{id:D}", CanonicalId.Capability(Origin, id));
        Assert.Matches(Rendered(), CanonicalId.Publisher(Origin, id));
        Assert.Matches(Rendered(), CanonicalId.Capability(Origin, id));
    }

    [Fact]
    public void The_uuid_part_is_lowercase_hyphenated_and_36_characters()
    {
        var id = Guid.Parse("0193A5C2-7D1E-7C3A-9F00-1A2B3C4D5E6F");
        var rendered = CanonicalId.Capability(Origin, id);
        var uuid = rendered[(rendered.LastIndexOf('/') + 1)..];
        Assert.Equal(36, uuid.Length);
        Assert.Equal("0193a5c2-7d1e-7c3a-9f00-1a2b3c4d5e6f", uuid);
    }

    [Fact]
    public void An_identifier_never_embeds_the_organization_or_a_name()
    {
        // The renderer takes only the origin and ids (and, for a child, its closed kind): an organization, handle or name cannot reach it.
        foreach (var method in typeof(CanonicalId).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            Type[] expected = method.Name == nameof(CanonicalId.Child) ? [typeof(string), typeof(Guid), typeof(string), typeof(Guid)] : [typeof(string), typeof(Guid)];
            Assert.Equal(expected, method.GetParameters().Select(p => p.ParameterType));
        }

        var organization = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        Assert.DoesNotContain(organization.ToString(), CanonicalId.Capability(Origin, id), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("resource")]
    [InlineData("resource-template")]
    [InlineData("prompt")]
    [InlineData("skill")]
    [InlineData("operation")]
    [InlineData("webhook")]
    [InlineData("channel")]
    public void A_child_id_is_the_capability_id_its_kind_and_the_child_uuid(string kind)
    {
        var (capability, child) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var rendered = CanonicalId.Child(Origin, capability, kind, child);
        Assert.Equal($"{CanonicalId.Capability(Origin, capability)}/{kind}/{child:D}", rendered);
        Assert.Matches(ChildRendered(), rendered);
        Assert.NotEqual(CanonicalId.Child(Origin, capability, kind, Guid.CreateVersion7()), rendered);
    }

    [GeneratedRegex("^gz://registry\\.demo\\.gozleme\\.test/cap/[0-9a-f-]{36}/(tool|resource|resource-template|prompt|skill|operation|webhook|channel)/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex ChildRendered();

    [GeneratedRegex("^gz://registry\\.demo\\.gozleme\\.test/(pub|cap)/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex Rendered();
}
