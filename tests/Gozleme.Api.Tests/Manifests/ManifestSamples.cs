using System.Text;
using System.Text.Json.Nodes;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>The bundled samples (web/src/samples/manifests, research R15) with the placeholder replaced.</summary>
internal static class ManifestSamples
{
    public static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "samples", "manifests");

    public static IReadOnlyList<Sample> All { get; } = Load();

    public static IEnumerable<Sample> Valid => All.Where(s => s.Expected["outcome"]!.GetValue<string>() == "accepted");

    public static Sample Get(string id) => All.Single(s => s.Id == id);

    private static List<Sample> Load()
    {
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(Directory, "index.json")))!;
        return [.. index["samples"]!.AsArray().Select(s => new Sample(
            s!["id"]!.GetValue<string>(), s["family"]!.GetValue<string>(), s["file"]!.GetValue<string>(), s["expected"]!.AsObject()))];
    }

    public sealed record Sample(string Id, string Family, string File, JsonObject Expected)
    {
        public string Text(string capability) =>
            System.IO.File.ReadAllText(Path.Combine(Directory, File)).Replace("{{capability}}", capability, StringComparison.Ordinal);

        public byte[] Body(string capability) => Encoding.UTF8.GetBytes(Text(capability));

        public JsonObject Envelope(string capability) => JsonNode.Parse(Text(capability))!.AsObject();

        public string Document => JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(Directory, File)))!["native"]!["document"]!.GetValue<string>();

        /// <summary>"category rule part pointer|line:column" of each expected problem, in order.</summary>
        public string[] ExpectedProblems() =>
        [
            .. Expected["problems"]!.AsArray().Select(p =>
            {
                var where = p!["location"] is not JsonObject location ? "-"
                    : location["pointer"] is { } pointer ? $"{location["part"]} {pointer.GetValue<string>()}"
                    : $"{location["part"]} {location["line"]}:{location["column"]}";
                return $"{p["category"]} {p["rule"]} {where}";
            }),
        ];

        public override string ToString() => Id;
    }
}
