using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bubo.Vfx.Domain;

/// <summary>Strict, bounded admission of data-only packages.</summary>
public static class VfxJson
{
    public const int MaximumPackageBytes = 1024 * 1024;
    public const int MaximumDepth = 32;

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = MaximumDepth
    };

    public static VfxPackage Parse(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 2 or > MaximumPackageBytes)
            throw new InvalidOperationException("VFX package must contain 2 to 1048576 JSON bytes");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("VFX package must be a JSON object");
            Inspect(document.RootElement);
            return JsonSerializer.Deserialize<VfxPackage>(bytes, Options)
                ?? throw new InvalidOperationException("VFX package must not be null");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Invalid strict VFX package JSON: " + error.Message, error);
        }
    }

    static void Inspect(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException("Null values are prohibited in VFX package JSON");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new InvalidOperationException("Duplicate VFX package JSON property: " + property.Name);
                Inspect(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Inspect(item);
        }
    }
}
