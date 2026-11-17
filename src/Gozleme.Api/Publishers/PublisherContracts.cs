using System.Text.Json.Serialization;

namespace Gozleme.Api.Publishers;

/// <summary>Publishers API v1 request/response shapes (docs/specs/005-publisher-identities/contracts/publishers-api.openapi.yaml).</summary>
internal sealed record RegisterPublisherRequest(string? Handle, string? DisplayName);

internal sealed record CreateCapabilityRequest(string? Name, string? DisplayName);

/// <summary><c>Id</c> is the opaque UUID used in URLs; <c>CanonicalId</c> is its rendered form <c>gz://&lt;origin&gt;/pub/&lt;id&gt;</c>.</summary>
internal sealed record PublisherResponse(
    Guid Id, string CanonicalId, string Origin, Guid OrganizationId, string Handle, string DisplayName, string State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

internal sealed record PublisherListResponse(string Origin, IReadOnlyList<PublisherResponse> Publishers);

/// <summary><c>CanonicalId</c> is <c>gz://&lt;origin&gt;/cap/&lt;id&gt;</c>; <c>Alias</c> is <c>&lt;handle&gt;/&lt;name&gt;</c>; <c>State</c> is the owning publisher's.</summary>
internal sealed record CapabilityResponse(
    Guid Id, string CanonicalId, string Origin, Guid PublisherId, string Alias, string Name, string DisplayName, string State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

internal sealed record CapabilityListResponse(string Origin, IReadOnlyList<CapabilityResponse> Capabilities);

/// <summary>Derived from the installation-owned handle claim (FR-037), never stored on the publisher.</summary>
internal static class PublisherState
{
    public const string Active = "active";
    public const string Suspended = "suspended";

    public static string Of(bool active) => active ? Active : Suspended;
}

internal sealed record PublisherError(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public const string ValidationFailed = "validation_failed";
    public const string Forbidden = "forbidden";

    /// <summary>The one fixed answer for a taken, blocked or other-organization earmarked handle (SC-004).</summary>
    public const string HandleUnavailable = "handle_unavailable";

    /// <summary>The name is used under this publisher, or its alias is reserved (FR-014).</summary>
    public const string Conflict = "conflict";

    public const string Suspended = "suspended";
    public const string RateLimited = "rate_limited";

    /// <summary>The registry origin is not yet verified (the same body as a database outage).</summary>
    public const string Unavailable = "unavailable";
}
