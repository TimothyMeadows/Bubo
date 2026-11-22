using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaera.ModelAction.Application;

namespace Hephaera.ModelAction.Infrastructure;

/// <summary>
/// Bounded syntax admission for an original host context. Nullable host metadata
/// remains intact; the owning verifier and HostExecutionContext validate policy,
/// references and authority after cumulative dependency admission.
/// </summary>
internal static class PreparationHostContextJson
{
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 2 or > EffectProfileV2.MaximumJsonBytes)
            throw new InvalidOperationException("Original preparation context exceeds its JSON byte bound");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var nodes = 0;
            void Inspect(JsonElement node)
            {
                if (++nodes > 65536)
                    throw new InvalidOperationException("Original preparation context exceeds its node bound");
                if (node.ValueKind == JsonValueKind.String && node.GetString()!.Length > 4096)
                    throw new InvalidOperationException("Original preparation context text exceeds its bound");
                if (node.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in node.EnumerateObject())
                    {
                        if (!names.Add(property.Name))
                            throw new InvalidOperationException("Duplicate original preparation context field: " + property.Name);
                        Inspect(property.Value);
                    }
                }
                else if (node.ValueKind == JsonValueKind.Array)
                {
                    if (node.GetArrayLength() > 4096)
                        throw new InvalidOperationException("Original preparation context array exceeds its bound");
                    foreach (var item in node.EnumerateArray()) Inspect(item);
                }
            }
            Inspect(document.RootElement);
            return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject
                ?? throw new InvalidOperationException("Original preparation context must be an object");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Invalid original preparation context JSON: " + error.Message, error);
        }
    }
}
