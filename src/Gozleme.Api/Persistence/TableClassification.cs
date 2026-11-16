namespace Gozleme.Api.Persistence;

/// <summary>
/// Every table in schema <c>gozleme</c> is classified here (GOZ-0003 FR-017, research R11), and
/// <see cref="RlsCatalog"/> fails on any table that is not. A new tenant-owned table carries
/// <c>organization_id</c> with a foreign key to <c>organizations</c>, and its creating migration enables
/// and forces RLS with policies (contracts/tenant-context.md §5) before it is added to
/// <see cref="TenantOwned"/>.
/// </summary>
internal static class TableClassification
{
    /// <summary>Protected by application authorization and forced row-level security.</summary>
    public static readonly IReadOnlySet<string> TenantOwned = new HashSet<string>(StringComparer.Ordinal)
    {
        "organizations", "memberships", "invitations",
    };

    /// <summary>Global account-level infrastructure (GOZ-0001): needed before any tenant is known, no tenant RLS.</summary>
    public static readonly IReadOnlySet<string> GlobalIdentity = new HashSet<string>(StringComparer.Ordinal)
    {
        "users", "user_claims", "user_logins", "user_tokens", "auth_sessions", "data_protection_keys",
    };

    /// <summary>
    /// Shared or public records. Reserved: GOZ-0012 must first define their separately verified access
    /// path (CR-02), so any entry here is a violation until then.
    /// </summary>
    public static readonly IReadOnlySet<string> SharedPublic = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The owner-defined <c>SECURITY DEFINER</c> helpers. Inside them <c>gozleme.rls_internal()</c> lets the
    /// policies pass (research R4), so this set is pinned: any other definer function is a violation.
    /// </summary>
    public static readonly IReadOnlySet<string> DefinerHelpers = new HashSet<string>(StringComparer.Ordinal)
    {
        "establish_context", "current_account_id", "account_organizations", "is_new_organization_of_current_account", "lock_invited_organization",
    };
}
