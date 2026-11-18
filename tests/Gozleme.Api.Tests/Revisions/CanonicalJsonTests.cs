using System.Text;
using Gozleme.Api.Revisions;

namespace Gozleme.Api.Tests.Revisions;

/// <summary>
/// GOZ-0007 T032 (research R3, contracts/attestation-statement-v1.md "Canonical bytes"): the RFC 8785 subset. Vectors
/// are the number-free parts of RFC 8785 Appendix B and section 3.2.3, plus the subset's own refusals.
/// </summary>
public sealed class CanonicalJsonTests
{
    private static string Canonical(string json) => Encoding.UTF8.GetString(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Whitespace_is_removed_and_members_are_sorted_recursively()
    {
        Assert.Equal("""{"a":[3,{"b":1,"c":2}],"z":null}""", Canonical("""{ "z" : null, "a" : [ 3 , { "c": 2, "b": 1 } ] }"""));
        Assert.Equal("[true,false,null,\"\"]", Canonical(" [ true, false, null, \"\" ] "));
    }

    [Fact]
    public void Members_sort_by_UTF16_code_units_not_code_points()
    {
        // RFC 8785 section 3.2.3: U+1F600 (surrogates D83D DE00) sorts before U+FB33 although its code point is larger.
        var json = "{\"\\ufb33\":1,\"\\ud83d\\ude00\":2,\"\\u20ac\":3,\"\\r\":4,\"1\":5,\"\\u00f6\":6}";
        // Escaped so editor normalization cannot decompose U+FB33 in the source.
        Assert.Equal("{\"\\r\":4,\"1\":5,\"\u00f6\":6,\"\u20ac\":3,\"\U0001F600\":2,\"\uFB33\":1}", Canonical(json));
    }

    [Fact]
    public void Strings_escape_as_ECMAScript_JSON_stringify()
    {
        Assert.Equal("\"\\\"\\\\\\b\\f\\n\\r\\t\\u0000\\u001f\\u000b\"", Canonical("\"\\\"\\\\\\b\\f\\n\\r\\t\\u0000\\u001F\\u000B\""));
        // Everything else is literal UTF-8, including '/', DEL, U+2028 and non-BMP characters.
        Assert.Equal("\"/\u007f\u2028€😀\"", Canonical("\"\\/\\u007f\\u2028\\u20AC\\uD83D\\uDE00\""));
    }

    [Fact]
    public void Integers_are_written_in_shortest_form_within_2_pow_53()
    {
        Assert.Equal("[0,-1,9007199254740991,-9007199254740991]", Canonical("[0, -1, 9007199254740991, -9007199254740991]"));
        Assert.Equal("0", Canonical("-0"));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData("9007199254740992")]
    [InlineData("-9007199254740992")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("\"\\ud800\"")]
    [InlineData("{\"\\udc00\":1}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("not json")]
    public void Values_outside_the_subset_are_refused(string json)
    {
        Assert.Throws<CanonicalJsonException>(() => CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(json)));
        Assert.False(CanonicalJson.IsCanonical(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void A_byte_order_mark_and_invalid_UTF8_are_refused()
    {
        Assert.Throws<CanonicalJsonException>(() => CanonicalJson.Canonicalize([0xEF, 0xBB, 0xBF, (byte)'1']));
        Assert.Throws<CanonicalJsonException>(() => CanonicalJson.Canonicalize([(byte)'"', 0xC3, (byte)'"']));
    }

    [Fact]
    public void IsCanonical_holds_exactly_for_the_canonical_bytes()
    {
        Assert.True(CanonicalJson.IsCanonical("""{"a":1,"b":[2]}"""u8));
        Assert.False(CanonicalJson.IsCanonical("""{"b":[2],"a":1}"""u8));
        Assert.False(CanonicalJson.IsCanonical("""{"a": 1}"""u8));
        Assert.False(CanonicalJson.IsCanonical("\"\\u00e9\""u8));
    }

    [Fact]
    public void Canonicalizing_is_idempotent()
    {
        var once = CanonicalJson.Canonicalize("""{"x":{"z":"\u00e9","y":[1,2]},"a":"\n"}"""u8);
        Assert.Equal(once, CanonicalJson.Canonicalize(once));
    }
}
