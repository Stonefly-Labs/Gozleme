using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Gozleme.Api.Manifests.Parsing;

/// <summary>
/// Restricted YAML (maintainer P2, research R3): only the low-level <see cref="Parser"/> event stream is used, so
/// nothing is expanded before it is inspected. Anchors, aliases, explicit tags (including <c>!!str</c>), merge keys,
/// complex keys and more than one document are <c>parse.yaml-feature</c>; a duplicate key is
/// <c>parse.duplicate-key</c>. Plain scalars resolve with the YAML 1.2 core schema, so <c>yes</c>/<c>no</c>/<c>on</c>/
/// <c>off</c> stay strings. Depth and node counts stop the read before more tree is built.
/// <para>
/// A core-schema number is kept in JSON form (<c>0x1F</c> → <c>31</c>, <c>+1</c> → <c>1</c>, <c>.5</c> → <c>0.5</c>);
/// <c>.inf</c> and <c>.nan</c> have no JSON form and are kept as strings (agent choice, disclosed in progress.yaml).
/// </para>
/// </summary>
internal static partial class RestrictedYamlReader
{
    /// <summary>Reads one YAML document; null when the text holds none. Positions are shifted by <paramref name="lineOffset"/> lines.</summary>
    public static ManifestNode? Read(string text, string part, int lineOffset = 0)
    {
        CheckControlCharacters(text, part, lineOffset);
        var state = new State(text, part, lineOffset);
        var parser = new Parser(new StringReader(text));
        try
        {
            parser.Consume<StreamStart>();
            if (parser.Accept<StreamEnd>(out _))
            {
                return null;
            }

            var document = parser.Consume<DocumentStart>();
            if (parser.Accept<DocumentEnd>(out _))
            {
                return null;
            }

            var root = ReadNode(parser, state, JsonPointer.Root, 0);
            parser.Consume<DocumentEnd>();
            if (parser.Accept<DocumentStart>(out var second))
            {
                throw state.Feature(second.Start);
            }

            parser.Consume<StreamEnd>();
            _ = document;
            return root;
        }
        catch (YamlException ex)
        {
            var (line, column) = TextPositions.Of(text, (int)Math.Min(ex.Start.Index, text.Length));
            throw new ValidationStop(ManifestRules.ParseSyntax, ProblemLocation.At(part, line + lineOffset, column));
        }
    }

    /// <summary>
    /// The comments of YAML text that has already been read, each from its <c>#</c> to the end of its line, with the
    /// 1-based line and column of the <c>#</c> (shifted by <paramref name="lineOffset"/> lines). Comments are not parser
    /// events, so the tree never holds them; the secret scan reads them here (maintainer decision 2026-11-17, T078).
    /// </summary>
    public static IEnumerable<BodyLine> Comments(string text, int lineOffset, out List<int> columns)
    {
        var comments = new List<BodyLine>();
        columns = [];
        var scanner = new Scanner(new StringReader(text), skipComments: false);
        while (scanner.MoveNext())
        {
            if (scanner.Current is YamlDotNet.Core.Tokens.Comment comment)
            {
                var start = (int)Math.Min(comment.Start.Index, text.Length);
                var hash = text.IndexOf('#', start);
                if (hash < 0)
                {
                    continue;
                }

                var end = text.IndexOfAny(['\r', '\n'], hash);
                var (line, column) = TextPositions.Of(text, hash);
                comments.Add(new BodyLine(line + lineOffset, text[hash..(end < 0 ? text.Length : end)]));
                columns.Add(column);
            }
        }

        return comments;
    }

