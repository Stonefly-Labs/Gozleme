using System.Text;
using Gozleme.Api.Identity;
using Gozleme.Api.Organizations;
using Gozleme.Api.Publishers;
using Gozleme.Api.Revisions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Gozleme.Api.Persistence;

/// <summary>
/// Owns Gözleme schema migrations. GOZ-0001 adds local identity (user-only Identity store),
/// server-side sessions and the Data Protection key ring; GOZ-0002 adds organizations, memberships
/// and invitations; GOZ-0003 forces row-level security on those three tenant tables; GOZ-0004 adds
/// built-in roles to memberships; GOZ-0005 adds publishers and capabilities (and, outside this model, the
/// origin and the installation-owned claim tables); GOZ-0007 adds the write-once revisions, attestations, signing
/// keys and retirements (and, outside this model, the publication budget). All tables live
/// in schema <c>gozleme</c>; the tenant context key lives in <c>gozleme_private</c>.
/// </summary>
internal sealed class GozlemeDbContext(DbContextOptions<GozlemeDbContext> options)
    : IdentityUserContext<GozlemeUser, Guid>(options), IDataProtectionKeyContext
{
    public const string Schema = "gozleme";
    public const string AppRole = "gozleme_app";

    /// <summary><c>ck_memberships_roles</c> (GOZ-0004 research R1).</summary>
    public const string RolesCheck =
        "roles <@ ARRAY['admin', 'approver', 'member', 'publisher', 'reviewer']::text[] AND 'member' = ANY (roles) AND array_position(roles, NULL) IS NULL";

    /// <summary>The namespace rule and reserved handles as a <c>CHECK</c> (GOZ-0005 research R7; pinned to <see cref="NamespaceRules"/> by tests).</summary>
    public const string HandleCheck =
        "handle ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$' AND length(handle) BETWEEN 2 AND 39 AND handle <> ALL (ARRAY['admin', 'api', 'capabilities', 'capability', "
        + "'gozleme', 'operator', 'publisher', 'publishers', 'registry', 'root', 'security', 'support', 'system', 'www'])";

    public const string CapabilityNameCheck = "name ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$' AND length(name) BETWEEN 2 AND 64";

    public const string DisplayNameCheck = "length(display_name) BETWEEN 1 AND 100";

    /// <summary>Shared provider configuration for the API, migrator and operator commands.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options
            // History stays in public."__EFMigrationsHistory" (GOZ-0000) although entities default to schema gozleme.
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "public"))
            // These events embed the server address in their message. Outages are reported with a fixed
            // message by DatabaseUnavailable; other failures still surface as unhandled exceptions.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.ConnectionError, CoreEventId.QueryIterationFailed, CoreEventId.SaveChangesFailed))
            .AddInterceptors(RefusedWriteSpanInterceptor.Instance);

    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<Publisher> Publishers => Set<Publisher>();

    public DbSet<Capability> Capabilities => Set<Capability>();

    public DbSet<ManifestRevision> ManifestRevisions => Set<ManifestRevision>();

    public DbSet<RevisionAttestation> RevisionAttestations => Set<RevisionAttestation>();

    public DbSet<PublisherSigningKey> PublisherSigningKeys => Set<PublisherSigningKey>();

    public DbSet<SigningKeyRetirement> SigningKeyRetirements => Set<SigningKeyRetirement>();

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

        // Tenant-owned (GOZ-0002). Every table carries its tenant key; GOZ-0003 forces RLS on all three
        // (migration TenantRls; TableClassification). No role, owner or admin column exists by design (FR-018).
        // A new tenant-owned table must follow "Adding a tenant-owned table" in docs/engineering/local-development.md.
        builder.Entity<Organization>(org =>
        {
            org.ToTable("organizations");
            org.Property(o => o.DisplayName).HasMaxLength(Organization.MaxDisplayNameLength);
            org.Property(o => o.CreatedAt).HasDefaultValueSql("now()");
            org.HasOne<GozlemeUser>().WithMany().HasForeignKey(o => o.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Membership>(membership =>
        {
            membership.ToTable("memberships", t =>
            {
                t.HasCheckConstraint("ck_memberships_joined_via", "joined_via IN ('creator', 'invitation')");
                // GOZ-0004 research R1: built-in roles only, always holding member; stored sorted by memberships_guard.
                t.HasCheckConstraint("ck_memberships_roles", RolesCheck);
            });
            membership.HasKey(m => new { m.OrganizationId, m.UserId });
            membership.Property(m => m.JoinedVia).HasMaxLength(16);
            membership.Property(m => m.Roles).HasColumnType("text[]").HasDefaultValueSql("'{member}'::text[]");
            membership.Property(m => m.JoinedAt).HasDefaultValueSql("now()");
            membership.HasIndex(m => new { m.UserId, m.SelectedAt }).IsDescending(false, true);
            membership.HasOne<Organization>().WithMany().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            membership.HasOne<GozlemeUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            membership.HasOne<Invitation>().WithMany().HasForeignKey(m => m.InvitationId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Invitation>(invitation =>
        {
            invitation.ToTable("invitations", t =>
            {
                t.HasCheckConstraint("ck_invitations_used_or_revoked", "NOT (used_at IS NOT NULL AND revoked_at IS NOT NULL)");
                t.HasCheckConstraint("ck_invitations_code_hash_length", "octet_length(code_hash) = 32");
            });
            invitation.HasIndex(i => i.CodeHash).IsUnique();
            invitation.HasIndex(i => new { i.OrganizationId, i.CreatedAt });
            invitation.HasOne<Organization>().WithMany().HasForeignKey(i => i.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            invitation.HasOne<GozlemeUser>().WithMany().HasForeignKey(i => i.CreatedBy).OnDelete(DeleteBehavior.SetNull);
            invitation.HasOne<GozlemeUser>().WithMany().HasForeignKey(i => i.UsedBy).OnDelete(DeleteBehavior.SetNull);
            invitation.HasOne<GozlemeUser>().WithMany().HasForeignKey(i => i.RevokedBy).OnDelete(DeleteBehavior.SetNull);
        });

        // Tenant-owned (GOZ-0005), RLS forced; ids, handles, names and organizations never change (guards in
        // migration PublisherIdentities). The claim tables are reached through narrow SQL only (data-model.md).
        builder.Entity<Publisher>(publisher =>
        {
            publisher.ToTable("publishers", t =>
            {
                t.HasCheckConstraint("ck_publishers_handle", HandleCheck);
                t.HasCheckConstraint("ck_publishers_display_name", DisplayNameCheck);
            });
            publisher.HasAlternateKey(p => new { p.Id, p.OrganizationId });
            publisher.HasIndex(p => p.OrganizationId);
            ReadOnlyAfterInsert(publisher.Property(p => p.Handle).Metadata, publisher.Property(p => p.OrganizationId).Metadata, publisher.Property(p => p.CreatedAt).Metadata);
            publisher.HasOne<Organization>().WithMany().HasForeignKey(p => p.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            publisher.HasOne<GozlemeUser>().WithMany().HasForeignKey(p => p.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Capability>(capability =>
        {
            capability.ToTable("capabilities", t =>
            {
                t.HasCheckConstraint("ck_capabilities_name", CapabilityNameCheck);
                t.HasCheckConstraint("ck_capabilities_display_name", DisplayNameCheck);
            });
            capability.HasAlternateKey(c => new { c.Id, c.OrganizationId });
            capability.HasIndex(c => new { c.PublisherId, c.Name }).IsUnique();
            capability.HasIndex(c => c.OrganizationId);
            ReadOnlyAfterInsert(capability.Property(c => c.Name).Metadata, capability.Property(c => c.OrganizationId).Metadata, capability.Property(c => c.PublisherId).Metadata, capability.Property(c => c.CreatedAt).Metadata);
            capability.HasOne<Organization>().WithMany().HasForeignKey(c => c.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            capability.HasOne<Publisher>().WithMany().HasForeignKey(c => new { c.PublisherId, c.OrganizationId })
                .HasPrincipalKey(p => new { p.Id, p.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            capability.HasOne<GozlemeUser>().WithMany().HasForeignKey(c => c.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        // Tenant-owned (GOZ-0007), RLS forced, write-once for every login (migration ManifestRevisions). Constraints,
        // guards, policies and the deferred attestation trigger live in the migration's SQL; this maps the columns.
        builder.Entity<PublisherSigningKey>(key =>
        {
            key.ToTable("publisher_signing_keys");
            key.HasAlternateKey(k => new { k.Id, k.OrganizationId });
            key.HasIndex(k => new { k.PublisherId, k.Generation }).IsUnique();
            key.HasIndex(k => k.KeyId).IsUnique();
            key.HasIndex(k => k.OrganizationId);
            key.HasOne<Organization>().WithMany().HasForeignKey(k => k.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            key.HasOne<Publisher>().WithMany().HasForeignKey(k => new { k.PublisherId, k.OrganizationId })
                .HasPrincipalKey(p => new { p.Id, p.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SigningKeyRetirement>(retirement =>
        {
            retirement.ToTable("signing_key_retirements");
            retirement.HasKey(r => r.KeyId);
            retirement.HasIndex(r => r.SuccessorKeyId).IsUnique();
            retirement.HasIndex(r => r.OrganizationId);
            retirement.HasOne<Organization>().WithMany().HasForeignKey(r => r.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            retirement.HasOne<PublisherSigningKey>().WithOne().HasForeignKey<SigningKeyRetirement>(r => new { r.KeyId, r.OrganizationId })
                .HasPrincipalKey<PublisherSigningKey>(k => new { k.Id, k.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            retirement.HasOne<PublisherSigningKey>().WithOne().HasForeignKey<SigningKeyRetirement>(r => new { r.SuccessorKeyId, r.OrganizationId })
                .HasPrincipalKey<PublisherSigningKey>(k => new { k.Id, k.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ManifestRevision>(revision =>
        {
            revision.ToTable("manifest_revisions");
            revision.HasAlternateKey(r => new { r.Id, r.OrganizationId });
            revision.HasIndex(r => new { r.CapabilityId, r.Sequence }).IsUnique();
            revision.HasIndex(r => new { r.CapabilityId, r.VersionKey }).IsUnique();
            revision.HasIndex(r => r.OrganizationId);
            revision.HasIndex(r => r.PublisherId);
            revision.Property(r => r.EnvelopeSummary).HasColumnType("jsonb");
            revision.HasOne<Organization>().WithMany().HasForeignKey(r => r.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            revision.HasOne<Publisher>().WithMany().HasForeignKey(r => new { r.PublisherId, r.OrganizationId })
                .HasPrincipalKey(p => new { p.Id, p.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            revision.HasOne<Capability>().WithMany().HasForeignKey(r => new { r.CapabilityId, r.OrganizationId })
                .HasPrincipalKey(c => new { c.Id, c.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RevisionAttestation>(attestation =>
        {
            attestation.ToTable("revision_attestations");
            attestation.HasKey(a => a.RevisionId);
            attestation.HasIndex(a => a.OrganizationId);
            attestation.HasIndex(a => a.SigningKeyId);
            attestation.HasOne<Organization>().WithMany().HasForeignKey(a => a.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            attestation.HasOne<ManifestRevision>().WithOne().HasForeignKey<RevisionAttestation>(a => new { a.RevisionId, a.OrganizationId })
                .HasPrincipalKey<ManifestRevision>(r => new { r.Id, r.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
            attestation.HasOne<PublisherSigningKey>().WithMany().HasForeignKey(a => new { a.SigningKeyId, a.OrganizationId })
                .HasPrincipalKey(k => new { k.Id, k.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        });

        ApplySnakeCaseNames(builder);
    }

    private static void ReadOnlyAfterInsert(params Microsoft.EntityFrameworkCore.Metadata.IMutableProperty[] properties)
    {
        foreach (var property in properties)
        {
            property.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Throw);
        }
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
