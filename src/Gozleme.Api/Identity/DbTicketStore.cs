using System.Security.Claims;
using System.Security.Cryptography;
using Gozleme.Api.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Identity;

/// <summary>
/// PostgreSQL-backed cookie session store (research R2). The cookie carries only a random 256-bit
/// key; the row is looked up by its SHA-256, so deleting a row revokes that session exactly and a
/// replayed cookie becomes anonymous. Each sign-in stores a new key (session fixation protection).
/// </summary>
internal sealed class DbTicketStore(
    IDbContextFactory<GozlemeDbContext> contexts,
    IDataProtectionProvider dataProtection,
    IOptions<AuthOptions> options,
    TimeProvider time) : ITicketStore
{
    public const string AbsoluteExpiryItem = "gozleme.absolute_expires";

    /// <summary>HttpContext item holding the session key resolved for the current request.</summary>
    private const string CurrentKeyItem = "gozleme.session_key";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Gozleme.Identity.SessionTicket.v1");

    public static byte[] Hash(string key) => SHA256.HashData(Convert.FromBase64String(key));

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Session ticket has no user id."));
        var now = time.GetUtcNow();
        var expiresAt = now.AddHours(options.Value.AbsoluteTimeoutHours);
        ticket.Properties.Items[AbsoluteExpiryItem] = expiresAt.ToString("O");
        Clamp(ticket, expiresAt);

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        // Opportunistic purge keeps the table bounded without a background job.
        await db.AuthSessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
        db.AuthSessions.Add(new AuthSession
        {
            KeyHash = Hash(key),
            UserId = userId,
            Ticket = Protect(ticket),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(cancellationToken);
        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public async Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var session = await db.AuthSessions.FindAsync([Hash(key)], cancellationToken);
        if (session is null)
        {
            return; // revoked concurrently; never resurrect it
        }

        Clamp(ticket, session.ExpiresAt);
        session.Ticket = Protect(ticket);
        session.LastSeenAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var ticket = await RetrieveAsync(key, cancellationToken);
        if (ticket is not null)
        {
            httpContext.Items[CurrentKeyItem] = key;
        }

        return ticket;
    }

    /// <summary>Ends the session this request arrived with, if any (called before issuing a new one).</summary>
    public Task RemoveCurrentAsync(HttpContext httpContext) =>
        httpContext.Items[CurrentKeyItem] is string key ? RemoveAsync(key, httpContext.RequestAborted) : Task.CompletedTask;

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        if (!IsWellFormed(key))
        {
            return null;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var hash = Hash(key);
        var session = await db.AuthSessions.AsNoTracking().FirstOrDefaultAsync(s => s.KeyHash == hash, cancellationToken);
        if (session is null)
        {
            return null;
        }

        if (session.ExpiresAt <= time.GetUtcNow())
        {
            await db.AuthSessions.Where(s => s.KeyHash == hash).ExecuteDeleteAsync(cancellationToken);
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(session.Ticket));
        }
        catch (CryptographicException)
        {
            return null; // key ring rotated away or tampered row: treat as anonymous
        }
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (!IsWellFormed(key))
        {
            return;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var hash = Hash(key);
        await db.AuthSessions.Where(s => s.KeyHash == hash).ExecuteDeleteAsync(cancellationToken);
    }

    private byte[] Protect(AuthenticationTicket ticket) => _protector.Protect(TicketSerializer.Default.Serialize(ticket));

    /// <summary>Sliding renewal never extends past the absolute expiry.</summary>
    private static void Clamp(AuthenticationTicket ticket, DateTimeOffset absolute)
    {
        if (ticket.Properties.ExpiresUtc is not { } expires || expires > absolute)
        {
            ticket.Properties.ExpiresUtc = absolute;
        }
    }

    private static bool IsWellFormed(string key)
    {
        Span<byte> buffer = stackalloc byte[32];
        return Convert.TryFromBase64String(key, buffer, out var written) && written == 32;
    }
}
