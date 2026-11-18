namespace Gozleme.Api.Manifests.Parsing;

/// <summary>
/// An RFC 6901 pointer held as a parent link plus one unescaped segment, so a tree of N nodes costs O(N) however deep
/// and long its member names are; the string form is built only when a problem is written (T074, security review F2).
/// Equality and order are by segments.
/// </summary>
internal sealed class JsonPointer : IEquatable<JsonPointer>
{
    public static readonly JsonPointer Root = new(null, string.Empty, 0);

    private readonly int _hash;

    private JsonPointer(JsonPointer? parent, string segment, int depth)
    {
        Parent = parent;
        Segment = segment;
        Depth = depth;
        _hash = parent is null ? 0 : HashCode.Combine(parent._hash, StringComparer.Ordinal.GetHashCode(segment));
    }

    public JsonPointer? Parent { get; }

    /// <summary>The unescaped member name or array index of this step (empty for the root).</summary>
    public string Segment { get; }

    public int Depth { get; }

    public JsonPointer Child(string name) => new(this, name, Depth + 1);

    public JsonPointer Child(int index) => new(this, index.ToString(System.Globalization.CultureInfo.InvariantCulture), Depth + 1);

    /// <summary>Parses an RFC 6901 pointer string (schema results arrive as text).</summary>
    public static JsonPointer Parse(string pointer)
    {
        var at = Root;
        foreach (var segment in pointer.Split('/').Skip(1))
        {
            at = at.Child(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        return at;
    }

    /// <summary>The segments from the root down.</summary>
    public string[] Segments()
    {
        var segments = new string[Depth];
        for (var at = this; at.Parent is not null; at = at.Parent)
        {
            segments[at.Depth - 1] = at.Segment;
        }

        return segments;
    }

    public override string ToString() => string.Concat(Segments().Select(s => "/" + Escape(s)));

    /// <summary>RFC 6901 escaping of one segment (<c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>); no allocation when there is nothing to escape.</summary>
    public static string Escape(string segment) =>
        segment.AsSpan().IndexOfAny('~', '/') < 0 ? segment : segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    public bool Equals(JsonPointer? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || other._hash != _hash || other.Depth != Depth)
        {
            return false;
        }

        for (JsonPointer? a = this, b = other; a is not null && b is not null; a = a.Parent, b = b.Parent)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (!string.Equals(a.Segment, b.Segment, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as JsonPointer);

    public override int GetHashCode() => _hash;
}
