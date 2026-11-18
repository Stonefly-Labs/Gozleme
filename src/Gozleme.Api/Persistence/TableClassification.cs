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
        "organizations", "memberships", "invitations", "publishers", "capabilities",
    };

    /// <summary>
    /// Global account-level infrastructure (GOZ-0001): keyed by account, no tenant content, no tenant RLS. GOZ-0005 adds
    /// the per-account publisher registration counter and GOZ-0006 the per-account manifest validation counter (T099), both
    /// used after authorization and gone with the account.
    /// </summary>
    public static readonly IReadOnlySet<string> GlobalIdentity = new HashSet<string>(StringComparer.Ordinal)
    {
        "users", "user_claims", "user_logins", "user_tokens", "auth_sessions", "data_protection_keys", "publisher_registration_attempts",
        "manifest_validation_attempts",
    };

    /// <summary>
    /// Installation reference data that only migrations write (GOZ-0003-IF-08 option B, GOZ-0005 research R1): no
    /// <c>organization_id</c>, no tenant content, and <c>gozleme_app</c> holds exactly the pinned privileges here,
    /// with no column grant and no dependent view, so <c>RegistryOriginReader</c> is its one read path.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> InstanceReference = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["registry_origin"] = Privileges("SELECT"),
    };

    /// <summary>
    /// Installation-owned namespace entries that request handlers write (GOZ-0005 maintainer Q10, research R1–R4):
    /// row-level security enabled but not forced, so the owner is the installation authority while
    /// <c>gozleme_app</c> is bound by at least one policy and holds exactly the pinned privileges (never
    /// <c>DELETE</c> or <c>TRUNCATE</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> InstanceClaims = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["handle_claims"] = Privileges("SELECT", "INSERT", "UPDATE"),
        ["alias_reservations"] = Privileges("INSERT"),
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

    private static HashSet<string> Privileges(params string[] privileges) => new(privileges, StringComparer.Ordinal);
}
