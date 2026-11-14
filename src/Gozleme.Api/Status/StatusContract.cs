namespace Gozleme.Api.Status;

/// <summary>Status values used in contract v1. Serialized verbatim.</summary>
internal static class StatusValues
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
}

/// <summary>GET /api/v1/status response (contract version 1). Never carries secrets or exception text.</summary>
internal sealed record ServiceStatus(
    string ContractVersion,
    string Service,
    string Version,
    string Status,
    DateTimeOffset CheckedAt,
    IReadOnlyList<DependencyStatus> Dependencies);

internal sealed record DependencyStatus(
    string Name,
    string Status,
    bool Required,
    double? LatencyMs,
    string? SchemaVersion,
    string? Message,
    DependencyCapabilities? Capabilities);

internal sealed record DependencyCapabilities(VectorCapability Vector);

internal sealed record VectorCapability(bool Available, bool Enabled);
