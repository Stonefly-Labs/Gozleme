namespace Gozleme.Api.Revisions.Signing;

/// <summary>
/// <c>Gozleme:Signing</c> options (GOZ-0007 research R6, contracts/signing-keys.md). <see cref="KeyEncryptionKey"/> is
/// base64 of exactly 32 random bytes, supplied by Secret reference (Compose <c>GOZLEME_SIGNING_KEK</c>, Helm
/// <c>signing.existingSecret</c>) and never stored in the database or Data Protection. It is deliberately not
/// validated on start: absent or malformed means publication answers <c>503 unavailable</c> while reads and
/// verification keep working (spec edge case "Signing key unavailable"). Only <c>SigningKeyVault</c> reads it.
/// </summary>
internal sealed class SigningOptions
{
    public const string Section = "Gozleme:Signing";

    public string? KeyEncryptionKey { get; set; }

    /// <summary>Never renders the key: options objects reach debuggers and diagnostics.</summary>
    public override string ToString() => $"{nameof(SigningOptions)} {{ {nameof(KeyEncryptionKey)} = {(string.IsNullOrEmpty(KeyEncryptionKey) ? "absent" : "set")} }}";
}
