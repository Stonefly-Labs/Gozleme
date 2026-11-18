namespace Gozleme.Api.Revisions;

/// <summary>
/// The SemVer 2.0.0 precedence key of a declared version (GOZ-0007 research R8): the version without any <c>+build</c>
/// suffix. Precedence ignores build metadata, and GOZ-0006 has already proven the version valid (no leading zeros in
/// numeric identifiers), so two versions have equal precedence exactly when their keys are equal strings.
/// </summary>
internal static class VersionKey
{
    public static string From(string semver)
    {
        var plus = semver.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? semver : semver[..plus];
    }
}
