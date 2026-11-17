using Npgsql;

namespace Gozleme.Api.Persistence;

/// <summary>
/// Catalog check for the tenant boundary (GOZ-0003 FR-018, research R11). It reads the system catalogs
/// only, so it is safe on any role's connection, and the GOZ-0039 release gate can reuse it.
/// Returns human-readable violations; empty means the boundary holds. The <c>sharedPublic</c> argument
/// overrides <see cref="TableClassification.SharedPublic"/> so tests can prove the reservation holds.
/// </summary>
internal static class RlsCatalog
{
    private const string FixedSearchPath = "search_path=pg_catalog, pg_temp";

    public static async Task<IReadOnlyList<string>> ViolationsAsync(NpgsqlConnection connection, IReadOnlySet<string>? sharedPublic = null, CancellationToken ct = default)
    {
        sharedPublic ??= TableClassification.SharedPublic;
        var violations = new List<string>();

        await using (var tables = new NpgsqlCommand("""
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'gozleme' AND c.relkind IN ('r', 'p')
            ORDER BY c.relname
            """, connection))
        await using (var reader = await tables.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var (name, rls, force, policies) = (reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt64(3));
                if (sharedPublic.Contains(name))
                {
                    violations.Add($"gozleme.{name} is classified shared/public, which is reserved until GOZ-0012 defines its access path");
                }
                else if (TableClassification.TenantOwned.Contains(name))
                {
                    if (!rls)
                    {
                        violations.Add($"gozleme.{name} is tenant-owned but row-level security is not enabled");
                    }

                    if (!force)
                    {
                        violations.Add($"gozleme.{name} is tenant-owned but row-level security is not forced for the owner");
                    }

                    if (policies == 0)
                    {
                        violations.Add($"gozleme.{name} is tenant-owned but has no policy");
                    }
                }
                else if (TableClassification.InstanceClaims.ContainsKey(name))
                {
                    if (!rls)
                    {
                        violations.Add($"gozleme.{name} holds instance claims but row-level security is not enabled");
                    }

                    if (force)
                    {
                        violations.Add($"gozleme.{name} holds instance claims but row-level security is forced, which binds the operator");
                    }

                    if (policies == 0)
                    {
                        violations.Add($"gozleme.{name} holds instance claims but has no policy");
                    }
                }
                else if (!TableClassification.GlobalIdentity.Contains(name) && !TableClassification.InstanceReference.ContainsKey(name))
                {
                    violations.Add($"gozleme.{name} is not classified (TableClassification)");
                }
            }
        }

        await InstanceViolationsAsync(connection, violations, ct);

