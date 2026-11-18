using System.Text;
using Gozleme.Api.Revisions;
using Gozleme.Api.Revisions.Signing;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Tests.Revisions;

/// <summary>GOZ-0007 T034: the SemVer precedence key (research R8) and the AES-256-GCM key vault (research R6).</summary>
public sealed class SigningKeyVaultTests
{
    private static readonly string Kek = Convert.ToBase64String(Encoding.ASCII.GetBytes("unit-test-key-encryption-key-32b"));
    private static readonly string OtherKek = Convert.ToBase64String(Encoding.ASCII.GetBytes("another-key-encryption-key-32byt"));

    private static SigningKeyVault Vault(string? kek) => new(Options.Create(new SigningOptions { KeyEncryptionKey = kek }));

    private static PublisherSigningKey Row(Guid publisherId, NewSigningKey key) => new()
    {
        Id = Guid.CreateVersion7(), PublisherId = publisherId, Generation = 1, KeyId = key.KeyId, Algorithm = Ed25519Signer.Algorithm,
        PublicKey = key.PublicKey, EncryptedPrivateKey = key.EncryptedPrivateKey,
    };

    [Theory]
    [InlineData("1.0.0+a", "1.0.0+b", true)]
    [InlineData("1.0.0", "1.0.0+build.7", true)]
    [InlineData("1.0.0-rc.1", "1.0.0", false)]
    [InlineData("1.0.0-rc.1+x", "1.0.0-rc.1", true)]
    [InlineData("1.2.5", "2.0.0", false)]
    public void Versions_share_a_key_exactly_when_their_precedence_is_equal(string a, string b, bool same) =>
        Assert.Equal(same, VersionKey.From(a) == VersionKey.From(b));

    [Fact]
    public void A_created_key_round_trips_and_signs_verifiably()
    {
        var vault = Vault(Kek);
        var publisher = Guid.CreateVersion7();
        Assert.True(vault.TryCreateKey(publisher, out var key));
        Assert.Equal(SigningKeyVault.BlobLength, key!.EncryptedPrivateKey.Length);
        Assert.Equal(61, SigningKeyVault.BlobLength);
        Assert.Equal(1, key.EncryptedPrivateKey[0]);
        Assert.Equal(Ed25519Signer.KeyId(key.PublicKey), key.KeyId);
        var row = Row(publisher, key);
        Assert.True(vault.TrySign(row, "statement"u8, out var signature));
        Assert.True(Ed25519Signer.Verify(row.PublicKey, "statement"u8, signature));
        Assert.True(vault.CanDecrypt(row));
    }

    [Fact]
    public void A_blob_moved_to_another_publisher_or_key_does_not_decrypt()
    {
        var vault = Vault(Kek);
        var publisher = Guid.CreateVersion7();
        vault.TryCreateKey(publisher, out var key);
        vault.TryCreateKey(publisher, out var other);
        var row = Row(publisher, key!);

        var movedPublisher = Row(Guid.CreateVersion7(), key!);
        Assert.False(vault.TrySign(movedPublisher, "x"u8, out _));
        var movedKey = Row(publisher, key!);
        movedKey.KeyId = other!.KeyId;
        movedKey.PublicKey = other.PublicKey;
        Assert.False(vault.TrySign(movedKey, "x"u8, out _));
        var swappedBlob = Row(publisher, other);
        swappedBlob.EncryptedPrivateKey = row.EncryptedPrivateKey;
        Assert.False(vault.TrySign(swappedBlob, "x"u8, out _));
        var flipped = Row(publisher, key!);
        flipped.EncryptedPrivateKey = [.. row.EncryptedPrivateKey];
        flipped.EncryptedPrivateKey[20] ^= 1;
        Assert.False(vault.TrySign(flipped, "x"u8, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQ=")] // 5 bytes
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")] // 33 bytes
    public void An_absent_or_malformed_KEK_is_signing_unavailable(string? kek)
    {
        var vault = Vault(kek);
        Assert.False(vault.IsConfigured);
        Assert.False(vault.TryCreateKey(Guid.CreateVersion7(), out var key));
        Assert.Null(key);
        Vault(Kek).TryCreateKey(Guid.Empty, out var made);
        Assert.False(vault.TrySign(Row(Guid.Empty, made!), "x"u8, out _));
    }

    [Fact]
    public void A_wrong_KEK_is_signing_unavailable_without_an_exception()
    {
        var publisher = Guid.CreateVersion7();
        Vault(Kek).TryCreateKey(publisher, out var key);
        var wrong = Vault(OtherKek);
        Assert.False(wrong.TrySign(Row(publisher, key!), "x"u8, out var signature));
        Assert.Null(signature);
        Assert.False(wrong.CanDecrypt(Row(publisher, key!)));
    }

    [Fact]
    public void No_key_material_appears_in_ToString()
    {
        var vault = Vault(Kek);
        vault.TryCreateKey(Guid.CreateVersion7(), out var key);
        var options = new SigningOptions { KeyEncryptionKey = Kek };
        foreach (var text in new[] { vault.ToString(), key!.ToString(), options.ToString() })
        {
            Assert.DoesNotContain(Kek, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(key.EncryptedPrivateKey), text, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexString(key.EncryptedPrivateKey), text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
