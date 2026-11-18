using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>GOZ-0007: the revision routes and envelopes with a chosen version, built from the GOZ-0006 samples.</summary>
public static class RevisionClient
{
    /// <summary>A throwaway test KEK: base64 of 32 fixed bytes (never a deployment value).</summary>
    public static readonly string TestKek = Convert.ToBase64String(Encoding.ASCII.GetBytes("gozleme-integration-test-kek-32b"));

    public static string Revisions(string orgId, string publisherId, string capabilityId) =>
        $"{OrgClient.Capability(orgId, publisherId, capabilityId)}/revisions";

    public static string Revision(string orgId, string publisherId, string capabilityId, string revisionId) =>
        $"{Revisions(orgId, publisherId, capabilityId)}/{revisionId}";

    public static string Keys(string orgId, string publisherId) => $"/api/v1/organizations/{orgId}/publishers/{publisherId}/signing-keys";

    public const string DefaultSample = "valid-stateless-2026-07-28";

    /// <summary>A valid sample's envelope for the capability with <paramref name="version"/> as the declared version.</summary>
    public static byte[] Envelope(string canonical, string version, string sample = DefaultSample)
    {
        var envelope = JsonNode.Parse(ManifestClient.SampleById(sample).Body(canonical))!.AsObject();
        envelope["version"] = version;
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> PublishAsync(HttpClient client, string path, byte[] body)
    {
        var (status, text, response) = await ManifestClient.PostAsync(client, path, body);
        return (status, text.Length == 0 ? default : ManifestClient.Json(text), response);
    }

    /// <summary>Publishes and asserts 201; returns the revision detail.</summary>
    public static async Task<JsonElement> PublishedAsync(HttpClient client, string path, byte[] body)
    {
        var (status, json, response) = await PublishAsync(client, path, body);
        response.Dispose();
        Assert.True(status == HttpStatusCode.Created, $"{status}: {json}");
        return json;
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text.Length == 0 || response.Content.Headers.ContentType?.MediaType != "application/json" ? default : ManifestClient.Json(text));
    }
}
