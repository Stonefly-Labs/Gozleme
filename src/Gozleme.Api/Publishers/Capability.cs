namespace Gozleme.Api.Publishers;

/// <summary>
/// A capability identity under one publisher (GOZ-0005 FR-011–FR-015). The id is its permanent canonical id,
/// rendered as <c>gz://&lt;origin&gt;/cap/&lt;id&gt;</c>; the name is fixed, and <c>&lt;handle&gt;/&lt;name&gt;</c> is
/// reserved forever as its alias in the same statement that creates it. Only the display name changes.
/// </summary>
internal sealed class Capability
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid PublisherId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
