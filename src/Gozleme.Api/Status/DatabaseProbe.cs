using System.Diagnostics;
using Npgsql;

namespace Gozleme.Api.Status;

internal interface IDatabaseProbe
{
    Task<DependencyStatus> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Checks PostgreSQL reachability, applied schema version and optional pgvector availability
/// using the ordinary application role. Failures map to fixed, non-sensitive messages.
/// </summary>
internal sealed partial class DatabaseProbe(NpgsqlDataSource dataSource, ILogger<DatabaseProbe> logger) : IDatabaseProbe
{
    public const int TimeoutSeconds = 2;
    public const string Name = "database";
    public const string UnreachableMessage = "Database unreachable";
    public const string NotInitializedMessage = "Database schema not initialized";

    private const string ProbeSql = """
        SELECT
          (SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1),
          EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector'),
          EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector')
        """;

    public async Task<DependencyStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var command = dataSource.CreateCommand(ProbeSql);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            await reader.ReadAsync(timeout.Token);
            var schemaVersion = await reader.IsDBNullAsync(0, timeout.Token) ? null : reader.GetString(0);
            var vector = new VectorCapability(reader.GetBoolean(1), reader.GetBoolean(2));
            var latency = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);

            return schemaVersion is null
                ? Unavailable(NotInitializedMessage)
                : new DependencyStatus(Name, StatusValues.Healthy, true, latency, schemaVersion, null, new DependencyCapabilities(vector));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            LogProbeFailed(ex.GetType().Name);
            return Unavailable(NotInitializedMessage);
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // Exception messages can contain host names or server detail; log the type only.
            LogProbeFailed(ex.GetType().Name);
            return Unavailable(UnreachableMessage);
        }
    }

    private static DependencyStatus Unavailable(string message) =>
        new(Name, StatusValues.Unavailable, true, null, null, message, null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database probe failed ({ExceptionType})")]
    private partial void LogProbeFailed(string exceptionType);
}
