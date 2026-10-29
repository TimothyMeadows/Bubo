using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

namespace OpenCaw.AssetProduction;

/// <summary>Stable asset-level selection identity, independent of per-operation evidence additions.</summary>
public static class HostPolicyIdentity
{
    public static string Fingerprint(HostExecutionContext context)
    {
        JsonNode Canonical(JsonNode node) => node switch
        {
            JsonObject value => new JsonObject(value.OrderBy(pair => pair.Key,StringComparer.Ordinal)
                .Select(pair => new KeyValuePair<string,JsonNode?>(pair.Key,pair.Value is null ? null : Canonical(pair.Value)))),
            JsonArray value => new JsonArray(value.Select(item => item is null ? null : Canonical(item)).ToArray()),
            _ => node.DeepClone()
        };
        var value = new JsonObject
        {
            ["pipeline"] = context.Pipeline, ["styleSha256"] = context.StyleSha256,
            ["engine"] = context.Engine, ["libraries"] = context.Libraries
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value).ToJsonString())));
    }
}
