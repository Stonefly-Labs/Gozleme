using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gozleme.Api.Manifests;
using Gozleme.Api.Telemetry;

namespace Gozleme.Api.Children;

/// <summary>Bounded label values (research R11).</summary>
internal static class ChildOutcome
{
    public const string Created = "created";
    public const string Matched = "matched";
    public const string Duplicate = "duplicate";

    public const string Recorded = "recorded";
    public const string Identical = "identical";
    public const string Refused = "refused";
}

/// <summary>
/// Child assignment, projection generation, install and re-projection spans and counters (GOZ-0008 FR-019, research
/// R11) on the shared Gözleme source and meter. Labels come from closed sets only (family, kind, outcome). Opaque child
/// and generation IDs go on read spans only after membership is confirmed. Names, titles, native names, digests,
/// documents and versions are never recorded.
/// </summary>
internal static class ChildTelemetry
{
    public const string AssignSpanName = "gozleme.children.assign";
    public const string GenerationSpanName = "gozleme.projection.generation.record";
    public const string InstallSpanName = "gozleme.children.install";
    public const string RegenerateCommand = "projection-regenerate";

    public const string DeclaredTag = "gozleme.children.declared";
    public const string CreatedTag = "gozleme.children.created";
    public const string MatchedTag = "gozleme.children.matched";
    public const string AbsentTag = "gozleme.children.absent";
    public const string DuplicatesTag = "gozleme.children.duplicates";
    public const string KindTag = "gozleme.child.kind";
    public const string ChildOutcomeTag = "gozleme.children.outcome";
    public const string GenerationTag = "gozleme.projection.generation";
    public const string AttestedTag = "gozleme.projection.attested";
    public const string GenerationOutcomeTag = "gozleme.projection.outcome";
    public const string RevisionCountTag = "gozleme.children.install.revisions";
    public const string CapabilityCountTag = "gozleme.children.install.capabilities";
    public const string ChildIdTag = "gozleme.child.id";
    public const string GenerationIdTag = "gozleme.projection.generation_id";

    private static readonly Counter<long> Assignments = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.children.assignments", unit: "{child}", description: "Child assignments by family, kind and bounded outcome");

    private static readonly Counter<long> Generations = GozlemeTelemetry.Meter.CreateCounter<long>(
        "gozleme.projection.generations", unit: "{generation}", description: "Projection generations by family and bounded outcome");

    /// <summary>The assignment span: it encloses the existing-children query, the matcher and the child inserts (T076).</summary>
    public static Activity? StartAssign() => GozlemeTelemetry.ActivitySource.StartActivity(AssignSpanName);

    /// <summary>The assignment span's counts (duplicates in install mode only).</summary>
    public static void Assigned(Activity? activity, string family, Assignment assignment, AssignMode mode)
    {
        var counts = assignment.Counts;
        activity?.SetTag(ManifestTelemetry.FamilyTag, family);
        activity?.SetTag(DeclaredTag, counts.Declared);
        activity?.SetTag(CreatedTag, counts.Created);
        activity?.SetTag(MatchedTag, counts.Matched);
        activity?.SetTag(AbsentTag, counts.Absent);
        if (mode == AssignMode.Install)
        {
            activity?.SetTag(DuplicatesTag, counts.Duplicates);
        }
    }

    /// <summary>One assignment count per entry and the generation 1 count; called only after the transaction commits.</summary>
    public static void RecordCommitted(string family, Assignment assignment)
    {
        var created = assignment.Created.ToDictionary(c => c.Id, c => c.IsDuplicate);
        foreach (var record in assignment.Records)
        {
            var outcome = !created.TryGetValue(record.ChildId, out var duplicate) ? ChildOutcome.Matched : duplicate ? ChildOutcome.Duplicate : ChildOutcome.Created;
            Assignments.Add(1, new(ManifestTelemetry.FamilyTag, family), new(KindTag, record.Kind), new(ChildOutcomeTag, outcome));
        }

        RecordGenerationOutcome(family, ChildOutcome.Recorded);
    }

    /// <summary>The generation span (number and attested flag only); it encloses the generation insert.</summary>
    public static Activity? StartGeneration(int generation)
    {
        var activity = GozlemeTelemetry.ActivitySource.StartActivity(GenerationSpanName);
        activity?.SetTag(GenerationTag, generation);
        activity?.SetTag(AttestedTag, generation == 1);
        return activity;
    }

    public static void RecordGenerationOutcome(string family, string outcome) =>
        Generations.Add(1, new(ManifestTelemetry.FamilyTag, family), new(GenerationOutcomeTag, outcome));

    public static Activity? StartInstall() => GozlemeTelemetry.ActivitySource.StartActivity(InstallSpanName);

    public static void Installed(Activity? activity, int revisions, int capabilities)
    {
        activity?.SetTag(RevisionCountTag, revisions);
        activity?.SetTag(CapabilityCountTag, capabilities);
    }

    public static void Child(Activity? activity, Guid childId) => activity?.SetTag(ChildIdTag, childId.ToString());

    public static void Generation(Activity? activity, Guid generationId) => activity?.SetTag(GenerationIdTag, generationId.ToString());
}
