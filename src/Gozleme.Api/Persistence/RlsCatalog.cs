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
                else if (!TableClassification.GlobalIdentity.Contains(name))
                {
                    violations.Add($"gozleme.{name} is not classified (TableClassification)");
                }
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
}
