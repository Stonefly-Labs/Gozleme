using System.Diagnostics;
using Gozleme.Api.Identity;
using Gozleme.Api.Manifests.Adapters;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Revisions;
using Gozleme.Api.Telemetry;
using Npgsql;

namespace Gozleme.Api.Children;

/// <summary>
/// Operator re-projection (GOZ-0008 FR-011a, contracts/projection-generations.md, research R7; maintainer Q3 option B
/// and A4), run like <c>migrate</c> and never reachable over HTTP. It connects as the owner
/// (<c>ConnectionStrings:GozlemeMigrator</c>) with no account context, which <c>is_operator()</c> recognises, and needs
/// no KEK. In one transaction under the per-revision lock <c>gozleme.projections:&lt;revision&gt;</c> it reads the
/// revision, re-projects the stored native bytes with the stored adapter (<see cref="ProjectionRegenerator"/>) and
/// inserts the unattested generation N + 1, even when it is byte-identical to generation N (clarify Q2). Every child
/// keeps its ID. Output, audit and spans carry opaque IDs only: never a digest, name or document.
/// </summary>
internal static class ProjectionOperatorCommands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int UnknownTarget = 2;
    public const int Refused = 3;
    public const int Usage = 64;

    public const string Command = "projection-regenerate";

    private const string UsageText = "Usage: projection-regenerate --revision <uuid>";

    public static bool Handles(string? command) => command == Command;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, ILoggerProvider? logs = null)
    {
        var options = args.Where(a => !(a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=', StringComparison.Ordinal))).ToArray();
        if (options is not ["--revision", var value] || !Guid.TryParse(value, out var revision))
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
        using var host = builder.Build();
        await host.StartAsync(); // telemetry providers; disposing flushes exporters
        using var activity = PublisherTelemetry.StartOperator(Command);
        var code = await ExecuteAsync(host.Services, revision, stdout, stderr);
        activity?.SetTag(PublisherTelemetry.ExitCodeTag, code);
        if (code is Failed or UnknownTarget or Refused)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        await host.StopAsync();
        return code;
    }

    private static async Task<int> ExecuteAsync(IServiceProvider services, Guid revisionId, TextWriter stdout, TextWriter stderr)
    {
        var audit = services.GetRequiredService<SecurityAudit>();
        var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(MigrationRunner.MigratorConnectionStringName);
        if (string.IsNullOrEmpty(connectionString))
        {
            audit.ProjectionGenerationRefused("failed");
            await stderr.WriteLineAsync("failed");
            return Failed;
        }

        try
        {
            await using var dataSource = new NpgsqlDataSourceBuilder(connectionString) { Name = HandleOperatorCommands.ServiceName }.Build();
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('gozleme.projections:' || $1, 0))", connection, tx))
            {
                lockCommand.Parameters.Add(new NpgsqlParameter { Value = revisionId.ToString() });
                await lockCommand.ExecuteNonQueryAsync();
            }

            if (await ReadRevisionAsync(connection, tx, revisionId) is not { } revision)
            {
                audit.ProjectionGenerationRefused("unknown_target");
                await stderr.WriteLineAsync("unknown_target");
                return UnknownTarget;
            }

            if (await LatestAsync(connection, tx, revision) is not { } latest)
            {
                // Generation 1 is recorded by publication or the install step; without it there is nothing to follow (F14).
                ChildTelemetry.RecordGenerationOutcome(revision.Family, ChildOutcome.Refused);
                audit.ProjectionGenerationRefused(RegenerateRefusal.NotInstalled);
                await stderr.WriteLineAsync(RegenerateRefusal.NotInstalled);
                return Refused;
            }

            var regeneration = ProjectionRegenerator.Regenerate(revision, ManifestAdapters.All);
            if (regeneration.Projection is not { } projection)
            {
                await tx.RollbackAsync();
                ChildTelemetry.RecordGenerationOutcome(revision.Family, ChildOutcome.Refused);
                audit.ProjectionGenerationRefused(regeneration.Refusal!);
                await stderr.WriteLineAsync(regeneration.Refusal);
                return Refused;
            }

            var generation = new ProjectionGeneration
            {
                Id = Guid.CreateVersion7(),
                Generation = latest.Generation.Generation + 1,
                AdapterId = revision.AdapterId,
                AdapterVersion = revision.AdapterVersion,
                ProjectionVersion = revision.ProjectionVersion,
                Projection = projection,
                ProjectionDigest = RevisionVerifier.Digest(projection),
            };
            using (ChildTelemetry.StartGeneration(generation.Generation))
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO gozleme.projection_generations (id, organization_id, revision_id, generation, adapter_id, adapter_version, projection_version, projection, projection_digest, created_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, now())
                """, connection, tx))
            {
                foreach (var value in new object[] { generation.Id, revision.OrganizationId, revision.Id, generation.Generation, generation.AdapterId, generation.AdapterVersion,
                    generation.ProjectionVersion, projection, generation.ProjectionDigest })
                {
                    insert.Parameters.Add(new NpgsqlParameter { Value = value });
                }

                await insert.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            var compared = ChildEndpoints.Compare(latest.Generation, latest.Digest, generation, generation.ProjectionDigest);
            ChildTelemetry.RecordGenerationOutcome(revision.Family, compared == GenerationComparison.Identical ? ChildOutcome.Identical : ChildOutcome.Recorded);
            audit.ProjectionGenerationRecorded(revision.Id, generation.Id, generation.Generation);
            var equals = compared switch
            {
                GenerationComparison.Identical => "yes",
                GenerationComparison.Different => "no",
                _ => "not_comparable",
            };
            await stdout.WriteLineAsync($"{revision.Id} generation {generation.Generation} {generation.Id} digest equals generation {latest.Generation.Generation}: {equals}");
            return Ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            audit.ProjectionGenerationRefused("failed");
            await stderr.WriteLineAsync("failed"); // the fixed word only: no exception type or message
            return Failed;
        }
    }

    /// <summary>The revision as the operator reads it (the A2 select branch); null when unknown.</summary>
    private static async Task<ManifestRevision?> ReadRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id)
    {
        await using var select = new NpgsqlCommand("""
            SELECT id, organization_id, capability_id, family, envelope_summary::text, native_encoding, native_document, native_digest, projection,
                   projection_version, projection_digest, adapter_id, adapter_version
            FROM gozleme.manifest_revisions WHERE id = $1
            """, connection, tx);
        select.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await select.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ManifestRevision
        {
            Id = reader.GetGuid(0),
            OrganizationId = reader.GetGuid(1),
            CapabilityId = reader.GetGuid(2),
            Family = reader.GetString(3),
            EnvelopeSummary = reader.GetString(4),
            NativeEncoding = reader.GetString(5),
            NativeDocument = reader.GetFieldValue<byte[]>(6),
            NativeDigest = reader.GetString(7),
            Projection = reader.GetFieldValue<byte[]>(8),
            ProjectionVersion = reader.GetString(9),
            ProjectionDigest = reader.GetString(10),
            AdapterId = reader.GetString(11),
            AdapterVersion = reader.GetString(12),
        };
    }

    /// <summary>The latest generation and its digest (generation 1's digest is the revision's projection digest).</summary>
    private static async Task<(ProjectionGeneration Generation, string Digest)?> LatestAsync(NpgsqlConnection connection, NpgsqlTransaction tx, ManifestRevision revision)
    {
        await using var select = new NpgsqlCommand("""
            SELECT id, generation, adapter_id, adapter_version, projection_version, projection_digest
            FROM gozleme.projection_generations WHERE revision_id = $1 ORDER BY generation DESC LIMIT 1
            """, connection, tx);
        select.Parameters.Add(new NpgsqlParameter { Value = revision.Id });
        await using var reader = await select.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var generation = new ProjectionGeneration
        {
            Id = reader.GetGuid(0),
            Generation = reader.GetInt32(1),
            AdapterId = reader.GetString(2),
            AdapterVersion = reader.GetString(3),
            ProjectionVersion = reader.GetString(4),
        };
        return (generation, generation.Generation == 1 ? revision.ProjectionDigest : reader.GetString(5));
    }
}
