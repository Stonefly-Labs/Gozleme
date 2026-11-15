using Gozleme.Api.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Gozleme.Api.Identity;

/// <summary>Ends every session of an account: rows deleted, security stamp rotated (defence in depth).</summary>
internal sealed class SessionRevoker(GozlemeDbContext db, UserManager<GozlemeUser> users)
{
    public async Task RevokeAllAsync(GozlemeUser user, CancellationToken cancellationToken)
    {
        await db.AuthSessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await users.UpdateSecurityStampAsync(user);
    }
}
