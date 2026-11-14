using Gozleme.Api.Telemetry;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gozleme.Api.Status;

/// <summary>Builds the versioned service status document from dependency probes.</summary>
internal sealed class StatusService(IDatabaseProbe databaseProbe, TimeProvider timeProvider, IConfiguration configuration)
{
    public const string ContractVersion = "1";
    public const string ServiceName = GozlemeApp.ServiceName;

    private readonly string _version = GozlemeTelemetry.ServiceVersion(configuration);

    public async Task<ServiceStatus> GetAsync(CancellationToken cancellationToken)
    {
        var database = await databaseProbe.ProbeAsync(cancellationToken);
        DependencyStatus[] dependencies = [database];
        return new ServiceStatus(ContractVersion, ServiceName, _version, Aggregate(dependencies), timeProvider.GetUtcNow(), dependencies);
    }

    /// <summary>Overall status is degraded when any required dependency is not healthy.</summary>
    public static string Aggregate(IEnumerable<DependencyStatus> dependencies) =>
        dependencies.Any(d => d.Required && d.Status != StatusValues.Healthy) ? StatusValues.Degraded : StatusValues.Healthy;
}

/// <summary>Readiness check backed by the same probe as the status document.</summary>
internal sealed class DatabaseHealthCheck(IDatabaseProbe databaseProbe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = await databaseProbe.ProbeAsync(cancellationToken);
        return result.Status == StatusValues.Healthy ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(result.Message);
    }
}
