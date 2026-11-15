using System.Text.Json.Serialization;

namespace Gozleme.Api.Identity;

/// <summary>Auth API v1 request/response shapes (contracts/auth-api.openapi.yaml).</summary>
internal sealed record CredentialsRequest(string? Email, string? Password);

internal sealed record SessionResponse(Guid UserId, string Email, bool EmailVerified, DateTimeOffset ExpiresAt);

internal sealed record AuthOptionsResponse(bool RegistrationEnabled, string[] ExternalProviders);

internal sealed record AuthError(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string RegistrationFailed = "registration_failed";
    public const string RegistrationClosed = "registration_closed";
    public const string ValidationFailed = "validation_failed";
    public const string Forbidden = "forbidden";
}
