using System.ComponentModel.DataAnnotations;

namespace Gozleme.Api.Publishers;

/// <summary>Operator-tunable publisher settings (<c>Gozleme:Publishers</c>), validated within safe bounds at start-up.</summary>
internal sealed class PublisherOptions
{
    public const string Section = "Gozleme:Publishers";

    /// <summary>Publisher registrations per account per hour (FR-008, research R8).</summary>
    [Range(1, 100)]
    public int RegisterPerHour { get; set; } = 10;
}
