using Gozleme.Api.Manifests;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gozleme.Api.Revisions;

/// <summary>
/// Publications per account per clock minute (GOZ-0007 FR-019, research R15): the same SQL and shape as
/// <see cref="ManifestValidationLimiter"/> over <c>manifest_publication_attempts</c>, so every API replica shares one
/// budget and a restart does not reset it. Acquired after membership, permission, target and suspension, so a
/// non-member, a forbidden caller or a suspended target never spends or sees <c>429</c>. It stores an account, a
/// window and a count, never manifest data.
/// </summary>
internal sealed class RevisionPublicationLimiter(NpgsqlDataSource dataSource, IOptions<ManifestOptions> options)
{
    private const string Sql = """
        WITH w AS (SELECT date_trunc('minute', now()) AS start),
        expired AS (
          DELETE FROM gozleme.manifest_publication_attempts
          WHERE account_id = $1 AND window_start < (SELECT start FROM w)),
        counted AS (
          INSERT INTO gozleme.manifest_publication_attempts AS a (account_id, window_start, attempts)
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
        command.Parameters.Add(new NpgsqlParameter { Value = options.Value.PublishPerMinute });
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetBoolean(0) ? null : Math.Max(1, reader.GetInt32(1));
    }
}
