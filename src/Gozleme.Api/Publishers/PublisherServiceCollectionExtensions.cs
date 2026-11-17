using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gozleme.Api.Publishers;

/// <summary>
/// Publisher composition (GOZ-0005): the validated registry origin with its blocking start check (research R6),
/// validated publisher options, the store and the per-account registration limiter (research R8).
/// </summary>
internal static class PublisherServiceCollectionExtensions
{
    public static WebApplicationBuilder AddGozlemePublishers(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<RegistryOriginOptions>().Bind(builder.Configuration.GetSection(RegistryOriginOptions.Section))
            .Validate(o => RegistryOrigin.IsValid(o.Origin), RegistryOrigin.InvalidMessage)
            .ValidateOnStart();
        builder.Services.AddOptions<PublisherOptions>().Bind(builder.Configuration.GetSection(PublisherOptions.Section))
            .Validate(o => Validator.TryValidateObject(o, new ValidationContext(o), null, validateAllProperties: true), $"{PublisherOptions.Section} values are outside their safe bounds.")
            .ValidateOnStart();
        builder.Services.AddSingleton<RegistryOriginReader>();
        builder.Services.AddHostedService<RegistryOriginCheck>();
        builder.Services.AddSingleton<PublisherRegistrationLimiter>();
        builder.Services.AddScoped<PublisherStore>();
        return builder;
    }
}

/// <summary>
/// Publisher registrations per account per clock hour, counted in PostgreSQL (FR-008, research R8) so every API replica
/// shares one budget and a restart does not reset it. Acquired by the handler after authorization (maintainer decision
/// U1), not by the rate-limiting middleware, so only a confirmed admin can spend or exhaust it. The count runs on its
/// own connection and commits at once: an attempt spends its permit even if the request later fails or rolls back.
/// The window comes from the database clock, and each call drops the account's expired windows.
/// </summary>
internal sealed class PublisherRegistrationLimiter(NpgsqlDataSource dataSource, IOptions<PublisherOptions> options)
{
    private const string Sql = """
        WITH w AS (SELECT date_trunc('hour', now()) AS start),
        expired AS (
          DELETE FROM gozleme.publisher_registration_attempts
          WHERE account_id = $1 AND window_start < (SELECT start FROM w)),
        counted AS (
          INSERT INTO gozleme.publisher_registration_attempts AS a (account_id, window_start, attempts)
          SELECT $1, start, 1 FROM w
          ON CONFLICT (account_id, window_start) DO UPDATE SET attempts = a.attempts + 1 WHERE a.attempts < $2
          RETURNING 1)
        SELECT (SELECT count(*) FROM counted) = 1,
               ceil(extract(epoch FROM (SELECT start FROM w) + interval '1 hour' - now()))::integer
        """;

    /// <summary><c>null</c> when a permit was taken; otherwise the seconds until the window ends (at least 1).</summary>
    public async Task<int?> TryAcquireAsync(Guid accountId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(Sql);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = options.Value.RegisterPerHour });
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetBoolean(0) ? null : Math.Max(1, reader.GetInt32(1));
    }
}
