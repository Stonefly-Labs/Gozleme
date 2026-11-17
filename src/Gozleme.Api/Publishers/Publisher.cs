namespace Gozleme.Api.Publishers;

/// <summary>
/// A publisher identity owned by one organization (GOZ-0005 FR-004–FR-006). The id is a permanent UUIDv7,
/// rendered as <c>gz://&lt;origin&gt;/pub/&lt;id&gt;</c>; the handle it registered never changes; only the display
/// name does. Whether it is active is derived from its handle claim and never stored here (FR-037).
/// </summary>
internal sealed class Publisher
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public string Handle { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
