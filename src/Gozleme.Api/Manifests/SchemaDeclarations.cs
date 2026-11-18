using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gozleme.Api.Manifests;

/// <summary>
/// Tells a real unknown member from validator noise (maintainer decision 2026-11-17, "separate real from false").
/// A failed in-place subschema discards its annotations, so <c>unevaluatedProperties</c> also reports members that
/// subschema declares. This index follows the failing evaluation path through the bundled documents to the schema
/// that owns the <c>unevaluatedProperties</c>, then asks whether that schema or any in-place applicator beside it
/// (<c>$ref</c>, <c>$dynamicRef</c>, <c>allOf</c>, <c>anyOf</c>, <c>oneOf</c>, <c>if</c>/<c>then</c>/<c>else</c>,
/// <c>dependentSchemas</c>) declares the member by <c>properties</c>, <c>patternProperties</c>, or a non-false
/// <c>additionalProperties</c>/<c>unevaluatedProperties</c>. Only <c>anyOf</c>/<c>oneOf</c> branches the object
/// matches count; the caller decides a match by validating the object against that branch alone (the validator drops
/// the results of branches inside a passing alternative, so they cannot be read from the run). When no branch matches
/// (the alternative itself is then reported as invalid), every branch counts; that is the accepted limit. Anything
/// that cannot be resolved counts as undeclared, so a real unknown member is not hidden.
/// </summary>
internal sealed class SchemaDeclarations
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly FrozenSet<string> MapKeywords =
        new[] { "properties", "patternProperties", "$defs", "definitions", "dependentSchemas", "dependencies" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ArrayKeywords =
        new[] { "allOf", "anyOf", "oneOf", "prefixItems", "items" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SchemaKeywords = new[]
    {
        "items", "additionalItems", "additionalProperties", "unevaluatedProperties", "unevaluatedItems", "not", "if", "then", "else",
        "contains", "propertyNames", "contentSchema",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly Dictionary<string, JsonNode> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonNode> _anchors = new(StringComparer.Ordinal);
    private readonly Dictionary<JsonNode, string> _baseOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JsonNode, string> _uriOf = new(ReferenceEqualityComparer.Instance);
    /// <summary>Owners resolved so far, shared across requests and capped (T085): beyond the cap an owner is resolved without being kept.</summary>
    private const int MaxCachedOwners = 4_096;

    private readonly ConcurrentDictionary<(string Root, string Evaluation), (JsonNode? Owner, bool Resolved)> _owners = new();
    private readonly ConcurrentDictionary<string, Regex?> _patterns = new(StringComparer.Ordinal);

    /// <summary>How many owners are cached (tests: the cache stays bounded across requests).</summary>
    internal int CachedOwners => _owners.Count;

    public SchemaDeclarations(IEnumerable<KeyValuePair<string, byte[]>> documents)
    {
        foreach (var (uri, bytes) in documents)
        {
            if (JsonNode.Parse(bytes) is { } root)
            {
                _resources[uri] = root;
                Index(root, uri, string.Empty);
            }
        }
    }

    /// <summary>
    /// Whether the member at <paramref name="instance"/> is declared by the schema owning the failing
    /// <c>unevaluatedProperties</c> at <paramref name="evaluation"/> (relative to <paramref name="rootUri"/>);
    /// <paramref name="matches"/> tells whether the member's object is valid against the branch schema at a URI.
    /// </summary>
    public bool IsDeclared(string rootUri, string evaluation, string instance, Func<string, bool> matches)
    {
        var slash = instance.LastIndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        if (!_owners.TryGetValue((rootUri, evaluation), out var found))
        {
            found = Owner(rootUri, evaluation);
            if (_owners.Count < MaxCachedOwners)
            {
                _owners.TryAdd((rootUri, evaluation), found);
            }
        }

        var (owner, resolved) = found;
        return resolved && owner is not null
            && Declares(owner, Unescape(instance[(slash + 1)..]), matches, new HashSet<JsonNode>(ReferenceEqualityComparer.Instance));
    }

    private (JsonNode? Owner, bool Resolved) Owner(string rootUri, string evaluation)
    {
        if (!_resources.TryGetValue(rootUri, out var node))
        {
            return (null, false);
        }

        var segments = evaluation.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToArray();
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (node is not JsonObject current)
            {
                return (null, false);
            }

            if (segment is "$ref" or "$dynamicRef")
            {
                node = current[segment] is JsonValue reference && reference.TryGetValue<string>(out var text) ? Resolve(node, text) : null;
            }
            else if (MapKeywords.Contains(segment) || current[segment] is JsonArray)
            {
                node = ++i < segments.Length - 1 ? Child(current[segment], segments[i]) : null;
            }
            else
            {
                node = current[segment];
            }

            if (node is null)
            {
                return (null, false);
            }
        }

        return (node, true);
    }

    private bool Declares(JsonNode schema, string name, Func<string, bool> matches, HashSet<JsonNode> visited)
    {
        if (schema is not JsonObject o || !visited.Add(schema))
        {
            return false;
        }

        if (o["properties"] is JsonObject properties && properties.ContainsKey(name))
        {
            return true;
        }

        if (o["patternProperties"] is JsonObject patterns && patterns.Any(p => Matches(p.Key, name)))
        {
            return true;
        }

        if (OpensAll(o["additionalProperties"]) || OpensAll(o["unevaluatedProperties"]))
        {
            return true;
        }

        return InPlace(o, matches).Any(s => Declares(s, name, matches, visited));
    }

    /// <summary>The in-place subschemas of <paramref name="o"/>; alternatives the object does not match are left out unless none matches.</summary>
    private IEnumerable<JsonNode> InPlace(JsonObject o, Func<string, bool> matches)
    {
        foreach (var keyword in (string[])["$ref", "$dynamicRef"])
        {
            if (o[keyword] is JsonValue v && v.TryGetValue<string>(out var reference) && Resolve(o, reference) is { } target)
            {
                yield return target;
            }
        }

        foreach (var keyword in (string[])["allOf", "anyOf", "oneOf"])
        {
            if (o[keyword] is not JsonArray array)
            {
                continue;
            }

            var branches = array.OfType<JsonNode>().ToList();
            var matched = keyword == "allOf" ? branches : [.. branches.Where(b => _uriOf.TryGetValue(b, out var uri) && matches(uri))];
            foreach (var branch in matched.Count > 0 ? matched : branches)
            {
                yield return branch;
            }
        }

        foreach (var keyword in (string[])["if", "then", "else"])
        {
            if (o[keyword] is { } s)
            {
                yield return s;
            }
        }

        if (o["dependentSchemas"] is JsonObject dependent)
        {
            foreach (var (_, s) in dependent)
            {
                if (s is not null)
                {
                    yield return s;
                }
            }
        }
    }

    private static bool OpensAll(JsonNode? keyword) =>
        keyword is not null && !(keyword is JsonValue v && v.TryGetValue<bool>(out var b) && !b);

    private bool Matches(string pattern, string name)
    {
        var regex = _patterns.GetOrAdd(pattern, p =>
        {
            try
            {
                return new Regex(p, RegexOptions.CultureInvariant, PatternTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        });

        try
        {
            return regex?.IsMatch(name) ?? false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private JsonNode? Resolve(JsonNode from, string reference)
    {
        var baseUri = _baseOf.TryGetValue(from, out var b) ? b : null;
        if (baseUri is null || !Uri.TryCreate(new Uri(baseUri), reference, out var target))
        {
            return null;
        }

        var resource = target.GetLeftPart(UriPartial.Query);
        var fragment = Uri.UnescapeDataString(target.Fragment.TrimStart('#'));
        if (fragment.Length == 0)
        {
            return _resources.GetValueOrDefault(resource);
        }

        if (!fragment.StartsWith('/'))
        {
            return _anchors.GetValueOrDefault(resource + "#" + fragment);
        }

        var node = _resources.GetValueOrDefault(resource);
        foreach (var segment in fragment.Split('/').Skip(1).Select(Unescape))
        {
            node = Child(node, segment);
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private static JsonNode? Child(JsonNode? node, string segment) => node switch
    {
        JsonObject o => o[segment],
        JsonArray a when int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
        _ => null,
    };

    private void Index(JsonNode node, string baseUri, string pointer)
    {
        if (node is not JsonObject o || _baseOf.ContainsKey(node))
        {
            return;
        }

        if (o["$id"] is JsonValue id && id.TryGetValue<string>(out var text) && Uri.TryCreate(new Uri(baseUri), text, out var resolved))
        {
            baseUri = resolved.GetLeftPart(UriPartial.Query);
            pointer = string.Empty;
            _resources.TryAdd(baseUri, node);
        }

        _baseOf[node] = baseUri;
        _uriOf[node] = baseUri + "#" + pointer;
        foreach (var keyword in (string[])["$anchor", "$dynamicAnchor"])
        {
            if (o[keyword] is JsonValue anchor && anchor.TryGetValue<string>(out var name))
            {
                _anchors.TryAdd(baseUri + "#" + name, node);
            }
        }

        foreach (var (keyword, value) in o)
        {
            var at = pointer + "/" + Escape(keyword);
            switch (value)
            {
                case JsonObject map when MapKeywords.Contains(keyword):
                    foreach (var (name, s) in map)
                    {
                        if (s is not null)
                        {
                            Index(s, baseUri, at + "/" + Escape(name));
                        }
                    }

                    break;
                case JsonArray array when ArrayKeywords.Contains(keyword):
                    for (var n = 0; n < array.Count; n++)
                    {
                        if (array[n] is { } s)
                        {
                            Index(s, baseUri, at + "/" + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }

                    break;
                case JsonObject s when SchemaKeywords.Contains(keyword):
                    Index(s, baseUri, at);
                    break;
            }
        }
    }

    /// <summary>A JSON Pointer segment, percent-encoded so it is also a valid URI fragment.</summary>
    private static string Escape(string segment) =>
        Uri.EscapeDataString(segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));

    private static string Unescape(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
