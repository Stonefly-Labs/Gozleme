using Microsoft.AspNetCore.Identity;

namespace Gozleme.Api.Identity;

/// <summary>
/// A global local-account principal (GOZ-0001). Not tenant-owned; the id is opaque, permanent and
/// the only account identifier allowed in telemetry or audit events. Email is unverified.
/// </summary>
internal sealed class GozlemeUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }
}
