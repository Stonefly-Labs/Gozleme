using Gozleme.Api.Manifests;

namespace Gozleme.Api.Children;

/// <summary>
/// Publish rule catalogue v1 (GOZ-0008 contracts/publish-delta.md, agent choice A3): rules that publication applies
/// after GOZ-0006 validation accepts. It is separate from <see cref="ManifestRules"/>, whose 40 rules and catalogue
/// version are unchanged, so validate is unchanged (FR-005a) and can answer <c>200</c> for a body publish refuses.
/// </summary>
internal static class PublishRules
{
    public const string CatalogueVersion = "1";

    public static readonly ManifestRule DuplicateEntry = new(
        "publish.duplicate_entry", ProblemCategory.InvalidValue, "Two or more projection entries have the same kind and native name.");

    public static readonly IReadOnlyList<ManifestRule> All = [DuplicateEntry];
}
