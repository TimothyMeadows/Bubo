using System.Text.Json;
using System.Text.Json.Serialization;
using Bubo.Vfx.Domain;
using Hephaera.ModelAction.Domain;
using static Hephaera.ModelAction.Application.ActionValidation;

namespace Hephaera.ModelAction.Application;

/// <summary>Strict data admission for the selected host profile; no rendering or tool execution.</summary>
public static class EffectProfileV2
{
    public const int MaximumJsonBytes = 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };

    public static ActionEffectProfile Parse(byte[] bytes) => ParseData<ActionEffectProfile>(bytes);

    public static T ParseData<T>(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 2 or > MaximumJsonBytes)
            throw new InvalidOperationException("Effect JSON must contain 2 to 1048576 bytes");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var nodes = 0;
            void Inspect(JsonElement node)
            {
                if (++nodes > 65536) throw new InvalidOperationException("Effect JSON exceeds its bounded node inventory");
                if (node.ValueKind == JsonValueKind.Null) throw new InvalidOperationException("Null effect fields are unsupported");
                if (node.ValueKind == JsonValueKind.String && node.GetString()!.Length > 4096)
                    throw new InvalidOperationException("Effect text exceeds its bounded length");
                if (node.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in node.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate effect JSON field: " + property.Name);
                        Inspect(property.Value);
                    }
                }
                else if (node.ValueKind == JsonValueKind.Array)
                {
                    if (node.GetArrayLength() > 4096) throw new InvalidOperationException("Effect JSON array is oversized");
                    foreach (var item in node.EnumerateArray()) Inspect(item);
                }
            }
            Inspect(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidOperationException("Missing effect data");
        }
        catch (JsonException error) { throw new InvalidOperationException("Invalid strict effect JSON: " + error.Message, error); }
    }

    public static void Validate(ActionEffectProfile profile, VfxPackage package, string selectedEffectId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(package);
        var native = VfxValidation.Validate(package);
        if (!native.IsValid) throw new InvalidOperationException("Invalid native VFX package: " + string.Join("; ", native.Issues.Select(issue => issue.Message)));
        if (profile.SchemaVersion != "hephaera-three-vfx-profile/v1" || package.Engine.Id != "THREEJS")
            throw new InvalidOperationException("Action v2 requires the explicit reviewed THREEJS production profile");
        Hash(profile.NativePackageSha256);
        if (profile.PreparationSha256 is not null) Hash(profile.PreparationSha256);
        Id(selectedEffectId);
        var selectedEffect = package.Effects.SingleOrDefault(effect => effect.Id == selectedEffectId)
            ?? throw new InvalidOperationException("Selected effect is absent from the native package");
        var b = profile.Budgets ?? throw new InvalidOperationException("Explicit production profile budgets required");
        IntegerBound(b.MaxSystems, 1, 128, "maxSystems"); IntegerBound(b.MaxParticles, 1, 65536, "maxParticles");
        IntegerBound(b.MaxTrailPoints, 0, 65536, "maxTrailPoints"); IntegerBound(b.MaxVertices, 1, 16777216, "maxVertices");
        IntegerBound(b.MaxIndices, 0, 50331648, "maxIndices"); IntegerBound(b.MaxDrawCalls, 1, 128, "maxDrawCalls");
        IntegerBound(b.MaxTextureBytes, 0, 268435456, "maxTextureBytes"); IntegerBound(b.MaxBufferBytes, 1, 268435456, "maxBufferBytes");
        Finite(b.MaxDurationSeconds, .000001, 3600, "maxDurationSeconds"); Finite(b.MaxUpdateSeconds, .001, .1, "maxUpdateSeconds");
        if (profile.Layers is null || profile.Layers.Count < 1 || profile.Layers.Count > Math.Min(128, b.MaxSystems))
            throw new InvalidOperationException("Production profile layer count exceeds its finite systems budget");
        var declarations = package.Effects.SelectMany(effect => effect.Layers.Select(layer => (effect.Id, Layer: layer)))
            .ToDictionary(item => item.Id + "/" + item.Layer.Id, item => item.Layer, StringComparer.Ordinal);
        var resources = package.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long particles = 0, trailPoints = 0;
        foreach (var layer in profile.Layers)
        {
            if (layer is null) throw new InvalidOperationException("Production layer is missing");
            Id(layer.EffectId); Id(layer.LayerId);
            var key = layer.EffectId + "/" + layer.LayerId;
            if (!seen.Add(key) || !declarations.TryGetValue(key, out var declaration) || layer.Family != declaration.Kind)
                throw new InvalidOperationException("Production layer identity/family is missing, duplicated or inconsistent");
            var em = layer.Emission ?? throw new InvalidOperationException("Production emission declaration required");
            IntegerBound(em.Capacity, 1, Math.Min(65536, declaration.MaxParticles), "emission capacity");
            IntegerBound(em.Count, 0, em.Capacity, "emission count");
            Finite(em.Rate, 0, 10000, "emission rate"); Finite(em.LifetimeSeconds, .001, 60, "particle lifetime");
            Finite(em.DurationSeconds, .001, declaration.DurationSeconds, "emission duration");
            if (em.Kind is not ("burst" or "rate") || em.Kind == "burst" && em.Rate != 0 || em.Kind == "rate" && em.Count != 0
                || em.Kind == "rate" && Math.Ceiling(em.Rate * (em.LifetimeSeconds + b.MaxUpdateSeconds)) + 2 > em.Capacity)
                throw new InvalidOperationException("Mixed or unbounded production emission is unsupported");
            if (em.Capacity > b.MaxParticles) throw new InvalidOperationException("Production layer capacity exceeds its profile budget");
            Finite(layer.Size, .0001, 1000, "layer size"); Finite(layer.Speed, 0, 10000, "layer speed");
            Finite(layer.MaxTailSeconds, 0, 60, "layer tail");
            if (em.DurationSeconds + layer.MaxTailSeconds > Math.Min(b.MaxDurationSeconds, declaration.DurationSeconds) + 1e-10)
                throw new InvalidOperationException("Production emission and tail exceed the native/profile duration");
            if (layer.Space is not ("local" or "world")) throw new InvalidOperationException("Production layer must declare local or world emission space");
            Components(layer.Color, 4, 0, 1, "linear RGBA");
            var textures = declaration.ResourceIds.Where(id => resources[id].Kind == "texture-png").ToArray();
            var meshes = declaration.ResourceIds.Where(id => resources[id].Kind == "mesh-glb").ToArray();
            if (textures.Length > 1 || textures.Length == 1 && layer.ResourceId != textures[0]
                || layer.ResourceId is not null && (!declaration.ResourceIds.Contains(layer.ResourceId, StringComparer.Ordinal)
                    || !resources.TryGetValue(layer.ResourceId, out var resource) || resource.Kind != "texture-png"))
                throw new InvalidOperationException("Production texture binding differs from the exact native resource slot");
            if (layer.Geometry is { } geometry)
            {
                switch (geometry.Kind)
                {
                    case "ring":
                        if (geometry.ResourceId is not null || geometry.InnerRadius is null || geometry.OuterRadius is null || geometry.Segments is null)
                            throw new InvalidOperationException("Ring geometry requires only explicit radii and segment bounds");
                        Finite(geometry.InnerRadius.Value, 0, 1000, "inner radius"); Finite(geometry.OuterRadius.Value, .0001, 1000, "outer radius");
                        IntegerBound(geometry.Segments.Value, 3, 128, "ring segments");
                        if (geometry.InnerRadius >= geometry.OuterRadius) throw new InvalidOperationException("Ring inner radius must be less than its outer radius");
                        break;
                    case "binding":
                        if (geometry.ResourceId is null || !meshes.Contains(geometry.ResourceId, StringComparer.Ordinal)
                            || geometry.InnerRadius is not null || geometry.OuterRadius is not null || geometry.Segments is not null)
                            throw new InvalidOperationException("Mesh geometry must bind the exact native GLB without procedural parameters");
                        break;
                    case "plane":
                        if (geometry.ResourceId is not null || geometry.InnerRadius is not null || geometry.OuterRadius is not null || geometry.Segments is not null)
                            throw new InvalidOperationException("Plane geometry has no resource or ring parameters");
                        break;
                    default: throw new InvalidOperationException("Unknown production geometry kind");
                }
            }
            if (meshes.Length > 0 && layer.Geometry?.Kind != "binding" || layer.Family == "mesh" && layer.Geometry?.Kind != "binding")
                throw new InvalidOperationException("Native mesh requires its exact reviewed geometry binding");
            if (layer.Family is "ring" or "ground-indicator" && layer.Geometry?.Kind != "ring")
                throw new InvalidOperationException("Ring and ground indicator layers require authored ring geometry");
            if (layer.Family == "beam" && (layer.ResourceId is not null || layer.Geometry is not null))
                throw new InvalidOperationException("The bounded beam profile uses only its owned untextured cylinder");
            if (layer.Family == "trail")
            {
                if (layer.Trail is null) throw new InvalidOperationException("Trail history bounds required");
                IntegerBound(layer.Trail.HistoryPoints, 2, 256, "trail history");
                var history = checked((long)layer.Trail.HistoryPoints * em.Capacity);
                if (history > declaration.MaxTrailPoints || history > b.MaxTrailPoints || layer.Speed != 0 || layer.Space != "world"
                    || em.Kind != "burst" || em.Count != 1)
                    throw new InvalidOperationException("Initial trail requires one stationary world-space burst and bounded retained history");
                if (layer.EffectId == selectedEffectId) trailPoints = checked(trailPoints + history);
            }
            else if (layer.Trail is not null) throw new InvalidOperationException("Only trail layers admit history declarations");
            var atlas = package.Atlases?.SingleOrDefault(a => a.Bindings.Any(binding => binding.EffectId == layer.EffectId && binding.LayerId == layer.LayerId));
            if (atlas is not null && atlas.ResourceId != layer.ResourceId) throw new InvalidOperationException("Profile texture differs from native atlas binding");
            if (layer.Family == "trail" && atlas is not null && (atlas.Frames.Count != 1 || atlas.Frames[0].CellWidth != atlas.Width || atlas.Frames[0].CellHeight != atlas.Height))
                throw new InvalidOperationException("The selected trail backend admits only one complete atlas cell");
            if (layer.EffectId == selectedEffectId) particles = checked(particles + em.Capacity);
        }
        if (particles > b.MaxParticles || trailPoints > b.MaxTrailPoints) throw new InvalidOperationException("Aggregate production profile capacity exceeds its budgets");
        foreach (var layer in selectedEffect.Layers)
            if (!seen.Contains(selectedEffectId + "/" + layer.Id)) throw new InvalidOperationException("Production profile must cover every selected effect layer exactly once");
    }

    public static void Finite(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum) throw new InvalidOperationException("Invalid bounded effect number: " + name);
    }

    public static void IntegerBound(long value, long minimum, long maximum, string name)
    {
        if (value < minimum || value > maximum) throw new InvalidOperationException("Invalid bounded effect integer: " + name);
    }

    public static void Components(IReadOnlyList<double>? values, int count, double minimum, double maximum, string name)
    {
        if (values is null || values.Count != count) throw new InvalidOperationException("Effect " + name + " needs exactly " + count + " components");
        foreach (var value in values) Finite(value, minimum, maximum, name);
    }
}
