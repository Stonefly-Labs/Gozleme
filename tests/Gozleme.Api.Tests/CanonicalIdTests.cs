using System.Text.RegularExpressions;
using Gozleme.Api.Publishers;

namespace Gozleme.Api.Tests;

/// <summary>
/// GOZ-0005 FR-004, FR-013 (contracts/namespace.md): identifiers are the origin plus a lowercase, hyphenated,
/// 36-character UUID, and never embed an organization id, a handle, a capability name or a display name.
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
        // The renderer takes only the origin and the id: an organization, handle or name cannot reach it.
        foreach (var method in typeof(CanonicalId).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            Assert.Equal([typeof(string), typeof(Guid)], method.GetParameters().Select(p => p.ParameterType));
        }

        var organization = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        Assert.DoesNotContain(organization.ToString(), CanonicalId.Capability(Origin, id), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^gz://registry\\.demo\\.gozleme\\.test/(pub|cap)/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex Rendered();
}
