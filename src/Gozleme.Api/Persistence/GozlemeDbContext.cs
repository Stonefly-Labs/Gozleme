using System.Text;
using Gozleme.Api.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Gozleme.Api.Persistence;

/// <summary>
/// Owns Gözleme schema migrations. GOZ-0001 adds local identity (user-only Identity store),
/// server-side sessions and the Data Protection key ring; all tables live in schema <c>gozleme</c>.
/// </summary>
internal sealed class GozlemeDbContext(DbContextOptions<GozlemeDbContext> options)
    : IdentityUserContext<GozlemeUser, Guid>(options), IDataProtectionKeyContext
{
    public const string Schema = "gozleme";
    public const string AppRole = "gozleme_app";

    /// <summary>Shared provider configuration for the API, migrator and operator commands.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options
            // History stays in public."__EFMigrationsHistory" (GOZ-0000) although entities default to schema gozleme.
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "public"))
            // These events embed the server address in their message. Outages are reported with a fixed
            // message by DatabaseUnavailable; other failures still surface as unhandled exceptions.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.ConnectionError, CoreEventId.QueryIterationFailed, CoreEventId.SaveChangesFailed));

    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<GozlemeUser>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
            // One account per normalized email (FR-002); Identity's default email index is not unique.
            user.HasIndex(u => u.NormalizedEmail).IsUnique();
        });
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        builder.Entity<AuthSession>(session =>
        {
            session.ToTable("auth_sessions");
            session.HasKey(s => s.KeyHash);
            session.Property(s => s.KeyHash).HasMaxLength(32);
            session.HasIndex(s => s.UserId);
            session.HasIndex(s => s.ExpiresAt);
            session.HasOne<GozlemeUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");

        ApplySnakeCaseNames(builder);
    }

    /// <summary>PostgreSQL naming convention: snake_case columns, keys and indexes.</summary>
    private static void ApplySnakeCaseNames(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(SnakeCase(key.GetName()!));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(SnakeCase(foreignKey.GetConstraintName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(SnakeCase(index.GetDatabaseName()!));
            }
        }
    }

    internal static string SnakeCase(string name)
    {
        var result = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && name[i - 1] != '_' && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                {
                    result.Append('_');
                }

                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}
