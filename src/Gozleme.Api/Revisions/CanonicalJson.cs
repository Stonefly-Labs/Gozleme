using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gozleme.Api.Revisions;

/// <summary>An input outside the canonical JSON subset (research R3). A programming error for Gözleme-built values.</summary>
internal sealed class CanonicalJsonException(string message) : FormatException(message);

/// <summary>
/// RFC 8785 (JCS) for the subset statement v1 and projection v1 use (GOZ-0007 research R3,
/// contracts/attestation-statement-v1.md "Canonical bytes"): objects, arrays, strings, booleans, <c>null</c> and integers
/// of magnitude at most 2^53 − 1. Members are sorted by UTF-16 code units, recursively; strings are escaped as
/// ECMAScript <c>JSON.stringify</c> escapes them; there is no BOM and no whitespace. Any other number, a duplicate member
/// or a lone surrogate is refused, so the bytes are reproducible by any independent implementation.
/// </summary>
internal static class CanonicalJson
{
    public const long MaxInteger = (1L << 53) - 1;

    private static readonly JsonDocumentOptions Strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 };

    public static byte[] Canonicalize(JsonNode node) => Canonicalize(Encoding.UTF8.GetBytes(node.ToJsonString()));

    /// <summary>The canonical bytes of a JSON text; throws <see cref="CanonicalJsonException"/> outside the subset.</summary>
    public static byte[] Canonicalize(ReadOnlySpan<byte> json)
    {
        if (json.StartsWith("﻿"u8))
        {
            throw new CanonicalJsonException("A byte order mark is not allowed.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), Strict);
        }
        catch (JsonException)
        {
            throw new CanonicalJsonException("The input is not JSON.");
        }

        using (document)
        {
            var output = new ArrayBufferWriter<byte>(json.Length);
            Write(document.RootElement, output);
            return output.WrittenSpan.ToArray();
        }
    }

    /// <summary>True when the bytes are valid JSON inside the subset and equal their own canonical form.</summary>
    public static bool IsCanonical(ReadOnlySpan<byte> json)
    {
        try
        {
            return Canonicalize(json).AsSpan().SequenceEqual(json);
        }
        catch (CanonicalJsonException)
        {
            return false;
        }
    }

    private static void Write(JsonElement element, ArrayBufferWriter<byte> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = new List<(string Name, JsonElement Value)>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in element.EnumerateObject())
                {
                    var name = ReadString(() => member.Name);
                    if (!seen.Add(name))
                    {
                        throw new CanonicalJsonException("A member name appears twice.");
                    }

                    members.Add((name, member.Value));
                }

                // string.CompareOrdinal compares UTF-16 code units, which is RFC 8785 section 3.2.3 order.
                members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                output.Write("{"u8);
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Write(","u8);
                    }

                    WriteString(members[i].Name, output);
                    output.Write(":"u8);
                    Write(members[i].Value, output);
                }

                output.Write("}"u8);
                break;
            case JsonValueKind.Array:
                output.Write("["u8);
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        output.Write(","u8);
                    }

                    first = false;
                    Write(item, output);
                }

                output.Write("]"u8);
                break;
            case JsonValueKind.String:
                WriteString(ReadString(element.GetString), output);
                break;
            case JsonValueKind.Number:
                output.Write(Encoding.ASCII.GetBytes(Integer(element.GetRawText()).ToString(CultureInfo.InvariantCulture)));
                break;
            case JsonValueKind.True:
                output.Write("true"u8);
                break;
            case JsonValueKind.False:
                output.Write("false"u8);
                break;
            default:
                output.Write("null"u8);
                break;
        }
    }

    /// <summary>Integers only: no fraction, no exponent, within ±(2^53 − 1).</summary>
    private static long Integer(string raw)
    {
        var digits = raw.StartsWith('-') ? raw[1..] : raw;
        if (digits.Length == 0 || digits.Length > 16 || !digits.All(char.IsAsciiDigit) || (digits.Length > 1 && digits[0] == '0')
            || !long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || Math.Abs(value) > MaxInteger)
        {
            throw new CanonicalJsonException("Only integers of magnitude at most 2^53 - 1 are allowed.");
        }

        return value;
    }

    private static string ReadString(Func<string?> read)
    {
        string value;
        try
        {
            value = read() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            throw new CanonicalJsonException("A string is not valid UTF-16.");
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                throw new CanonicalJsonException("A string holds a lone surrogate.");
            }
        }

        return value;
    }

    /// <summary>ECMAScript <c>JSON.stringify</c> escaping (RFC 8785 section 3.2.2.2).</summary>
    private static void WriteString(string value, ArrayBufferWriter<byte> output)
    {
        var text = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            _ = c switch
            {
                '"' => text.Append("\\\""),
                '\\' => text.Append("\\\\"),
                '\b' => text.Append("\\b"),
                '\f' => text.Append("\\f"),
                '\n' => text.Append("\\n"),
                '\r' => text.Append("\\r"),
                '\t' => text.Append("\\t"),
                < ' ' => text.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)),
                _ => text.Append(c),
            };
        }

        output.Write(Encoding.UTF8.GetBytes(text.Append('"').ToString()));
    }
}