        // Views, materialized views and foreign tables cannot be classified, so none may exist in gozleme
        // (GOZ-0003-IF-12). A materialized view stores rows without row-level security, and a foreign table
        // reaches data outside these policies.
        await using (var relations = new NpgsqlCommand("""
            SELECT c.relname, CASE c.relkind WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized view' ELSE 'foreign table' END
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'gozleme' AND c.relkind IN ('v', 'm', 'f')
            ORDER BY c.relname
            """, connection))
        await using (var reader = await relations.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                violations.Add($"gozleme.{reader.GetString(0)} is a {reader.GetString(1)}, which cannot be classified and could expose tenant rows");
            }
        }

        // An owner-defined definer function anywhere bypasses every tenant policy through rls_internal().
        await using (var elsewhere = new NpgsqlCommand("""
            SELECT n.nspname, p.proname
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prosecdef AND p.proowner = 'gozleme_migrator'::regrole
              AND n.nspname NOT IN ('gozleme', 'gozleme_private', 'pg_catalog', 'information_schema')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
            ORDER BY n.nspname, p.proname
            """, connection))
        await using (var reader = await elsewhere.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                violations.Add($"{reader.GetString(0)}.{reader.GetString(1)} is SECURITY DEFINER but not a pinned helper; it would bypass row-level security");
            }
        }

        await using (var functions = new NpgsqlCommand("""
            SELECT n.nspname, p.proname, p.prosecdef, coalesce(array_to_string(p.proconfig, ';'), ''),
                   EXISTS (SELECT 1 FROM aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a WHERE a.grantee = 0 AND a.privilege_type = 'EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname IN ('gozleme', 'gozleme_private')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
            ORDER BY n.nspname, p.proname
            """, connection))
        await using (var reader = await functions.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var (schema, name, definer, config, publicExecute) = (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3), reader.GetBoolean(4));
                if (definer && (schema != "gozleme" || !TableClassification.DefinerHelpers.Contains(name)))
                {
                    violations.Add($"{schema}.{name} is SECURITY DEFINER but not a pinned helper; it would bypass row-level security");
                }

                if (definer && !config.Contains(FixedSearchPath, StringComparison.Ordinal))
                {
                    violations.Add($"{schema}.{name} is SECURITY DEFINER without a fixed search_path");
                }

                if (publicExecute)
                {
                    violations.Add($"{schema}.{name} is executable by PUBLIC");
                }
            }
        }

        await using (var privileges = new NpgsqlCommand("""
            SELECT has_schema_privilege('gozleme_app', 'gozleme_private', 'USAGE') OR has_schema_privilege('gozleme_app', 'gozleme_private', 'CREATE')
            """, connection))
        {
            if (await privileges.ExecuteScalarAsync(ct) is true)
            {
                violations.Add("gozleme_app has a privilege on schema gozleme_private, which holds the context key");
            }
        }

        return violations;
    }

    /// <summary>
    /// The pinned privileges of the instance classes (GOZ-0005 research R1). An <c>InstanceReference</c> table also
    /// carries no <c>organization_id</c>, no column-level grant to <c>gozleme_app</c> and no dependent view, so it
    /// keeps exactly one read path (FR-002).
    /// </summary>
    private static async Task InstanceViolationsAsync(NpgsqlConnection connection, List<string> violations, CancellationToken ct)
    {
        var pinned = TableClassification.InstanceReference.Select(t => (t.Key, t.Value, Reference: true))
            .Concat(TableClassification.InstanceClaims.Select(t => (t.Key, t.Value, Reference: false)));
        foreach (var (name, expected, reference) in pinned)
        {
            await using var command = new NpgsqlCommand("""
                SELECT coalesce((SELECT array_agg(a.privilege_type::text ORDER BY a.privilege_type)
                                 FROM aclexplode(coalesce(c.relacl, acldefault('r', c.relowner))) a
                                 WHERE a.grantee = 'gozleme_app'::regrole), '{}'),
                       EXISTS (SELECT 1 FROM pg_attribute t WHERE t.attrelid = c.oid AND t.attname = 'organization_id' AND NOT t.attisdropped),
                       EXISTS (SELECT 1 FROM pg_attribute t, aclexplode(t.attacl) a
                               WHERE t.attrelid = c.oid AND t.attacl IS NOT NULL AND a.grantee = 'gozleme_app'::regrole),
                       EXISTS (SELECT 1 FROM pg_depend d JOIN pg_rewrite r ON d.classid = 'pg_rewrite'::regclass AND d.objid = r.oid
                               WHERE d.refclassid = 'pg_class'::regclass AND d.refobjid = c.oid AND r.ev_class <> c.oid)
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'gozleme' AND c.relname = $1 AND c.relkind IN ('r', 'p')
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = name });
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                continue; // not created yet
            }

            var held = reader.GetFieldValue<string[]>(0).ToHashSet(StringComparer.Ordinal);
            var (hasOrganization, columnGrant, dependentView) = (reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
            var label = reference ? "is instance reference data" : "holds instance claims";
            if (!held.SetEquals(expected))
            {
                violations.Add($"gozleme.{name} {label} but gozleme_app holds {Describe(held)} instead of exactly {Describe(expected)}");
            }

            if (held.Contains("DELETE") || held.Contains("TRUNCATE"))
            {
                violations.Add($"gozleme.{name} {label} but gozleme_app may delete or truncate it");
            }

            if (reference && hasOrganization)
            {
                violations.Add($"gozleme.{name} is instance reference data but has an organization_id column");
            }

            if (reference && columnGrant)
            {
                violations.Add($"gozleme.{name} is instance reference data but gozleme_app has a column-level privilege on it");
            }

            if (reference && dependentView)
            {
                violations.Add($"gozleme.{name} is instance reference data but a view depends on it, a second read path");
            }
        }
    }

    private static string Describe(IEnumerable<string> privileges) =>
        privileges.Any() ? string.Join(", ", privileges.Order(StringComparer.Ordinal)) : "nothing";
}
