namespace Gozleme.Api.Organizations;

/// <summary>
/// A tenant (GOZ-0002). The id is permanent and opaque; the display name is editable and not unique.
/// <see cref="CreatedBy"/> is kept for audit only and grants nothing (FR-018).
/// </summary>
internal sealed class Organization
{
    public const int MaxDisplayNameLength = 100;

    public Guid Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
