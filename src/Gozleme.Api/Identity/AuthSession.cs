namespace Gozleme.Api.Identity;

/// <summary>
/// Server-side session row behind the <c>gozleme_session</c> cookie. Only the SHA-256 of the session
/// key is stored, so database read access does not yield usable cookies.
/// </summary>
internal sealed class AuthSession
{
    public required byte[] KeyHash { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>Serialized authentication ticket, encrypted with Data Protection.</summary>
    public required byte[] Ticket { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Absolute expiry, fixed at sign-in.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}