    /// <summary>A control character other than TAB, LF and CR is <c>parse.encoding</c> (contracts/manifest-validation.md "Invalid text").</summary>
    internal static void CheckControlCharacters(string text, string part, int lineOffset = 0)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] < 0x20 && text[i] is not ('\t' or '\n' or '\r'))
            {
                var (line, column) = TextPositions.Of(text, i);
                throw new ValidationStop(ManifestRules.ParseEncoding, ProblemLocation.At(part, line + lineOffset, column));
            }
        }
    }

    private static ManifestNode ReadNode(IParser parser, State state, JsonPointer pointer, int depth)
    {
        if (pointer.Length > TreeBounds.MaxLocationLength)
        {
            throw new ValidationStop(ManifestRules.BoundLocation, null);
        }

        if (++state.Nodes > TreeBounds.MaxNodes)
        {
            throw new ValidationStop(ManifestRules.BoundNodes, null);
        }

        var current = parser.Current ?? throw new YamlException("Unexpected end of stream.");
        if (current is AnchorAlias)
        {
            throw state.Feature(current.Start);
        }

        if (current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
        {
            throw state.Feature(current.Start);
        }

        var (line, column) = state.Position(current.Start);
        switch (current)
        {
            case Scalar scalar:
                parser.MoveNext();
                return Resolve(scalar, pointer, line, column);
            case SequenceStart:
                {
                    if (depth + 1 > TreeBounds.MaxDepth)
                    {
                        throw new ValidationStop(ManifestRules.BoundDepth, null);
                    }

                    parser.MoveNext();
                    var items = new List<ManifestNode>();
                    while (!parser.TryConsume<SequenceEnd>(out _))
                    {
                        items.Add(ReadNode(parser, state, pointer.Child(items.Count), depth + 1));
                    }

                    return ManifestNode.Array(pointer, line, column, items);
                }

            case MappingStart:
                {
                    if (depth + 1 > TreeBounds.MaxDepth)
                    {
                        throw new ValidationStop(ManifestRules.BoundDepth, null);
                    }

                    parser.MoveNext();
                    var members = new List<KeyValuePair<string, ManifestNode>>();
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    while (!parser.TryConsume<MappingEnd>(out _))
                    {
                        var keyEvent = parser.Current!;
                        if (keyEvent is not Scalar key || !key.Anchor.IsEmpty || !key.Tag.IsEmpty || key.Value == "<<")
                        {
                            throw state.Feature(keyEvent.Start);
                        }

                        if (key.Value.Length > TreeBounds.MaxNameLength)
                        {
                            throw new ValidationStop(ManifestRules.BoundName, null);
                        }

                        if (!names.Add(key.Value))
                        {
                            var at = state.Position(key.Start);
                            throw new ValidationStop(ManifestRules.ParseDuplicateKey, ProblemLocation.At(state.Part, at.Line, at.Column));
                        }

                        parser.MoveNext();
                        members.Add(new(key.Value, ReadNode(parser, state, pointer.Child(key.Value), depth + 1)));
                    }

                    return ManifestNode.Object(pointer, line, column, members);
                }

            default:
                throw new YamlException(current.Start, current.End, "Unexpected event.");
        }
    }

    /// <summary>YAML 1.2 core schema (https://yaml.org/spec/1.2.2/#1032-tag-resolution) for plain scalars; every other style is a string.</summary>
    private static ManifestNode Resolve(Scalar scalar, JsonPointer pointer, int line, int column)
    {
        var value = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain)
        {
            return ManifestNode.String(pointer, line, column, value);
        }

        if (value is "" or "~" or "null" or "Null" or "NULL")
        {
            return ManifestNode.Null(pointer, line, column);
        }

        if (value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            return ManifestNode.Bool(pointer, line, column, value[0] is 't' or 'T');
        }

        // Numeric scalars beyond the bound are refused before any BigInteger work (maintainer decision 2026-11-17, T079).
        if (value.Length > TreeBounds.MaxNumberLength && (DecimalInt().IsMatch(value) || OctalInt().IsMatch(value) || HexInt().IsMatch(value) || Float().IsMatch(value)))
        {
            throw new ValidationStop(ManifestRules.BoundNumber, null);
        }

        if (DecimalInt().IsMatch(value))
        {
            return ManifestNode.Number(pointer, line, column, BigInteger.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        }

        if (OctalInt().IsMatch(value))
        {
            var octal = value[2..].Aggregate(BigInteger.Zero, (n, d) => (n * 8) + (d - '0'));
            return ManifestNode.Number(pointer, line, column, octal.ToString(CultureInfo.InvariantCulture));
        }

        if (HexInt().IsMatch(value))
        {
            var hex = BigInteger.Parse("0" + value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return ManifestNode.Number(pointer, line, column, hex.ToString(CultureInfo.InvariantCulture));
        }

        if (Float().Match(value) is { Success: true } f)
        {
            var sign = f.Groups["sign"].Value == "-" ? "-" : string.Empty;
            var whole = f.Groups["int"].Value is { Length: > 0 } i ? i : "0";
            var fraction = f.Groups["frac"].Value is { Length: > 0 } fr ? "." + fr : string.Empty;
            var exponent = f.Groups["exp"].Value;
            return ManifestNode.Number(pointer, line, column, sign + whole.TrimStart('0').PadLeft(1, '0') + fraction + exponent);
        }

        return ManifestNode.String(pointer, line, column, value);
    }

    [GeneratedRegex("^[-+]?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalInt();

    [GeneratedRegex("^0o[0-7]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OctalInt();

    [GeneratedRegex("^0x[0-9a-fA-F]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HexInt();

    [GeneratedRegex(@"^(?<sign>[-+]?)(?:\.(?<frac>[0-9]+)|(?<int>[0-9]+)(?:\.(?<frac>[0-9]*))?)(?<exp>[eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Float();

    private sealed class State(string text, string part, int lineOffset)
    {
        private readonly StringPositionCursor _cursor = new(text);

        public string Part { get; } = part;

        public int Nodes { get; set; }

        /// <summary>The position of a mark, recomputed from its character index so a surrogate pair is one column.</summary>
        public (int Line, int Column) Position(Mark mark)
        {
            var (line, column) = _cursor.At((int)Math.Min(mark.Index, text.Length));
            return (line + lineOffset, column);
        }

        public ValidationStop Feature(Mark mark)
        {
            var (line, column) = Position(mark);
            return new ValidationStop(ManifestRules.ParseYamlFeature, ProblemLocation.At(Part, line, column));
        }
    }
}
