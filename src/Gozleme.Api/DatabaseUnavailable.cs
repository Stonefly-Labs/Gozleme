using System.Net.Sockets;
using Npgsql;

namespace Gozleme.Api;

/// <summary>
/// Fail-closed handling when PostgreSQL is unreachable (maintainer decision 2026-11-15): sessions and
/// credentials cannot be verified, so the request is answered <c>503 {"error":"unavailable"}</c> —
/// never treated as authenticated, never a 500 with internals. Readiness already reports the outage
/// anonymously, so this reveals nothing more.
/// </summary>
internal static partial class DatabaseUnavailable
{
    public const string Error = "unavailable";

    public static IApplicationBuilder UseDatabaseUnavailableResponses(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        try
        {
            await next(http);
        }
        catch (Exception ex) when (UnreachableCause(ex) is { } cause && !http.Response.HasStarted)
        {
            LogUnavailable(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseUnavailable)), cause.GetType().Name);
            http.Response.Clear();
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            http.Response.Headers.CacheControl = "no-store";
            await http.Response.WriteAsJsonAsync(new { error = Error }, http.RequestAborted);
        }
    });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database unreachable; request answered 503 ({ExceptionType})")]
    private static partial void LogUnavailable(ILogger logger, string exceptionType);

    /// <summary>
    /// The connection-level failure in <paramref name="ex"/>'s inner-exception chain, if any. Wrappers
    /// (EF Core, Identity) vary by call path, so the cause is what gets logged. SQL errors from a
    /// reachable server are real bugs and stay 500.
    /// </summary>
    internal static Exception? UnreachableCause(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is SocketException or TimeoutException || (ex is NpgsqlException && ex is not PostgresException))
            {
                return ex;
            }

            if (ex is PostgresException pg && pg.SqlState.StartsWith("57P", StringComparison.Ordinal))
            {
                return ex; // admin shutdown / crash shutdown / cannot connect now
            }
        }

        return null;
    }
}
