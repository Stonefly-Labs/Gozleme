using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>GOZ-0006: the validate endpoint and the bundled samples (web/src/samples/manifests, research R15).</summary>
public static class ManifestClient
{
    public static readonly string SampleDirectory = Path.Combine(AppContext.BaseDirectory, "samples", "manifests");

    public static string Validate(string orgId, string publisherId, string capabilityId) =>
        $"{OrgClient.Capability(orgId, publisherId, capabilityId)}/manifests/validate";

    public static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> PostAsync(
        HttpClient client, string path, byte[] body, string contentType = "application/json")
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var response = await client.PostAsync(new Uri(path, UriKind.Relative), content, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), response);
    }

    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, string path, byte[] body, string contentType = "application/json")
    {
        var (status, text, response) = await PostAsync(client, path, body, contentType);
        response.Dispose();
        return (status, text);
    }

    public static IReadOnlyList<Sample> Samples { get; } = LoadSamples();

    public static Sample SampleById(string id) => Samples.Single(s => s.Id == id);

    private static List<Sample> LoadSamples()
    {
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(SampleDirectory, "index.json")))!;
        return [.. index["samples"]!.AsArray().Select(s => new Sample(s!["id"]!.GetValue<string>(), s["family"]!.GetValue<string>(), s["file"]!.GetValue<string>(), s["expected"]!.AsObject()))];
    }

    public sealed record Sample(string Id, string Family, string File, JsonObject Expected)
    {
        public bool Valid => Expected["outcome"]!.GetValue<string>() == "accepted";

        public string Raw => System.IO.File.ReadAllText(Path.Combine(SampleDirectory, File));

        public byte[] Body(string capability) => Encoding.UTF8.GetBytes(Raw.Replace("{{capability}}", capability, StringComparison.Ordinal));

        public string Document => JsonNode.Parse(Raw)!["native"]!["document"]!.GetValue<string>();

        public override string ToString() => Id;
    }

    /// <summary>An envelope for the target, from a native document.</summary>
    public static byte[] Envelope(string capability, string family, string format, string formatVersion, string encoding, string document, Action<JsonObject>? extra = null)
    {
        var envelope = new JsonObject
        {
            ["envelopeVersion"] = 1,
            ["family"] = family,
            ["capability"] = capability,
            ["version"] = "1.0.0",
            ["native"] = new JsonObject { ["format"] = format, ["formatVersion"] = formatVersion, ["encoding"] = encoding, ["document"] = document },
        };
        extra?.Invoke(envelope);
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    public static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();
}
