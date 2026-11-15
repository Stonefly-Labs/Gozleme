using System.Diagnostics;
using Gozleme.Api.Organizations;
using Gozleme.Api.Persistence;
using Gozleme.Api.Telemetry;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Gozleme.Api.Identity;

/// <summary>
/// Operator-only one-shot modes (FR-026, FR-027, FR-035; GOZ-0002 demo organization), run like <c>migrate</c> and never reachable
/// over HTTP: <c>seed</c>, <c>reset-password</c> and <c>unlock</c>. They use the ordinary app role
/// (DML only). Output and audit events carry the opaque account id only, never the email or password.
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
    public static readonly string[] DemoAccounts = ["alice@demo.gozleme.test", "bob@demo.gozleme.test", "lockout@demo.gozleme.test", "carol@demo.gozleme.test"];

    /// <summary>The seeded demo organization (GOZ-0002): fixed id, members alice (creator) and carol.</summary>
    public static readonly Guid DemoOrganizationId = Guid.Parse("0193a000-0000-7000-8000-000000000002");
    public const string DemoOrganizationName = "Seeded Demo Org";

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

        await SeedOrganizationAsync(services, stdout);
        return Ok;
    }

    /// <summary>Idempotent: every run restores the demo organization's name, exactly alice and carol as members, and no invitations.</summary>
    private static async Task SeedOrganizationAsync(IServiceProvider services, TextWriter stdout)
    {
        var db = services.GetRequiredService<GozlemeDbContext>();
        var users = services.GetRequiredService<UserManager<GozlemeUser>>();
        var alice = (await users.FindByEmailAsync(DemoAccounts[0]))!.Id;
        var carol = (await users.FindByEmailAsync(DemoAccounts[3]))!.Id;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync();
        var organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == DemoOrganizationId);
        if (organization is null)
        {
            db.Organizations.Add(new Organization { Id = DemoOrganizationId, DisplayName = DemoOrganizationName, CreatedAt = now, CreatedBy = alice, UpdatedAt = now });
        }
        else
        {
            organization.DisplayName = DemoOrganizationName;
            organization.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
        await db.Memberships.Where(m => m.OrganizationId == DemoOrganizationId).ExecuteDeleteAsync();
        await db.Invitations.Where(i => i.OrganizationId == DemoOrganizationId).ExecuteDeleteAsync();
        db.Memberships.Add(new Membership { OrganizationId = DemoOrganizationId, UserId = alice, JoinedAt = now, JoinedVia = Membership.ViaCreator });
        db.Memberships.Add(new Membership { OrganizationId = DemoOrganizationId, UserId = carol, JoinedAt = now.AddSeconds(1), JoinedVia = Membership.ViaInvitation });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await stdout.WriteLineAsync($"Seeded demo organization {DemoOrganizationId}");
    }

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
