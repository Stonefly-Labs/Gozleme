using System.Diagnostics;
using Gozleme.Api.Identity;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Revisions.Signing;
using Gozleme.Api.Telemetry;
using Npgsql;

namespace Gozleme.Api.Revisions;

/// <summary>
/// Operator signing-key succession (GOZ-0007 FR-007a, contracts/signing-keys.md, research R9), run like <c>migrate</c>
/// and never reachable over HTTP. It connects as the owner (<c>ConnectionStrings:GozlemeMigrator</c>) with no account
/// context, which is what <c>is_operator()</c> recognises, and needs the current KEK. In one transaction, under the
/// exclusive form of the per-publisher signing lock that publications hold shared, it reads the active key
/// (generation n), creates n + 1 under the current KEK and inserts the retirement of n. Old keys are never changed and
/// still verify what they signed. Output, audit and spans carry opaque IDs only.
/// </summary>
internal static class SigningKeyOperatorCommands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int UnknownTarget = 2;
    public const int Usage = 64;

    public const string Command = "signing-key-succession";

    private const string UsageText = "Usage: signing-key-succession --publisher <uuid> --reason lost|compromised\n       signing-key-succession --undecryptable --reason lost";

    public static bool Handles(string? command) => command == Command;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, ILoggerProvider? logs = null)
    {
        var options = args.Where(a => !(a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=', StringComparison.Ordinal))).ToArray();
        if (!TryParse(options, out var publisher, out var reason))
        {
            await stderr.WriteLineAsync(UsageText);
            return Usage;
        }

        // Only `--Key=Value` arguments are configuration; command options use `--name value`.
        var builder = Host.CreateApplicationBuilder(args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=', StringComparison.Ordinal)).ToArray());
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o => o.UseUtcTimestamp = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs); // tests capture the audit events
        }

        builder.AddGozlemeTelemetry(HandleOperatorCommands.ServiceName);
        builder.Services.AddSingleton<SecurityAudit>();
        builder.Services.AddOptions<SigningOptions>().Bind(builder.Configuration.GetSection(SigningOptions.Section));
        builder.Services.AddSingleton<SigningKeyVault>();
        using var host = builder.Build();
        await host.StartAsync(); // telemetry providers; disposing flushes exporters
        using var activity = PublisherTelemetry.StartOperator(Command);
        var code = await ExecuteAsync(host.Services, publisher, reason!, stdout, stderr);
        activity?.SetTag(PublisherTelemetry.ExitCodeTag, code);
        if (code == Failed)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        await host.StopAsync();
        return code;
    }

    /// <summary><paramref name="publisher"/> null means <c>--undecryptable</c>.</summary>
    private static async Task<int> ExecuteAsync(IServiceProvider services, Guid? publisher, string reason, TextWriter stdout, TextWriter stderr)
    {
        var audit = services.GetRequiredService<SecurityAudit>();
        var vault = services.GetRequiredService<SigningKeyVault>();
        if (!vault.IsConfigured)
        {
            audit.SigningKeySuccessionRefused("kek_unavailable");
            await stderr.WriteLineAsync("The signing key-encryption key is missing or invalid.");
            return Failed;
        }

        var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(MigrationRunner.MigratorConnectionStringName);
        if (string.IsNullOrEmpty(connectionString))
        {
            await stderr.WriteLineAsync($"Connection string '{MigrationRunner.MigratorConnectionStringName}' is not configured.");
            return Failed;
        }

        try
        {
            await using var dataSource = new NpgsqlDataSourceBuilder(connectionString) { Name = HandleOperatorCommands.ServiceName }.Build();
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var targets = publisher is { } one ? [one] : await UndecryptableAsync(connection, tx, vault);
            var done = new List<(Guid Publisher, Guid Retired, Guid Successor)>();
            foreach (var target in targets)
            {
                if (await SucceedAsync(connection, tx, vault, target, reason) is not { } succession)
                {
                    await tx.RollbackAsync();
                    audit.SigningKeySuccessionRefused("unknown_target");
                    await stderr.WriteLineAsync("Unknown publisher, or the publisher has no signing key yet.");
                    return UnknownTarget;
                }

                done.Add((target, succession.Retired, succession.Successor));
            }

            await tx.CommitAsync();
            foreach (var (target, retired, successor) in done)
            {
                audit.SigningKeySucceeded(retired, successor, target, reason);
                await stdout.WriteLineAsync($"{target} retired {retired} successor {successor}");
            }

            return Ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            audit.SigningKeySuccessionRefused("failed");
            await stderr.WriteLineAsync($"Operator command failed ({ex.GetType().Name}).");
            return Failed;
        }
    }

    /// <summary>Publishers whose active key the current KEK cannot unwrap (for example after a KEK change).</summary>
    private static async Task<List<Guid>> UndecryptableAsync(NpgsqlConnection connection, NpgsqlTransaction tx, SigningKeyVault vault)
    {
        var undecryptable = new List<Guid>();
        await foreach (var key in ActiveKeysAsync(connection, tx, null))
        {
            if (!vault.CanDecrypt(key))
            {
                undecryptable.Add(key.PublisherId);
            }
        }

        return undecryptable;
    }

    private static async Task<(Guid Retired, Guid Successor)?> SucceedAsync(NpgsqlConnection connection, NpgsqlTransaction tx, SigningKeyVault vault, Guid publisher, string reason)
    {
        // Publications in flight hold the shared form: they commit with the old key first or sign with the new one.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('gozleme.signing:' || $1, 0))", connection, tx))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter { Value = publisher.ToString() });
            await lockCommand.ExecuteNonQueryAsync();
        }

        PublisherSigningKey? active = null;
        await foreach (var key in ActiveKeysAsync(connection, tx, publisher))
        {
            active = key;
        }

        if (active is null || !vault.TryCreateKey(publisher, out var fresh))
        {
            return null;
        }

        var successor = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO gozleme.publisher_signing_keys (id, organization_id, publisher_id, generation, key_id, algorithm, public_key, encrypted_private_key, created_at, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, now(), NULL)
            """, connection, tx))
        {
            foreach (var value in new object[] { successor, active.OrganizationId, publisher, active.Generation + 1, fresh!.KeyId, Ed25519Signer.Algorithm, fresh.PublicKey, fresh.EncryptedPrivateKey })
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = value });
            }

            await insert.ExecuteNonQueryAsync();
        }

        await using (var retire = new NpgsqlCommand(
            "INSERT INTO gozleme.signing_key_retirements (key_id, organization_id, successor_key_id, reason, retired_at) VALUES ($1, $2, $3, $4, now())", connection, tx))
        {
            foreach (var value in new object[] { active.Id, active.OrganizationId, successor, reason })
            {
                retire.Parameters.Add(new NpgsqlParameter { Value = value });
            }

            await retire.ExecuteNonQueryAsync();
        }

        return (active.Id, successor);
    }

    /// <summary>Unretired keys, one per publisher (the highest generation), optionally for one publisher.</summary>
    private static async IAsyncEnumerable<PublisherSigningKey> ActiveKeysAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid? publisher)
    {
        await using var select = new NpgsqlCommand("""
            SELECT k.id, k.organization_id, k.publisher_id, k.generation, k.key_id, k.public_key, k.encrypted_private_key
            FROM gozleme.publisher_signing_keys k
            WHERE NOT EXISTS (SELECT 1 FROM gozleme.signing_key_retirements r WHERE r.key_id = k.id) AND ($1::uuid IS NULL OR k.publisher_id = $1)
            ORDER BY k.publisher_id, k.generation
            """, connection, tx);
        select.Parameters.Add(new NpgsqlParameter { Value = (object?)publisher ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            yield return new PublisherSigningKey
            {
                Id = reader.GetGuid(0), OrganizationId = reader.GetGuid(1), PublisherId = reader.GetGuid(2), Generation = reader.GetInt32(3),
                KeyId = reader.GetString(4), Algorithm = Ed25519Signer.Algorithm, PublicKey = reader.GetFieldValue<byte[]>(5),
                EncryptedPrivateKey = reader.GetFieldValue<byte[]>(6),
            };
        }
    }

    private static bool TryParse(string[] args, out Guid? publisher, out string? reason)
    {
        publisher = null;
        reason = null;
        var undecryptable = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--undecryptable" when !undecryptable:
                    undecryptable = true;
                    break;
                case "--publisher" when publisher is null && i + 1 < args.Length && Guid.TryParse(args[i + 1], out var id):
                    publisher = id;
                    i++;
                    break;
                case "--reason" when reason is null && i + 1 < args.Length && args[i + 1] is "lost" or "compromised":
                    reason = args[++i];
                    break;
                default:
                    return false;
            }
        }

        // Exactly one target form; the contract allows only `lost` for `--undecryptable`.
        return reason is not null && (publisher is not null ^ undecryptable) && (publisher is not null || reason == "lost");
    }
}
