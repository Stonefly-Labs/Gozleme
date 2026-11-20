using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gozleme.Api.Manifests;

/// <summary><c>Gozleme:Manifests</c> options (data-model.md "Configuration"; GOZ-0007 adds publication and paging, GOZ-0008 child paging).</summary>
internal sealed class ManifestOptions
{
    public const string Section = "Gozleme:Manifests";

    [Range(1, 10_000)]
    public int ValidatePerMinute { get; set; } = 30;

    /// <summary>Publications per account per clock minute (GOZ-0007 FR-019, research R15).</summary>
    [Range(1, 10_000)]
    public int PublishPerMinute { get; set; } = 10;

    /// <summary>Default revisions per list page; a request may ask for at most 100 (GOZ-0007 data-model.md).</summary>
    [Range(1, 100)]
    public int RevisionPageSize { get; set; } = 50;

    /// <summary>Default children, child-history and revision-children page size; a request may ask for at most 100 (GOZ-0008 data-model.md).</summary>
    [Range(1, 100)]
    public int ChildPageSize { get; set; } = 50;
}

/// <summary>
/// Validations per account per clock minute, counted in PostgreSQL so every API replica shares one budget and a restart
/// does not reset it (maintainer decision 2026-11-18, "shared-rate-limit", T099; the same shape as
/// <c>PublisherRegistrationLimiter</c>). Acquired by the handler after membership, permission, target and suspension,
/// so a non-member, a forbidden caller or a suspended target never spends or sees <c>429</c>. A refusal is recorded on
/// the manifest span and counter only, never through the shared sign-in <c>OnRejected</c> path (GOZ-0002-IF-08). The
/// count runs on its own connection and commits at once; the window comes from the database clock, and each call drops
/// the account's expired windows. It stores an account, a window and a count, never manifest data (FR-017).
/// </summary>
internal sealed class ManifestValidationLimiter(NpgsqlDataSource dataSource, IOptions<ManifestOptions> options)
{
    private const string Sql = """
        WITH w AS (SELECT date_trunc('minute', now()) AS start),
        expired AS (
          DELETE FROM gozleme.manifest_validation_attempts
          WHERE account_id = $1 AND window_start < (SELECT start FROM w)),
        counted AS (
          INSERT INTO gozleme.manifest_validation_attempts AS a (account_id, window_start, attempts)
          SELECT $1, start, 1 FROM w
          ON CONFLICT (account_id, window_start) DO UPDATE SET attempts = a.attempts + 1 WHERE a.attempts < $2
          RETURNING 1)
        SELECT (SELECT count(*) FROM counted) = 1,
               ceil(extract(epoch FROM (SELECT start FROM w) + interval '1 minute' - now()))::integer
        """;

    /// <summary><c>null</c> when a permit was taken; otherwise the seconds until the account's window ends (at least 1).</summary>
    public async Task<int?> TryAcquireAsync(Guid accountId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(Sql);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = options.Value.ValidatePerMinute });
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetBoolean(0) ? null : Math.Max(1, reader.GetInt32(1));
    }
}
