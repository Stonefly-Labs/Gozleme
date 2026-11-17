using System.Diagnostics;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Telemetry;
using Npgsql;

namespace Gozleme.Api.Identity;

/// <summary>
/// Operator handle commands (GOZ-0005 FR-032–FR-037, contracts/operator-commands.md): <c>handle-reserve</c>,
/// <c>handle-release</c> and <c>handle-reclaim</c>, run like <c>migrate</c> and never reachable over HTTP. They connect
/// as the owner (<c>ConnectionStrings:GozlemeMigrator</c>, maintainer Q11) with no account context, which is what
/// <c>handle_claims_guard</c> recognises as the operator, and write only <c>gozleme.handle_claims</c>. Output, audit
/// and spans carry opaque ids only: the handle is the operator's own input and is never logged or traced.
/// </summary>
internal static class HandleOperatorCommands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int UnknownTarget = 2;
    public const int InvalidHandle = 3;
    public const int NotFree = 4;
    public const int Usage = 64;

    public const string ServiceName = "gozleme-operator";

    public static bool Handles(string? command) => command is "handle-reserve" or "handle-release" or "handle-reclaim";

    public static async Task<int> RunAsync(string command, string[] args, TextWriter stdout, TextWriter stderr, ILoggerProvider? logs = null)
    {
        var usage = command switch
        {
            "handle-reserve" => "Usage: handle-reserve --handle <handle> [--organization <id>]",
            "handle-release" => "Usage: handle-release --handle <handle>",
            _ => "Usage: handle-reclaim --handle <handle> [--organization <id>]",
        };
        var options = args.Where(a => !(a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=', StringComparison.Ordinal))).ToArray();
        if (!TryParse(options, command != "handle-release", out var handle, out var organization))
        {
            await stderr.WriteLineAsync(usage);
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

        builder.AddGozlemeTelemetry(ServiceName);
        builder.Services.AddSingleton<SecurityAudit>();
        using var host = builder.Build();
        await host.StartAsync(); // telemetry providers; disposing flushes exporters
        using var activity = PublisherTelemetry.StartOperator(command);
        var code = await ExecuteAsync(host.Services, command, handle!, organization, stdout, stderr);
        activity?.SetTag(PublisherTelemetry.ExitCodeTag, code);
        if (code == Failed)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        await host.StopAsync();
        return code;
    }

    private static async Task<int> ExecuteAsync(IServiceProvider services, string command, string handle, Guid? organization, TextWriter stdout, TextWriter stderr)
    {
        if (!NamespaceRules.IsValidHandle(handle))
        {
            await stderr.WriteLineAsync($"Invalid handle: {NamespaceRules.RuleText.Handle}");
            return InvalidHandle;
        }

        var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(MigrationRunner.MigratorConnectionStringName);
        if (string.IsNullOrEmpty(connectionString))
        {
            await stderr.WriteLineAsync($"Connection string '{MigrationRunner.MigratorConnectionStringName}' is not configured.");
            return Failed;
        }

        var audit = services.GetRequiredService<SecurityAudit>();
        try
        {
            await using var dataSource = new NpgsqlDataSourceBuilder(connectionString) { Name = ServiceName }.Build();
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            switch (command)
            {
                case "handle-reserve":
                    {
                        var claim = Guid.CreateVersion7();
                        await using var insert = new NpgsqlCommand(
                            "INSERT INTO gozleme.handle_claims (id, handle, status, organization_id) VALUES ($1, $2, $3, $4)", connection, tx);
                        insert.Parameters.Add(new NpgsqlParameter { Value = claim });
                        insert.Parameters.Add(new NpgsqlParameter { Value = handle });
                        insert.Parameters.Add(new NpgsqlParameter { Value = organization is null ? "blocked" : "earmarked" });
                        insert.Parameters.Add(new NpgsqlParameter { Value = (object?)organization ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
                        await insert.ExecuteNonQueryAsync();
                        await tx.CommitAsync();
                        audit.HandleReserved(claim, organization is null ? "blocked" : "earmarked", organization);
                        await stdout.WriteLineAsync(organization is null
                            ? $"Reserved handle claim {claim} (blocked)"
                            : $"Reserved handle claim {claim} (earmarked for organization {organization})");
                        return Ok;
                    }

                case "handle-release":
                    {
                        await using var release = new NpgsqlCommand(
                            "UPDATE gozleme.handle_claims SET status = 'released', ended_at = now() WHERE handle = $1 AND status IN ('blocked', 'earmarked') RETURNING id",
                            connection, tx);
                        release.Parameters.Add(new NpgsqlParameter { Value = handle });
                        if (await release.ExecuteScalarAsync() is not Guid claim)
                        {
                            await stderr.WriteLineAsync("No unused reservation for that handle.");
                            return UnknownTarget;
                        }

                        await tx.CommitAsync();
                        audit.HandleReservationReleased(claim);
                        await stdout.WriteLineAsync($"Released handle claim {claim}");
                        return Ok;
                    }

                default:
                    {
                        await using var reclaim = new NpgsqlCommand(
                            "UPDATE gozleme.handle_claims SET status = 'reclaimed', ended_at = now() WHERE handle = $1 AND status = 'held' RETURNING id, publisher_id, organization_id",
                            connection, tx);
                        reclaim.Parameters.Add(new NpgsqlParameter { Value = handle });
                        Guid claim, publisher, owner;
                        await using (var reader = await reclaim.ExecuteReaderAsync())
                        {
                            if (!await reader.ReadAsync())
                            {
                                await stderr.WriteLineAsync("No held claim for that handle.");
                                return UnknownTarget;
                            }

                            (claim, publisher, owner) = (reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
                        }

                        Guid? earmark = null;
                        if (organization is { } target)
                        {
                            earmark = Guid.CreateVersion7();
                            await using var insert = new NpgsqlCommand(
                                "INSERT INTO gozleme.handle_claims (id, handle, status, organization_id) VALUES ($1, $2, 'earmarked', $3)", connection, tx);
                            insert.Parameters.Add(new NpgsqlParameter { Value = earmark.Value });
                            insert.Parameters.Add(new NpgsqlParameter { Value = handle });
                            insert.Parameters.Add(new NpgsqlParameter { Value = target });
                            await insert.ExecuteNonQueryAsync();
                        }

                        await tx.CommitAsync();
                        audit.HandleReclaimed(claim, publisher, owner, earmark);
                        await stdout.WriteLineAsync($"Reclaimed handle claim {claim}; publisher {publisher} of organization {owner} suspended");
                        if (earmark is not null)
                        {
                            await stdout.WriteLineAsync($"Reserved handle claim {earmark} (earmarked for organization {organization})");
                        }

                        return Ok;
                    }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            await stderr.WriteLineAsync("No such organization.");
            return UnknownTarget;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await stderr.WriteLineAsync("That handle is already held or reserved.");
            return NotFree;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            await stderr.WriteLineAsync($"Invalid handle: {NamespaceRules.RuleText.Handle}");
            return InvalidHandle;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await stderr.WriteLineAsync($"Operator command failed ({ex.GetType().Name}).");
            return Failed;
        }
    }

    private static bool TryParse(string[] args, bool allowOrganization, out string? handle, out Guid? organization)
    {
        handle = null;
        organization = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
            {
                return false;
            }

            switch (args[i])
            {
                case "--handle" when handle is null:
                    handle = args[++i];
                    break;
                case "--organization" when allowOrganization && organization is null && Guid.TryParse(args[i + 1], out var id):
                    organization = id;
                    i++;
                    break;
                default:
                    return false;
            }
        }

        return handle is not null;
    }
}
