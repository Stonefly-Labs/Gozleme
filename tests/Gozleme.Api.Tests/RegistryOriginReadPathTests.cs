namespace Gozleme.Api.Tests;

/// <summary>
/// GOZ-0005 FR-002 (research R1, R6): <c>RegistryOriginReader</c> is the only code outside migrations and the
/// migration runner that names <c>registry_origin</c>, and it reads it with one parameterless <c>SELECT</c>.
/// With the catalog rule (exactly <c>SELECT</c>, no column grant, no dependent view) this keeps one read path.
/// </summary>
public sealed class RegistryOriginReadPathTests
{
    private const string Table = "registry_origin";

    [Fact]
    public void Only_the_reader_the_migrator_and_migrations_name_the_origin_table()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var allowed = new[]
        {
            Path.Combine("Gozleme.Api", "Publishers", "RegistryOrigin.cs"),
            Path.Combine("Gozleme.Api", "Persistence", "MigrationRunner.cs"),
            Path.Combine("Gozleme.Api", "Persistence", "TableClassification.cs"), // the classification, not a read
        };
        var naming = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains(Path.Combine("Persistence", "Migrations"), StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(Table, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(src, f))
            .Order(StringComparer.Ordinal);
        Assert.Equal(allowed.Order(StringComparer.Ordinal), naming);
    }

    [Fact]
    public void The_reader_issues_exactly_one_parameterless_select()
    {
        var reader = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gozleme.Api", "Publishers", "RegistryOrigin.cs"));
        var uses = reader.Split('\n').Where(l => l.Contains($"gozleme.{Table}", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)).ToList();
        var use = Assert.Single(uses);
        Assert.Contains($"new NpgsqlCommand(\"SELECT origin FROM gozleme.{Table}\", connection)", use, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gozleme.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
