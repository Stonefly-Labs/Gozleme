namespace Gozleme.Api.Tenancy;

/// <summary>
/// Row-level security settings (<c>Gozleme:Rls</c>). The context key signs each transaction's account
/// context (research R1–R2); the database holds the same key where <c>gozleme_app</c> cannot read it.
/// Validation messages never include the value.
/// </summary>
internal sealed class TenancyOptions
{
    public const string Section = "Gozleme:Rls";
    public const int MinKeyLength = 32;

    public string? ContextKey { get; set; }

    public bool IsValid => ContextKey is { Length: >= MinKeyLength };
}
