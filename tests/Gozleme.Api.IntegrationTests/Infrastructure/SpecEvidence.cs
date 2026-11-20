using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Writes a measured section into a spec's <c>evidence/*.json</c>. A spec the registry marks <c>complete</c> keeps the
/// evidence it merged with: its tests still assert their limits, but later runs never rewrite it (GOZ-0008-IF-16).
/// </summary>
internal static class SpecEvidence
{
    // Relaxed escaping keeps the e2e-written text (e.g. "+") byte-identical; this is a local evidence file, not HTML.
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static void Record(string specId, string relativeFile, string section, JsonObject value)
    {
        if (IsComplete(specId))
        {
            return;
        }

        var file = GozlemeDatabase.RepoPath(relativeFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var evidence = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject() : new JsonObject { ["spec"] = specId };
        evidence[section] = value;
        File.WriteAllText(file, evidence.ToJsonString(Options) + "\n");
    }

    internal static bool IsComplete(string specId)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(GozlemeDatabase.RepoPath(".gozleme/spec-registry.yaml")))
        {
            yaml.Load(reader);
        }

        var specs = (YamlSequenceNode)((YamlMappingNode)yaml.Documents[0].RootNode)["specs"];
        var spec = specs.Children.Cast<YamlMappingNode>().Single(s => ((YamlScalarNode)s["id"]).Value == specId);
        return ((YamlScalarNode)spec["status"]).Value == "complete";
    }
}
