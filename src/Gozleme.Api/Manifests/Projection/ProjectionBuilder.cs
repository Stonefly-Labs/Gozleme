namespace Gozleme.Api.Manifests.Projection;

/// <summary>
/// Builds projection v1 (contracts/projection-v1.md) from values an adapter read out of the tree. When the native
/// document has no title, <c>title</c> falls back to <c>nativeName</c> and <c>/title</c> is listed in <c>derived</c>.
/// <c>derived</c> is in a fixed order: <c>/title</c>, then attributes, then entries by index.
/// </summary>
internal sealed class ProjectionBuilder(string adapter, string family, string kind, ManifestEnvelope envelope)
{
    private readonly List<ProjectionEntry> _entries = [];
    private readonly List<string> _derivedAttributes = [];
    private readonly List<int> _derivedEntries = [];

    public ProjectionAttributes Attributes { get; set; } = new();

    public void DeriveAttribute(string name) => _derivedAttributes.Add("/attributes/" + name);

    /// <summary>Adds an entry; <paramref name="nameDerived"/> marks a defaulted native name.</summary>
    public void Entry(string kind, string nativeName, string? title, bool nameDerived = false)
    {
        if (nameDerived)
        {
            _derivedEntries.Add(_entries.Count);
        }

        _entries.Add(new ProjectionEntry(kind, nativeName, title));
    }

    public ManifestProjection Build(string nativeName, string? title, string? description)
    {
        var derived = new List<string>();
        if (title is null)
        {
            derived.Add("/title");
        }

        derived.AddRange(_derivedAttributes);
        derived.AddRange(_derivedEntries.Select(i => $"/entries/{i}/nativeName"));
        return new ManifestProjection(ManifestProjection.Version, adapter, family, kind, nativeName, title ?? nativeName, description,
            envelope.Version!, Attributes, [.. _entries], derived);
    }
}
