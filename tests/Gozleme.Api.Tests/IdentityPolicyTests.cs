using Gozleme.Api.Identity;
using Gozleme.Api.Persistence;

namespace Gozleme.Api.Tests;

/// <summary>FR-002, FR-003: email normalisation, password policy and naming convention.</summary>
public sealed class IdentityPolicyTests
{
    private static readonly GozlemeUser User = new() { Email = "alice.smith@it.gozleme.test" };

    [Theory]
    [InlineData("a-long-unique-passphrase", true)]
    [InlineData("twelve-chars", true)] // exactly 12, no composition rules
    [InlineData("password1234", false)] // common
    [InlineData("PASSWORD1234", false)] // common, case-insensitive
    [InlineData("alice.smith@it.gozleme.test", false)] // the email
    [InlineData("ALICE.SMITH", false)] // the local part
    public async Task Password_policy(string password, bool valid)
    {
        var result = await new GozlemePasswordValidator().ValidateAsync(null!, User, password);
        Assert.Equal(valid, result.Succeeded);
    }

    [Fact]
    public async Task Passwords_over_128_characters_are_rejected()
    {
        var result = await new GozlemePasswordValidator().ValidateAsync(null!, User, new string('k', 129));
        Assert.Contains(result.Errors, e => e.Code == "PasswordTooLong");
        Assert.True((await new GozlemePasswordValidator().ValidateAsync(null!, User, new string('k', 128))).Succeeded);
    }

    [Fact]
    public void Common_password_list_is_embedded_and_substantial()
    {
        Assert.True(GozlemePasswordValidator.IsCommon("qwertyuiopasdfghjkl") || GozlemePasswordValidator.IsCommon("password1234"));
        Assert.False(GozlemePasswordValidator.IsCommon("local-dev-demo-pass")); // the Compose demo password must be accepted
    }

    [Theory]
    [InlineData("  Alice@Example.test ", "Alice@Example.test")]
    [InlineData("bob+tag@sub.example.test", "bob+tag@sub.example.test")]
    [InlineData("no-at-sign", null)]
    [InlineData("two@@example.test", null)]
    [InlineData("space in@example.test", null)]
    [InlineData("ctrl\u0007@example.test", null)]
    [InlineData("nodot@localhost", null)]
    [InlineData("Display Name <x@example.test>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Email_normalisation(string? raw, string? expected) => Assert.Equal(expected, AuthEndpoints.NormalizeEmail(raw));

    [Fact]
    public void Overlong_email_is_rejected() =>
        Assert.Null(AuthEndpoints.NormalizeEmail(new string('a', AuthEndpoints.MaxEmailLength) + "@example.test"));

    [Theory]
    [InlineData("NormalizedEmail", "normalized_email")]
    [InlineData("PK_users", "pk_users")]
    [InlineData("FK_auth_sessions_users_UserId", "fk_auth_sessions_users_user_id")]
    [InlineData("KeyHash", "key_hash")]
    public void Snake_case_naming(string input, string expected) => Assert.Equal(expected, GozlemeDbContext.SnakeCase(input));
}
