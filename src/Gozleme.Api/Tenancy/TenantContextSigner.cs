using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Tenancy;

/// <summary>
/// Signs the per-transaction account context <c>v1.&lt;account&gt;.&lt;expires&gt;.&lt;mac&gt;</c>
/// (contracts/tenant-context.md §1). The token names an account only, never an organization, so a
/// membership removal takes effect on the next statement even while a token is still valid.
/// </summary>
internal sealed class TenantContextSigner(IOptions<TenancyOptions> options, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.Value.ContextKey ?? string.Empty);

    public string Sign(Guid accountId) => Sign(_key, accountId, time.GetUtcNow() + Lifetime);

    public static string Sign(byte[] key, Guid accountId, DateTimeOffset expires)
    {
        var payload = string.Create(CultureInfo.InvariantCulture, $"v1.{accountId:D}.{expires.ToUnixTimeSeconds()}");
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        return $"{payload}.{Convert.ToHexStringLower(mac)}";
    }
}
