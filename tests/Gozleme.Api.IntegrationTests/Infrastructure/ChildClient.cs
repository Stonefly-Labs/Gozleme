using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gozleme.Api.IntegrationTests.Infrastructure;

/// <summary>GOZ-0008: the child and projection generation routes, and MCP or A2A envelopes with chosen entries.</summary>
public static class ChildClient
{
    public static string Children(string orgId, string publisherId, string capabilityId) => $"{OrgClient.Capability(orgId, publisherId, capabilityId)}/children";

    public static string Child(string orgId, string publisherId, string capabilityId, string childId) => $"{Children(orgId, publisherId, capabilityId)}/{childId}";

    public static string RevisionChildren(string orgId, string publisherId, string capabilityId, string revisionId) =>
        $"{RevisionClient.Revision(orgId, publisherId, capabilityId, revisionId)}/children";

    public static string Projections(string orgId, string publisherId, string capabilityId, string revisionId) =>
        $"{RevisionClient.Revision(orgId, publisherId, capabilityId, revisionId)}/projections";

    /// <summary>An MCP 2026-07-28 server manifest envelope whose entries are <paramref name="entries"/> (kind tool, resource or prompt), in order by kind list.</summary>
    public static byte[] Mcp(string canonical, string version, params (string Kind, string Name, string? Title)[] entries)
    {
        JsonArray List(string kind) => [.. entries.Where(e => e.Kind == kind).Select((e, i) =>
        {
            var item = new JsonObject { ["name"] = e.Name };
            if (e.Title is not null)
            {
                item["title"] = e.Title;
            }

            switch (kind)
            {
                case "tool":
                    item["inputSchema"] = new JsonObject { ["type"] = "object" };
                    break;
                case "resource":
                    item["uri"] = $"https://children.example.test/resources/{i}";
                    break;
            }

            return (JsonNode)item;
        })];

        var document = new JsonObject
        {
            ["protocolRevision"] = "2026-07-28",
            ["serverInfo"] = new JsonObject { ["name"] = "io.example.demo/children", ["version"] = "1.0.0" },
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject(), ["resources"] = new JsonObject(), ["prompts"] = new JsonObject() },
            ["tools"] = List("tool"),
            ["resources"] = List("resource"),
            ["prompts"] = List("prompt"),
        };
        return ManifestClient.Envelope(canonical, "mcp", "mcp-server-manifest", "1", "json", document.ToJsonString(), e => e["version"] = version);
    }

    /// <summary>The A2A sample card with its first skill repeated (same <c>id</c>, other title): validate accepts it, publish refuses it.</summary>
    public static byte[] A2aWithDuplicateSkill(string canonical, string version)
    {
        var envelope = JsonNode.Parse(ManifestClient.SampleById("valid-agent-card").Body(canonical))!.AsObject();
        envelope["version"] = version;
        var card = JsonNode.Parse(envelope["native"]!["document"]!.GetValue<string>())!.AsObject();
        var skills = card["skills"]!.AsArray();
        var copy = skills[0]!.DeepClone().AsObject();
        copy["name"] = "Repeated skill";
        skills.Add(copy);
        envelope["native"]!["document"] = card.ToJsonString();
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    public static string[] Ids(JsonElement list, string member = "childId") => [.. list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(member).GetString()!)];

    public static JsonElement[] Items(JsonElement list) => [.. list.GetProperty("items").EnumerateArray()];
}
