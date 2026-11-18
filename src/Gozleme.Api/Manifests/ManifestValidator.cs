using System.Security.Cryptography;
using Gozleme.Api.Manifests.Adapters;
using Gozleme.Api.Manifests.Parsing;

namespace Gozleme.Api.Manifests;

/// <summary>One validation: the result and the family label for telemetry (one of the six, or <c>unknown</c>).</summary>
internal sealed record ValidationRun(ValidationResult Result, string Family, IReadOnlyList<ProblemCategory> Categories);

/// <summary>
/// The pure validation pipeline (research R5, contracts/manifest-validation.md "Stages"): envelope stages, native
/// parse, adapter checks, rules, then the projection only when nothing was found. It does no I/O and stores nothing;
/// identical input gives an identical result (FR-015).
/// </summary>
internal sealed class ManifestValidator
{
    public const int MaxNativeBytes = 512 * 1024;
    public const int MaxEntries = 1_000;
    public const int MaxProblems = 50;
    public const string UnknownFamily = "unknown";

    private readonly SchemaRegistry _schemas;
    private readonly IReadOnlyDictionary<string, IProtocolAdapter> _adapters;

    public ManifestValidator(SchemaRegistry schemas, IEnumerable<IProtocolAdapter> adapters)
    {
        _schemas = schemas;
        _adapters = adapters.ToDictionary(a => a.Family, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Families => [.. _adapters.Keys];

    public ValidationRun Validate(ReadOnlyMemory<byte> body, ManifestTarget target)
    {
        var problems = new List<ManifestProblem>();
        var family = UnknownFamily;
        try
        {
            var (envelope, envelopeTree) = EnvelopeReader.Read(body, target, _adapters, problems);
            family = envelope.Family;
            var adapter = _adapters[family];
            ManifestRuleChecks.Envelope(envelopeTree, problems);

            var nativeBytes = EnvelopeReader.NativeBytes(envelope.Document);
            if (nativeBytes.Length > MaxNativeBytes)
            {
                throw new ValidationStop(ManifestRules.BoundNative, null);
            }

            var (tree, skill) = ParseNative(envelope, nativeBytes);
            if (adapter.CountEntries(tree) > MaxEntries)
            {
                throw new ValidationStop(ManifestRules.BoundEntries, null);
            }

            adapter.Validate(tree, new NativeContext(envelope, _schemas, problems));
            ManifestRuleChecks.Native(adapter, tree, skill, problems);
            if (envelope.Encoding == NativeEncoding.Yaml)
            {
                problems.AddRange(Rules.SecretFields.YamlComments(envelope.Document.StartsWith('\uFEFF') ? envelope.Document[1..] : envelope.Document, 0, ManifestPart.Native));
            }
            else if (skill is not null)
            {
                problems.AddRange(Rules.SecretFields.YamlComments(skill.FrontMatterText, 1, ManifestPart.Native));
            }

            if (problems.Count == 0)
            {
                var native = new NativeResult(envelope.Format, envelope.FormatVersion, envelope.Encoding, envelope.Document,
                    "sha256:" + Convert.ToHexStringLower(SHA256.HashData(nativeBytes)), nativeBytes.Length);
                var summary = new EnvelopeSummary(1, family, envelope.Capability!, envelope.Version!, envelope.Title, envelope.Summary,
                    new NativeDeclaration(envelope.Format, envelope.FormatVersion, envelope.Encoding));
                return new ValidationRun(new AcceptedResult(summary, native, adapter.Project(tree, envelope)), family, []);
            }
        }
        catch (ValidationStop stop)
        {
            problems = [stop.Problem];
        }

        var ordered = Order(ManifestRuleChecks.WithoutSecretPointers(problems));
        var result = new RejectedResult([.. ordered.Take(MaxProblems).Select(ValidationProblem.From)], ordered.Count > MaxProblems);
        return new ValidationRun(result, family, [.. ordered.Select(p => p.Rule.Category).Distinct().Order()]);
    }

    /// <summary>Stage 4: encoding dispatch. An empty or whitespace-only document is <c>parse.empty</c>; failures stop.</summary>
    private static (ManifestNode Tree, SkillDocument? Skill) ParseNative(ManifestEnvelope envelope, byte[] nativeBytes)
    {
        var text = envelope.Document.StartsWith('﻿') ? envelope.Document[1..] : envelope.Document;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ValidationStop(ManifestRules.ParseEmpty, ProblemLocation.At(ManifestPart.Native, 1, 1));
        }

        switch (envelope.Encoding)
        {
            case NativeEncoding.Json:
                return (JsonTreeReader.Read(nativeBytes, ManifestPart.Native, null, out _), null);
            case NativeEncoding.Yaml:
                return (RestrictedYamlReader.Read(text, ManifestPart.Native)
                    ?? throw new ValidationStop(ManifestRules.ParseEmpty, ProblemLocation.At(ManifestPart.Native, 1, 1)), null);
            default:
                var skill = SkillMarkdownReader.Read(envelope.Document, ManifestPart.Native);
                return (skill.FrontMatter, skill);
        }
    }

    /// <summary>
    /// De-duplicates by (part, location, category, rule) and keeps every distinct rule at a location (maintainer
    /// decision 2026-11-17: all problems found are reported). Orders by part (envelope first), pointer segment by
    /// segment (indices numerically, names ordinally), category, rule.
    /// </summary>
    internal static List<ManifestProblem> Order(IEnumerable<ManifestProblem> problems) =>
        [.. problems.Distinct().Order(ProblemComparer.Instance)];

    private sealed class ProblemComparer : IComparer<ManifestProblem>
    {
        public static readonly ProblemComparer Instance = new();

        public int Compare(ManifestProblem? x, ManifestProblem? y)
        {
            var (a, b) = (x!.Location, y!.Location);
            var c = Rank(a).CompareTo(Rank(b));
            if (c == 0 && a is not null && b is not null)
            {
                c = (a.Pointer, b.Pointer) switch
                {
                    ({ } p, { } q) => ComparePointers(p, q),
                    (null, null) => (a.Line, a.Column).CompareTo((b.Line, b.Column)),
                    (null, _) => 1,
                    _ => -1,
                };
            }

            if (c == 0)
            {
                c = x.Rule.Category.CompareTo(y.Rule.Category);
            }

            return c != 0 ? c : string.CompareOrdinal(x.Rule.Id, y.Rule.Id);
        }

        private static int Rank(ProblemLocation? location) => location switch
        {
            null => 0,
            { Part: ManifestPart.Envelope } => 1,
            _ => 2,
        };

        private static int ComparePointers(string p, string q)
        {
            var a = p.Split('/');
            var b = q.Split('/');
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = int.TryParse(a[i], out var m) && int.TryParse(b[i], out var n) ? m.CompareTo(n) : string.CompareOrdinal(a[i], b[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
