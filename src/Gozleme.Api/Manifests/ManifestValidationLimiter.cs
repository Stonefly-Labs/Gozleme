using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Manifests;

/// <summary><c>Gozleme:Manifests</c> options (data-model.md "Configuration").</summary>
internal sealed class ManifestOptions
{
    public const string Section = "Gozleme:Manifests";

    [Range(1, 10_000)]
    public int ValidatePerMinute { get; set; } = 30;
}

/// <summary>
/// Validations per account per minute, in memory, per API instance (research R11, disclosed agent choice). Acquired
/// by the handler after membership, permission, target and suspension, so a non-member, a forbidden caller or a
/// suspended target never spends or sees <c>429</c>. A refusal is recorded on the manifest span and counter only,
/// never through the shared sign-in <c>OnRejected</c> path (GOZ-0002-IF-08).
/// </summary>
internal sealed class ManifestValidationLimiter(IOptions<ManifestOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(account =>
        RateLimitPartition.GetFixedWindowLimiter(account, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.Value.ValidatePerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    /// <summary><c>null</c> when a permit was taken; otherwise the seconds until the account's window ends (at least 1).</summary>
    public int? TryAcquire(Guid accountId)
    {
        using var lease = _limiter.AttemptAcquire(accountId);
        if (lease.IsAcquired)
        {
            return null;
        }

        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 60;
    }

    public void Dispose() => _limiter.Dispose();
}
