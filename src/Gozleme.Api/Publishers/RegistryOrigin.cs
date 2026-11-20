using Microsoft.Extensions.Options;
using Npgsql;

namespace Gozleme.Api.Publishers;

/// <summary>
/// The configured registry origin (<c>Gozleme:Registry:Origin</c>; GOZ-0005 FR-001–FR-003, research R6). The
/// migrator records it once; the API refuses to start when it is missing, invalid or different.
/// </summary>
internal sealed class RegistryOriginOptions
{
    public const string Section = "Gozleme:Registry";

    public string? Origin { get; set; }
}

/// <summary>
/// The origin rule (contracts/namespace.md): a lowercase ASCII host name of dot-separated labels, each 1–63
/// characters of <c>[a-z0-9-]</c> and not starting or ending with <c>-</c>, at most 253 characters, with no
/// scheme, path, port, user information or trailing dot. Ordinal ASCII checks only.
/// </summary>
internal static class RegistryOrigin
{
    public const int MaxLength = 253;
    public const int MaxLabelLength = 63;

    /// <summary>The rule as a PostgreSQL <c>CHECK</c> on <c>registry_origin.origin</c>.</summary>
    public const string Check =
        "length(origin) <= 253 AND origin ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$'";

    public const string InvalidMessage =
        "Gozleme:Registry:Origin must be a lowercase host name (labels of a-z, 0-9 and inner hyphens, at most 253 characters, no scheme, port or path).";

    public const string MismatchMessage = "The configured origin differs from the recorded origin; nothing was changed.";

    public static bool IsValid(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || origin.Length > MaxLength)
        {
            return false;
        }

        var labelStart = 0;
        for (var i = 0; i <= origin.Length; i++)
        {
            if (i < origin.Length && origin[i] != '.')
            {
                if (origin[i] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                {
                    return false;
                }

                continue;
            }

            var length = i - labelStart;
            if (length is 0 or > MaxLabelLength || origin[labelStart] == '-' || origin[i - 1] == '-')
            {
                return false;
            }

            labelStart = i + 1;
        }

        return true;
    }
}

/// <summary>Rendered identifiers (contracts/namespace.md): only the UUID is stored; the origin is fixed and verified.</summary>
internal static class CanonicalId
{
    public static string Publisher(string origin, Guid id) => $"gz://{origin}/pub/{id:D}";

    public static string Capability(string origin, Guid id) => $"gz://{origin}/cap/{id:D}";

    /// <summary>A child ID (GOZ-0008 maintainer Q1, contracts/child-identity.md): only the child UUID is stored; no alias.</summary>
    public static string Child(string origin, Guid capabilityId, string kind, Guid childId) => $"gz://{origin}/cap/{capabilityId:D}/{kind}/{childId:D}";
}

/// <summary>Whether the recorded origin matches the configured one.</summary>
internal enum OriginState
{
    Verified,
    Unrecorded,
    Mismatch,
}

/// <summary>
/// The one read path for <c>gozleme.registry_origin</c> outside migrations (FR-002): a parameterless
/// <c>SELECT</c> as <c>gozleme_app</c>, which holds only <c>SELECT</c> on it. Pinned by
/// <c>RegistryOriginReadPathTests</c>. The verified value is cached; until then publisher endpoints answer
/// <c>503 unavailable</c>, and a mismatch found after start stops the application (research R6).
/// </summary>
internal sealed partial class RegistryOriginReader(
    NpgsqlDataSource dataSource, IOptions<RegistryOriginOptions> options, IHostApplicationLifetime lifetime, ILogger<RegistryOriginReader> logger)
{
    private volatile string? _verified;

    public string Configured => options.Value.Origin!;

    /// <summary>The verified origin, or null while it cannot be verified. A database outage propagates (503).</summary>
    public async Task<string?> GetVerifiedOriginAsync(CancellationToken ct)
    {
        if (_verified is { } origin)
        {
            return origin;
        }

        var state = await VerifyAsync(ct);
        if (state == OriginState.Mismatch)
        {
            LogMismatch(logger);
            lifetime.StopApplication();
        }

        return state == OriginState.Verified ? _verified : null;
    }

    public async Task<OriginState> VerifyAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT origin FROM gozleme.registry_origin", connection);
        var recorded = await command.ExecuteScalarAsync(ct) as string;
        if (recorded is null)
        {
            return OriginState.Unrecorded;
        }

        if (!string.Equals(recorded, Configured, StringComparison.Ordinal))
        {
            return OriginState.Mismatch;
        }

        _verified = recorded;
        return OriginState.Verified;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The configured origin differs from the recorded origin; stopping")]
    private static partial void LogMismatch(ILogger logger);
}

/// <summary>
/// Blocking start check (FR-003): a mismatch throws, so the host does not start. An unreachable database is
/// tolerated so the GOZ-0000 outage behaviour holds; verification then happens on first use.
/// </summary>
internal sealed partial class RegistryOriginCheck(RegistryOriginReader reader, ILogger<RegistryOriginCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        OriginState state;
        try
        {
            state = await reader.VerifyAsync(cancellationToken);
        }
        catch (Exception ex) when (DatabaseUnavailable.UnreachableCause(ex) is not null)
        {
            LogDeferred(logger);
            return;
        }

        if (state == OriginState.Mismatch)
        {
            throw new InvalidOperationException(RegistryOrigin.MismatchMessage);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database unreachable at start; registry origin verification deferred")]
    private static partial void LogDeferred(ILogger logger);
}
