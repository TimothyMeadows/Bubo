using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bubo.Vfx.Domain;

public static class PreparationJson
{
    public const int MaximumRequestBytes = 1024 * 1024;
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = VfxJson.MaximumDepth
    };

    public static VfxPreparationRequest Parse(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 2 or > MaximumRequestBytes)
            throw new InvalidOperationException("Preparation request JSON must contain 2 to 1048576 bytes");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = VfxJson.MaximumDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Preparation request must be an object");
            Strict(document.RootElement);
            return JsonSerializer.Deserialize<VfxPreparationRequest>(bytes, Options)
                ?? throw new InvalidOperationException("Preparation request must not be null");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Invalid strict VFX preparation JSON: " + error.Message, error);
        }
    }

    public static byte[] Serialize(VfxPreparationRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Options);

    static void Strict(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException("Null values are prohibited in VFX preparation JSON");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidOperationException("Duplicate preparation JSON property: " + property.Name);
                Strict(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Strict(item);
    }
}
