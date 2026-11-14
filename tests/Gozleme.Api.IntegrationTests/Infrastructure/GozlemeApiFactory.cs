using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>Hosts the real API against a test database as the ordinary app role, capturing logs.</summary>
public sealed class GozlemeApiFactory(string appConnectionString) : WebApplicationFactory<Program>
{
    public ConcurrentQueue<string> Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:Gozleme", appConnectionString);
        builder.UseSetting("Gozleme:BuildVersion", "0.0.0-it");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(new CapturingLoggerProvider(Logs)));
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
