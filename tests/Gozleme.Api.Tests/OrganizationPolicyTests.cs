using Gozleme.Api.Organizations;

namespace Gozleme.Api.Tests;

/// <summary>GOZ-0002 FR-002, FR-007 (research R5): invitation code format and normalisation, display-name rules.</summary>
public sealed class OrganizationPolicyTests
{
    [Fact]
    public void Codes_are_32_crockford_characters_in_groups_of_four()
    {
        var code = InvitationCodes.Generate();
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){7}$", code);
        Assert.Equal(InvitationCodes.Length, InvitationCodes.Normalize(code)!.Length);
    }

    [Fact]
    public void Codes_are_unique_across_many_draws()
    {
        var codes = Enumerable.Range(0, 2000).Select(_ => InvitationCodes.Generate()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(2000, codes.Count);
    }

    [Theory]
    [InlineData("abcd-efgh-jkmn-pqrs-tvwx-yz01-2345-6789", "ABCDEFGHJKMNPQRSTVWXYZ0123456789")] // lower case
    [InlineData("  ABCD EFGH JKMN PQRS TVWX YZ01 2345 6789 ", "ABCDEFGHJKMNPQRSTVWXYZ0123456789")] // whitespace
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZOI23456789", "ABCDEFGHJKMNPQRSTVWXYZ0123456789")] // O→0, I→1
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ0l23456789", "ABCDEFGHJKMNPQRSTVWXYZ0123456789")] // l→1
    public void Codes_are_normalised(string input, string expected) => Assert.Equal(expected, InvitationCodes.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD")] // too short
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ01234567890")] // too long
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ012345678U")] // U is not Crockford
    [InlineData("ABCDEFGHJKMNPQRSTVWXYZ012345678!")]
    public void Malformed_codes_are_rejected(string? input) => Assert.Null(InvitationCodes.Normalize(input));

    [Fact]
    public void Overlong_input_is_rejected_before_parsing() =>
        Assert.Null(InvitationCodes.Normalize(new string('-', 40) + "ABCDEFGHJKMNPQRSTVWXYZ0123456789"));

    [Fact]
    public void Hash_is_a_stable_32_byte_digest()
    {
        var a = InvitationCodes.Hash("ABCDEFGHJKMNPQRSTVWXYZ0123456789");
        Assert.Equal(32, a.Length);
        Assert.Equal(a, InvitationCodes.Hash("ABCDEFGHJKMNPQRSTVWXYZ0123456789"));
        Assert.NotEqual(a, InvitationCodes.Hash("ABCDEFGHJKMNPQRSTVWXYZ0123456788"));
    }

    [Theory]
    [InlineData("Demo Org", "Demo Org")]
    [InlineData("  Demo Org  ", "Demo Org")]
    [InlineData("<b>markup</b>", "<b>markup</b>")] // stored as text, rendered as text
    [InlineData("Ö", "Ö")]
    public void Valid_display_names_are_trimmed(string input, string expected)
    {
        var (name, error) = DisplayNames.Validate(input);
        Assert.Equal(expected, name);
        Assert.Null(error);
    }

    [Fact]
    public void Display_name_of_exactly_100_characters_is_allowed() => Assert.NotNull(DisplayNames.Validate(new string('a', 100)).Name);

    [Theory]
    [InlineData(null, DisplayNames.LengthError)]
    [InlineData("", DisplayNames.LengthError)]
    [InlineData("   ", DisplayNames.LengthError)]
    [InlineData("tab\there", DisplayNames.ControlError)]
    [InlineData("new\nline", DisplayNames.ControlError)]
    public void Invalid_display_names_are_rejected(string? input, string expected)
    {
        var (name, error) = DisplayNames.Validate(input);
        Assert.Null(name);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Display_name_over_100_characters_is_rejected() => Assert.Equal(DisplayNames.LengthError, DisplayNames.Validate(new string('a', 101)).Error);
}
