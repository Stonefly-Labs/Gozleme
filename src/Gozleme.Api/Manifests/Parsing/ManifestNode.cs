using System.Text;
using System.Text.Json;

namespace Gozleme.Api.Manifests.Parsing;

internal enum NodeKind
{
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
}

/// <summary>
/// One value of the positioned tree every format is parsed into (research R3, data-model.md). Objects keep their
/// members in source order; a number keeps its text in JSON form. Every node carries its RFC 6901 pointer (as a
/// <see cref="JsonPointer"/> link, built into text only when reported) and its
/// 1-based start line and column in its source text, so JSON, YAML and front matter give identical locations.
/// </summary>
internal sealed class ManifestNode
{
    private ManifestNode(NodeKind kind, JsonPointer pointer, int line, int column)
    {
        Kind = kind;
        Pointer = pointer;
        Line = line;
        Column = column;
    }

    public NodeKind Kind { get; }

    public JsonPointer Pointer { get; }

    public int Line { get; }

    public int Column { get; }

    /// <summary>The string value, or a number's JSON text.</summary>
    public string? Text { get; private init; }

    public bool Boolean { get; private init; }

    public IReadOnlyList<KeyValuePair<string, ManifestNode>> Members { get; private init; } = [];

    public IReadOnlyList<ManifestNode> Items { get; private init; } = [];

    public bool IsObject => Kind == NodeKind.Object;

    public bool IsArray => Kind == NodeKind.Array;

    public bool IsString => Kind == NodeKind.String;

    /// <summary>The member called <paramref name="name"/> (ordinal), or null.</summary>
    public ManifestNode? this[string name]
    {
        get
        {
            foreach (var member in Members)
            {
                if (string.Equals(member.Key, name, StringComparison.Ordinal))
                {
                    return member.Value;
                }
            }

            return null;
        }
    }

    /// <summary>The string value of member <paramref name="name"/>, or null when absent or not a string.</summary>
    public string? StringOf(string name) => this[name] is { Kind: NodeKind.String } node ? node.Text : null;

    public static ManifestNode Object(JsonPointer pointer, int line, int column, IReadOnlyList<KeyValuePair<string, ManifestNode>> members) =>
        new(NodeKind.Object, pointer, line, column) { Members = members };

    public static ManifestNode Array(JsonPointer pointer, int line, int column, IReadOnlyList<ManifestNode> items) =>
        new(NodeKind.Array, pointer, line, column) { Items = items };

    public static ManifestNode String(JsonPointer pointer, int line, int column, string value) =>
        new(NodeKind.String, pointer, line, column) { Text = value };

    public static ManifestNode Number(JsonPointer pointer, int line, int column, string jsonText) =>
        new(NodeKind.Number, pointer, line, column) { Text = jsonText };

    public static ManifestNode Bool(JsonPointer pointer, int line, int column, bool value) =>
        new(NodeKind.Boolean, pointer, line, column) { Boolean = value };

    public static ManifestNode Null(JsonPointer pointer, int line, int column) => new(NodeKind.Null, pointer, line, column);

    /// <summary>Visits this node and every descendant, depth first in source order.</summary>
    public IEnumerable<ManifestNode> Descendants()
    {
        var stack = new Stack<ManifestNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Members.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Members[i].Value);
            }

            for (var i = node.Items.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Items[i]);
            }
        }
    }

    /// <summary>The tree as UTF-8 JSON (the instance the schema validator reads; YAML scalars already in JSON form).</summary>
    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true, MaxDepth = 1024 }))
        {
            Write(writer);
        }

        return buffer.ToArray();
    }

    private void Write(Utf8JsonWriter writer)
    {
        switch (Kind)
        {
            case NodeKind.Object:
                writer.WriteStartObject();
                foreach (var (name, value) in Members)
                {
                    writer.WritePropertyName(name);
                    value.Write(writer);
                }

                writer.WriteEndObject();
                break;
            case NodeKind.Array:
                writer.WriteStartArray();
                foreach (var item in Items)
                {
                    item.Write(writer);
                }

                writer.WriteEndArray();
                break;
            case NodeKind.String:
                writer.WriteStringValue(Text);
                break;
            case NodeKind.Number:
                writer.WriteRawValue(Encoding.UTF8.GetBytes(Text!), skipInputValidation: true);
                break;
            case NodeKind.Boolean:
                writer.WriteBooleanValue(Boolean);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}

/// <summary>The tree bounds (contracts/manifest-validation.md "Bounds"); contract constants, not configuration.</summary>
internal static class TreeBounds
{
    public const int MaxDepth = 64;
    public const int MaxNodes = 50_000;
    public const int MaxNameLength = 1_024;
    public const int MaxNumberLength = 1_024;

    /// <summary>The longest RFC 6901 location of any value (maintainer decision 2026-11-17, "new limit + no crash").</summary>
    public const int MaxLocationLength = 4_096;
}

/// <summary>A stage that stops processing with exactly one problem (<c>malformed</c>, <c>too_large</c>, or an unsupported envelope version, family, format or encoding).</summary>
internal sealed class ValidationStop(ManifestRule rule, ProblemLocation? location) : Exception(rule.Id)
{
    public ManifestProblem Problem { get; } = new(rule, location);
}
