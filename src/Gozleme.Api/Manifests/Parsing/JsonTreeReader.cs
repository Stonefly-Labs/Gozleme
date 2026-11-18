using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Gozleme.Api.Manifests.Parsing;

/// <summary>
/// Reads RFC 8259 JSON into a <see cref="ManifestNode"/> tree (research R3): no comments, no trailing commas, a
/// duplicate member name in any object (ordinal, per object) is <c>parse.duplicate-key</c>, a leading U+FEFF is
/// skipped (it stays in the bytes), raw control characters other than TAB, LF and CR and invalid UTF-8 are
/// <c>parse.encoding</c>, and the depth, node and name bounds stop the read as <c>too_large</c>.
/// <para>
/// Verified at implementation (T010, .NET 10): <c>JsonReaderOptions</c> has no duplicate-property option (it exists
/// only on <c>JsonDocumentOptions</c> and <c>JsonSerializerOptions</c>), so detection here is the only check.
/// </para>
/// </summary>
internal static class JsonTreeReader
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Reads <paramref name="utf8"/>; throws <see cref="ValidationStop"/> with the one stopping problem.</summary>
    /// <param name="utf8">The UTF-8 text.</param>
    /// <param name="part">The part locations refer to.</param>
    /// <param name="measure">A pointer whose string value's raw token length (in bytes) is reported, or null.</param>
    /// <param name="measured">That length, or 0 when the pointer is absent or not a string.</param>
    public static ManifestNode Read(ReadOnlyMemory<byte> utf8, string part, string? measure, out long measured)
    {
        var span = utf8.Span;
        var start = span.StartsWith(Bom) ? Bom.Length : 0;
        CheckText(utf8, start, part);
        if (span[start..].IndexOfAnyExcept(" \t\r\n"u8) < 0)
        {
            throw new ValidationStop(ManifestRules.ParseEmpty, ProblemLocation.At(part, 1, 1));
        }

        var reader = new Utf8JsonReader(span[start..], new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = TreeBounds.MaxDepth + 1,
        });
        var state = new State(new Utf8PositionCursor(utf8, start), start, part, measure);
        try
        {
            if (!reader.Read())
            {
                throw new ValidationStop(ManifestRules.ParseEmpty, ProblemLocation.At(part, 1, 1));
            }

            var root = ReadValue(ref reader, state, JsonPointer.Root, 0);
            if (reader.Read())
            {
                throw Syntax(utf8, start, reader.TokenStartIndex, state);
            }

            measured = state.Measured;
            return root;
        }
        catch (JsonException ex)
        {
            var offset = start + TextPositions.Utf8Offset(span[start..], ex.LineNumber ?? 0, ex.BytePositionInLine ?? 0);
            var (line, column) = new Utf8PositionCursor(utf8, start).At(offset);
            throw new ValidationStop(ManifestRules.ParseSyntax, ProblemLocation.At(part, line, column));
        }
    }

    /// <summary>Invalid UTF-8 or a raw control character other than TAB, LF and CR anywhere in the text.</summary>
    private static void CheckText(ReadOnlyMemory<byte> utf8, int start, string part)
    {
        var span = utf8.Span;
        var offset = start;
        while (offset < span.Length)
        {
            var status = Rune.DecodeFromUtf8(span[offset..], out var rune, out var consumed);
            if (status != OperationStatus.Done || (rune.Value < 0x20 && rune.Value is not ('\t' or '\n' or '\r')))
            {
                var (line, column) = new Utf8PositionCursor(utf8, start).At(offset);
                throw new ValidationStop(ManifestRules.ParseEncoding, ProblemLocation.At(part, line, column));
            }

            offset += consumed;
        }
    }

    private static ManifestNode ReadValue(ref Utf8JsonReader reader, State state, JsonPointer pointer, int depth)
    {
        if (++state.Nodes > TreeBounds.MaxNodes)
        {
            throw new ValidationStop(ManifestRules.BoundNodes, null);
        }

        var (line, column) = state.Cursor.At(state.Start + reader.TokenStartIndex);
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                {
                    if (depth + 1 > TreeBounds.MaxDepth)
                    {
                        throw new ValidationStop(ManifestRules.BoundDepth, null);
                    }

                    var members = new List<KeyValuePair<string, ManifestNode>>();
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var nameAt = state.Cursor.At(state.Start + reader.TokenStartIndex);
                        var name = GetString(ref reader, state, nameAt);
                        if (name.Length > TreeBounds.MaxNameLength)
                        {
                            throw new ValidationStop(ManifestRules.BoundName, null);
                        }

                        if (!names.Add(name))
                        {
                            throw new ValidationStop(ManifestRules.ParseDuplicateKey, ProblemLocation.At(state.Part, nameAt.Line, nameAt.Column));
                        }

                        reader.Read();
                        members.Add(new(name, ReadValue(ref reader, state, pointer.Child(name), depth + 1)));
                    }

                    return ManifestNode.Object(pointer, line, column, members);
                }

            case JsonTokenType.StartArray:
                {
                    if (depth + 1 > TreeBounds.MaxDepth)
                    {
                        throw new ValidationStop(ManifestRules.BoundDepth, null);
                    }

                    var items = new List<ManifestNode>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        items.Add(ReadValue(ref reader, state, pointer.Child(items.Count), depth + 1));
                    }

                    return ManifestNode.Array(pointer, line, column, items);
                }

            case JsonTokenType.String:
                if (pointer.Equals(state.Measure))
                {
                    state.Measured = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
                }

                return ManifestNode.String(pointer, line, column, GetString(ref reader, state, (line, column)));
            case JsonTokenType.Number:
                if (reader.ValueSpan.Length > TreeBounds.MaxNumberLength)
                {
                    throw new ValidationStop(ManifestRules.BoundNumber, null);
                }

                return ManifestNode.Number(pointer, line, column, Encoding.UTF8.GetString(reader.ValueSpan));
            case JsonTokenType.True:
                return ManifestNode.Bool(pointer, line, column, true);
            case JsonTokenType.False:
                return ManifestNode.Bool(pointer, line, column, false);
            default:
                return ManifestNode.Null(pointer, line, column);
        }
    }

    /// <summary>A string whose escapes form a lone surrogate cannot become UTF-8: <c>parse.encoding</c> at its position.</summary>
    private static string GetString(ref Utf8JsonReader reader, State state, (int Line, int Column) at)
    {
        try
        {
            return reader.GetString()!;
        }
        catch (InvalidOperationException)
        {
            throw new ValidationStop(ManifestRules.ParseEncoding, ProblemLocation.At(state.Part, at.Line, at.Column));
        }
    }

    private static ValidationStop Syntax(ReadOnlyMemory<byte> utf8, int start, long tokenStart, State state)
    {
        var (line, column) = new Utf8PositionCursor(utf8, start).At(start + tokenStart);
        return new ValidationStop(ManifestRules.ParseSyntax, ProblemLocation.At(state.Part, line, column));
    }

    private sealed class State(Utf8PositionCursor cursor, int start, string part, string? measure)
    {
        public Utf8PositionCursor Cursor { get; } = cursor;

        public int Start { get; } = start;

        public string Part { get; } = part;

        public JsonPointer? Measure { get; } = measure is null ? null : JsonPointer.Parse(measure);

        public long Measured { get; set; }

        public int Nodes { get; set; }
    }
}
