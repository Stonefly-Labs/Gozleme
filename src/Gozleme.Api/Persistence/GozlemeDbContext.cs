using Microsoft.EntityFrameworkCore;

namespace Gozleme.Api.Persistence;

/// <summary>
/// Owns Gözleme schema migrations. GOZ-0000 defines no entities; later specs add their own.
/// </summary>
internal sealed class GozlemeDbContext(DbContextOptions<GozlemeDbContext> options) : DbContext(options)
{
    public const string Schema = "gozleme";
    public const string AppRole = "gozleme_app";
}
