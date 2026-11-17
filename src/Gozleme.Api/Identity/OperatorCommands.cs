using System.Diagnostics;
using Gozleme.Api.Organizations;
using Gozleme.Api.Persistence;
using Gozleme.Api.Telemetry;
using Gozleme.Api.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gozleme.Api.Identity;

/// <summary>
/// Operator-only one-shot modes (FR-026, FR-027, FR-035; GOZ-0002 demo organization), run like <c>migrate</c> and never reachable
/// over HTTP: <c>seed</c>, <c>reset-password</c> and <c>unlock</c>. They use the ordinary app role
/// (DML only). Output and audit events carry the opaque account id only, never the email or password.
/// Only <c>seed</c> touches tenant data, and only as a seeded member of each demo organization
/// (GOZ-0003); the other commands touch global identity tables and need no tenant context.
/// </summary>
internal static class OperatorCommands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int UnknownAccount = 2;
    public const int PolicyFailure = 3;
    public const int Usage = 64;

    public const string DemoPasswordKey = "GOZLEME_DEMO_PASSWORD";
    public const string ServiceName = "gozleme-operator";

    /// <summary>Deterministic synthetic demo accounts (reserved <c>.test</c> TLD).</summary>
    public static readonly string[] DemoAccounts = ["alice@demo.gozleme.test", "bob@demo.gozleme.test", "lockout@demo.gozleme.test", "carol@demo.gozleme.test", "dave@demo.gozleme.test"];

    /// <summary>The seeded demo organization (GOZ-0002): fixed id, members alice (creator) and carol.</summary>
    public static readonly Guid DemoOrganizationId = Guid.Parse("0193a000-0000-7000-8000-000000000002");
    public const string DemoOrganizationName = "Seeded Demo Org";

    /// <summary>The second demo tenant (GOZ-0003): fixed id, member dave (creator), disjoint from the first.</summary>
    public static readonly Guid PartnerOrganizationId = Guid.Parse("0193a000-0000-7000-8000-000000000003");
    public const string PartnerOrganizationName = "Seeded Partner Org";

    public const string UnrestorableMessage = "Demo organization cannot be restored as a member; run make reset.";

    public static bool Handles(string? command) => command is "seed" or "reset-password" or "unlock";

    public static async Task<int> RunAsync(string command, string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Any(a => a is "--password" || a.StartsWith("--password=", StringComparison.Ordinal)))
        {
            await stderr.WriteLineAsync("Passwords are never accepted as arguments; use --password-stdin or --password-file.");
            return Usage;
        }

        // Only `--Key=Value` arguments are configuration; command options use `--name value`.
        var builder = Host.CreateApplicationBuilder(args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=', StringComparison.Ordinal)).ToArray());
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.UseUtcTimestamp = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.AddGozlemeTelemetry(ServiceName);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => GozlemeApp.CreateDataSource(sp.GetRequiredService<IConfiguration>()));
        builder.AddGozlemeAccounts();
        if (command == "seed")
        {
            if (builder.Configuration.GetSection(TenancyOptions.Section).Get<TenancyOptions>() is not { IsValid: true })
            {
                await stderr.WriteLineAsync($"{TenancyOptions.Section}:ContextKey must be set to at least {TenancyOptions.MinKeyLength} characters.");
                return Failed;
            }

            builder.AddGozlemeTenancy();
        }

        using var host = builder.Build();
        await host.StartAsync(); // telemetry providers; disposing flushes exporters
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var config = services.GetRequiredService<IConfiguration>();
        using var activity = GozlemeTelemetry.ActivitySource.StartActivity($"gozleme.operator.{command.Replace('-', '_')}");
        try
        {
            var code = command switch
            {
                "seed" => await SeedAsync(services, config[DemoPasswordKey], stdout, stderr),
                "reset-password" => await ResetPasswordAsync(services, Option(args, "--email"), await ReadPasswordAsync(args, stdin), stdout, stderr),
                _ => await UnlockAsync(services, Option(args, "--email"), stdout, stderr),
            };
            activity?.SetTag("gozleme.operator.exit_code", code);
            await host.StopAsync();
            return code;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            await stderr.WriteLineAsync($"Operator command failed ({ex.GetType().Name}).");
            return Failed;
        }
    }

    private static async Task<int> SeedAsync(IServiceProvider services, string? password, TextWriter stdout, TextWriter stderr)
    {
        if (string.IsNullOrEmpty(password))
        {
            await stderr.WriteLineAsync($"{DemoPasswordKey} is not set.");
            return Usage;
        }

        var users = services.GetRequiredService<UserManager<GozlemeUser>>();
        var audit = services.GetRequiredService<SecurityAudit>();
        foreach (var email in DemoAccounts)
        {
            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
                user = new GozlemeUser { Id = Guid.CreateVersion7(now), Email = email, UserName = email, CreatedAt = now, LockoutEnabled = true };
                var created = await users.CreateAsync(user, password);
                if (!created.Succeeded)
                {
                    await stderr.WriteLineAsync($"Seed password rejected: {string.Join("; ", created.Errors.Select(e => e.Code))}");
                    return PolicyFailure;
                }
            }
            else
            {
                // Idempotent: every run restores the same known state.
                var code = await SetPasswordAsync(services, user, password, stderr);
                if (code != Ok)
                {
                    return code;
                }
            }

            audit.Seeded(user.Id);
            await stdout.WriteLineAsync($"Seeded demo account {user.Id}");
        }

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var email in DemoAccounts)
        {
            ids[email] = (await users.FindByEmailAsync(email))!.Id;
        }

        // GOZ-0005 research R11: carol holds publisher, so the demo shows capability writes; alice (admin) does not.
        var roles = new Dictionary<Guid, string[]> { [ids[DemoAccounts[3]]] = [OrganizationRole.Member, OrganizationRole.Publisher] };
        var seeded = await SeedOrganizationAsync(services, DemoOrganizationId, DemoOrganizationName, [ids[DemoAccounts[0]], ids[DemoAccounts[3]]], roles, stdout, stderr);
        return seeded != Ok ? seeded : await SeedOrganizationAsync(services, PartnerOrganizationId, PartnerOrganizationName, [ids[DemoAccounts[4]]], roles, stdout, stderr);
    }

    /// <summary>
    /// Idempotent: every run restores the organization's name, exactly <paramref name="members"/> (the first is
    /// the creator and holds admin; the others hold member, plus any <paramref name="roles"/> listed for them) and no invitations. It acts only through the
    /// signed context of a seeded member holding admin, so it has no right that admin lacks (GOZ-0003 research
    /// R10, GOZ-0004 research R11).
    /// </summary>
    private static async Task<int> SeedOrganizationAsync(
        IServiceProvider services, Guid organizationId, string name, Guid[] members, IReadOnlyDictionary<Guid, string[]> roles, TextWriter stdout, TextWriter stderr)
    {
        var db = services.GetRequiredService<GozlemeDbContext>();
        var signer = services.GetRequiredService<TenantContextSigner>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        foreach (var actor in members)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await TenantContext.EstablishAsync(db, signer.Sign(actor), CancellationToken.None);
            var own = await db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == actor);
            if (own is null || !own.Roles.Contains(OrganizationRole.Admin))
            {
                continue; // not an admin of it: an ordinary member cannot restore it
            }

            var organization = await db.Organizations.SingleAsync(o => o.Id == organizationId);
            organization.DisplayName = name;
            organization.UpdatedAt = now;
            await db.Invitations.Where(i => i.OrganizationId == organizationId).ExecuteDeleteAsync();
            await db.Memberships.Where(m => m.OrganizationId == organizationId && !members.Contains(m.UserId)).ExecuteDeleteAsync();
            var present = await db.Memberships.Where(m => m.OrganizationId == organizationId).ToListAsync();
            AddMembers(db, organizationId, members, [.. present.Select(m => m.UserId)], roles, now);

            // The creator regains admin before anyone is demoted, so the actor never removes the last admin.
            if (present.SingleOrDefault(m => m.UserId == members[0]) is { } creator)
            {
                creator.Roles = [OrganizationRole.Admin, OrganizationRole.Member];
            }

            await db.SaveChangesAsync();
            foreach (var membership in present.Where(m => m.UserId != members[0]))
            {
                membership.Roles = RolesOf(roles, membership.UserId);
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            await stdout.WriteLineAsync($"Seeded demo organization {organizationId}");
            return Ok;
        }

        // No seeded member administers it: create it as the first seeded member through the creator path,
        // then add the others as that admin. If it exists with no seeded admin, a member's rights cannot restore it.
        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await TenantContext.EstablishAsync(db, signer.Sign(members[0]), CancellationToken.None);
            db.Organizations.Add(new Organization { Id = organizationId, DisplayName = name, CreatedAt = now, CreatedBy = members[0], UpdatedAt = now });
            db.Memberships.Add(new Membership
            {
                OrganizationId = organizationId,
                UserId = members[0],
                JoinedAt = now,
                JoinedVia = Membership.ViaCreator,
                Roles = [OrganizationRole.Admin, OrganizationRole.Member],
            });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // It exists, but only non-seeded accounts belong to it: a member's rights cannot restore it.
                await stderr.WriteLineAsync(UnrestorableMessage);
                return Failed;
            }

            AddMembers(db, organizationId, members, [members[0]], roles, now);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await stdout.WriteLineAsync($"Seeded demo organization {organizationId}");
        return Ok;
    }

    private static void AddMembers(
        GozlemeDbContext db, Guid organizationId, Guid[] members, IReadOnlyCollection<Guid> present, IReadOnlyDictionary<Guid, string[]> roles, DateTimeOffset now)
    {
        for (var i = 0; i < members.Length; i++)
        {
            if (!present.Contains(members[i]))
            {
                db.Memberships.Add(new Membership
                {
                    OrganizationId = organizationId,
                    UserId = members[i],
                    JoinedAt = now.AddSeconds(i),
                    JoinedVia = i == 0 ? Membership.ViaCreator : Membership.ViaInvitation,
                    Roles = i == 0 ? [OrganizationRole.Admin, OrganizationRole.Member] : RolesOf(roles, members[i]),
                });
            }
        }
    }

    private static string[] RolesOf(IReadOnlyDictionary<Guid, string[]> roles, Guid member) =>
        roles.TryGetValue(member, out var held) ? held : [OrganizationRole.Member];

    private static async Task<int> ResetPasswordAsync(IServiceProvider services, string? email, string? password, TextWriter stdout, TextWriter stderr)
    {
        var audit = services.GetRequiredService<SecurityAudit>();
        if (email is null || password is null)
        {
            await stderr.WriteLineAsync("Usage: reset-password --email <email> (--password-stdin | --password-file <path>)");
            return Usage;
        }

        var user = await services.GetRequiredService<UserManager<GozlemeUser>>().FindByEmailAsync(email.Trim());
        if (user is null)
        {
            audit.OperatorReset("unknown_account", null);
            await stderr.WriteLineAsync("No such account.");
            return UnknownAccount;
        }

        var code = await SetPasswordAsync(services, user, password, stderr);
        audit.OperatorReset(code == Ok ? "success" : "policy_failure", user.Id);
        if (code == Ok)
        {
            await stdout.WriteLineAsync($"Password reset; all sessions ended for account {user.Id}");
        }

        return code;
    }

    private static async Task<int> UnlockAsync(IServiceProvider services, string? email, TextWriter stdout, TextWriter stderr)
    {
        var audit = services.GetRequiredService<SecurityAudit>();
        if (email is null)
        {
            await stderr.WriteLineAsync("Usage: unlock --email <email>");
            return Usage;
        }

        var users = services.GetRequiredService<UserManager<GozlemeUser>>();
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            audit.OperatorUnlock("unknown_account", null);
            await stderr.WriteLineAsync("No such account.");
            return UnknownAccount;
        }

        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        audit.OperatorUnlock("success", user.Id);
        await stdout.WriteLineAsync($"Unlocked account {user.Id}");
        return Ok;
    }

    /// <summary>Validates first (a failed policy never leaves the account without a password), then hashes, unlocks and revokes sessions.</summary>
    private static async Task<int> SetPasswordAsync(IServiceProvider services, GozlemeUser user, string password, TextWriter stderr)
    {
        var users = services.GetRequiredService<UserManager<GozlemeUser>>();
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, password);
            if (!result.Succeeded)
            {
                await stderr.WriteLineAsync($"Password rejected: {string.Join(" ", result.Errors.Select(e => e.Description))}");
                return PolicyFailure;
            }
        }

        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        await users.UpdateAsync(user);
        await services.GetRequiredService<SessionRevoker>().RevokeAllAsync(user, CancellationToken.None);
        return Ok;
    }

    private static async Task<string?> ReadPasswordAsync(string[] args, TextReader stdin)
    {
        if (args.Contains("--password-stdin"))
        {
            return (await stdin.ReadToEndAsync()).TrimEnd('\r', '\n');
        }

        return Option(args, "--password-file") is { } path ? (await File.ReadAllTextAsync(path)).TrimEnd('\r', '\n') : null;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
