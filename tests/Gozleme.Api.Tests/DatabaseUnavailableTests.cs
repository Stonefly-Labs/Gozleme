using System.Net.Sockets;
using Npgsql;

namespace Gozleme.Api.Tests;

/// <summary>FR-021a: only connection-level failures fail closed with 503, and the logged type is the cause, not a wrapper.</summary>
public sealed class DatabaseUnavailableTests
{
    [Fact]
    public void Wrapped_connection_failure_reports_the_inner_cause()
    {
        var cause = new NpgsqlException("connect failed", new SocketException());
        var wrapped = new InvalidOperationException("transient failure", cause);
        Assert.Same(cause, DatabaseUnavailable.UnreachableCause(wrapped));
    }

    [Fact]
    public void Shutdown_sql_state_is_unreachable()
    {
        var pg = new PostgresException("terminating connection", "FATAL", "FATAL", "57P01");
        Assert.Same(pg, DatabaseUnavailable.UnreachableCause(new InvalidOperationException("x", pg)));
    }

    [Fact]
    public void Sql_errors_and_unrelated_exceptions_are_not_unreachable()
    {
        Assert.Null(DatabaseUnavailable.UnreachableCause(new PostgresException("syntax", "ERROR", "ERROR", "42601")));
        Assert.Null(DatabaseUnavailable.UnreachableCause(new InvalidOperationException("bug")));
        Assert.Null(DatabaseUnavailable.UnreachableCause(null));
    }
}
