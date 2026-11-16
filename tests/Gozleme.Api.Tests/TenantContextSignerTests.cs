using System.Security.Cryptography;
using System.Text;
using Gozleme.Api.Tenancy;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Tests;

/// <summary>GOZ-0003 contracts/tenant-context.md §1: the signed account context format and key rules.</summary>
public sealed class TenantContextSignerTests
{
    private const string Key = "unit-test-context-key-0003-not-a-secret";
    private static readonly Guid Account = Guid.Parse("0193A000-0000-7000-8000-0000000000AA");

    [Fact]
    public void Token_is_v1_lowercase_account_expiry_and_hex_hmac_over_the_payload()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var signer = new TenantContextSigner(Options.Create(new TenancyOptions { ContextKey = Key }), new FixedTime(now));

        var token = signer.Sign(Account);

        var parts = token.Split('.');
        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.Equal("0193a000-0000-7000-8000-0000000000aa", parts[1]);
        Assert.Equal((now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), parts[2]);
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}.{parts[2]}")));
        Assert.Equal(expected, parts[3]);
    }

    [Fact]
    public void Known_answer_vector_matches_an_independent_hmac_sha256()
    {
        // Pins the construction the database verifier implements (gozleme_private.verify_context).
        // Expected value computed independently with Python's hmac module.
        var token = TenantContextSigner.Sign(Encoding.UTF8.GetBytes(Key), Account, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        Assert.Equal("v1.0193a000-0000-7000-8000-0000000000aa.1800000000.b5a7b41708dde4fd1d29073e0640a225ff5fca73a0f163c8328d31327645eeb3", token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-key")]
    [InlineData("31-characters-is-one-too-short!")]
    public void Keys_shorter_than_32_characters_are_invalid(string? key) =>
        Assert.False(new TenancyOptions { ContextKey = key }.IsValid);

    [Fact]
    public void A_32_character_key_is_valid() => Assert.True(new TenancyOptions { ContextKey = new string('k', 32) }.IsValid);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
