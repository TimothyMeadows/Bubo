namespace Bubo.Vfx.Domain;

/// <summary>Explicit PNG pixel coordinates. Engine owners perform one declared top-left-pixel to UV conversion.</summary>
public sealed record VfxAtlasMetadata
{
    public required string ResourceId { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Origin { get; init; }
    public required string Alpha { get; init; }
    public required string ColorSpace { get; init; }
    public required int Padding { get; init; }
    public required int Extrusion { get; init; }
    public required IReadOnlyList<VfxAtlasFrame> Frames { get; init; }
    public required int RateNumerator { get; init; }
    public required int RateDenominator { get; init; }
    public required string Loop { get; init; }
    public required long DurationNumerator { get; init; }
    public required int DurationDenominator { get; init; }
    public required IReadOnlyList<VfxAtlasBinding> Bindings { get; init; }
}

public sealed record VfxAtlasFrame
{
    public required int SourceFrame { get; init; }
    public required int CellX { get; init; }
    public required int CellY { get; init; }
    public required int CellWidth { get; init; }
    public required int CellHeight { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

public sealed record VfxAtlasBinding
{
    public required string EffectId { get; init; }
    public required string LayerId { get; init; }
}

public static class VfxAtlasValidation
{
    public static ValidationReport Validate(VfxPackage package)
    {
        var issues = new List<ValidationIssue>();
        void Issue(string path, string message)
        {
            if (issues.Count < 256) issues.Add(new("invalid-atlas", path, message));
        }
        ValidationReport Report() => new(issues.Count == 0, issues.AsReadOnly());
        if (package is null)
        {
            Issue("$", "Package is required");
            return Report();
        }
        if (package.SchemaVersion != "bubo-vfx-package/v2")
        {
            if (package.Atlases is not null) Issue("atlases", "Atlas declarations require explicit package/v2; v1 stays unchanged");
            return Report();
        }
        if (package.Atlases is null || package.Atlases.Count is < 1 or > VfxLimits.MaximumDependencies)
        {
            Issue("atlases", "Package/v2 requires a bounded nonempty explicit atlas inventory");
            return Report();
        }
        if (package.Resources is null || package.Effects is null || package.Resources.Count > VfxLimits.MaximumDependencies || package.Effects.Count > VfxLimits.MaximumEffects)
        {
            Issue("$", "Complete bounded resource and effect inventories are required");
            return Report();
        }
        var resources = new Dictionary<string, VfxResource>(StringComparer.Ordinal);
        foreach (var resource in package.Resources)
            if (resource?.Id is not null) resources.TryAdd(resource.Id, resource);
        var boundResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layers = new Dictionary<string, VfxLayer>(StringComparer.Ordinal);
        long totalLayers = 0;
        foreach (var effect in package.Effects)
        {
            if (effect?.Layers is null) continue;
            totalLayers += effect.Layers.Count;
            if (totalLayers > VfxLimits.MaximumLayers)
            {
                Issue("effects", "Atlas binding lookup requires bounded aggregate layers");
                return Report();
            }
            foreach (var layer in effect.Layers)
                if (VfxContractGuards.Id(effect.Id) && layer is not null && VfxContractGuards.Id(layer.Id)) layers.TryAdd(effect.Id + "/" + layer.Id, layer);
        }
        long totalFrames = 0, totalBindings = 0, totalPixels = 0;
        for (var index = 0; index < package.Atlases.Count; index++)
        {
            var a = package.Atlases[index]; var path = $"atlases[{index}]";
            if (a is null) { Issue(path, "Atlas must not be null"); continue; }
            if (!VfxContractGuards.Id(a.ResourceId) || !resources.TryGetValue(a.ResourceId ?? "", out var resource) || resource.Kind != "texture-png")
                Issue(path + ".resourceId", "Atlas must name an exact declared PNG resource");
            if (a.ResourceId is not null && !boundResources.Add(a.ResourceId)) Issue(path + ".resourceId", "Every PNG has exactly one atlas declaration");
            if (a.Width is < 1 or > PreparationValidation.MaximumDimension || a.Height is < 1 or > PreparationValidation.MaximumDimension || (long)a.Width * a.Height > PreparationValidation.MaximumPixels)
                Issue(path, "Atlas dimensions exceed the absolute pixel envelope");
            totalPixels += Math.Max(0, (long)a.Width * a.Height);
            if (totalPixels > PreparationValidation.MaximumPixels * 4)
            {
                Issue("atlases", "Aggregate declared atlas pixels exceed the 64-megapixel envelope");
                return Report();
            }
            if (a.Origin != "top-left") Issue(path + ".origin", "Only explicit top-left PNG pixel coordinates are admitted");
            if (a.Alpha != "straight") Issue(path + ".alpha", "Only straight alpha is supported");
            if (a.ColorSpace is not ("srgb" or "linear")) Issue(path + ".colorSpace", "Color interpretation must be explicit srgb or linear");
            if (a.Padding is < 0 or > 64 || a.Extrusion is < 0 or > 64) Issue(path, "Padding and extrusion must each be 0..64 pixels");
            if (a.RateNumerator is < 1 or > 1000000 || a.RateDenominator is < 1 or > 1000000) Issue(path, "Positive bounded rational frame rate is required");
            if (a.Loop is not ("once" or "repeat")) Issue(path + ".loop", "Loop intent must be once or repeat; this does not certify a seamless loop");
            if (a.Frames is null || a.Frames.Count is < 1 or > PreparationValidation.MaximumFrames)
            {
                Issue(path + ".frames", "Explicit ordered rectangles for 1..256 frames are required");
                continue;
            }
            totalFrames += a.Frames.Count;
            if (totalFrames > 4096)
            {
                Issue("atlases", "Aggregate explicit frame inventory exceeds 4096 frames");
                return Report();
            }
            if (a.RateNumerator > 0 && a.RateDenominator > 0)
            {
                var numerator = (long)a.Frames.Count * a.RateDenominator;
                var gcd = GreatestCommonDivisor(numerator, a.RateNumerator);
                if (a.DurationNumerator != numerator / gcd || a.DurationDenominator != a.RateNumerator / gcd)
                    Issue(path, "Reduced rational duration must equal frame count divided by frame rate");
                if ((double)numerator / a.RateNumerator > 3600) Issue(path, "Atlas duration exceeds one hour");
            }
            for (var frameIndex = 0; frameIndex < a.Frames.Count; frameIndex++)
            {
                var f = a.Frames[frameIndex]; var framePath = $"{path}.frames[{frameIndex}]";
                if (f is null) { Issue(framePath, "Frame rectangle must not be null"); continue; }
                if (f.SourceFrame is < 0 or > 65535) Issue(framePath + ".sourceFrame", "Explicit source-frame index must fit bounded grid/order limits");
                if (f.Width < 1 || f.Height < 1 || f.CellWidth < 1 || f.CellHeight < 1 || f.CellX < 0 || f.CellY < 0 || f.X < 0 || f.Y < 0
                    || (long)f.CellX + f.CellWidth > a.Width || (long)f.CellY + f.CellHeight > a.Height
                    || (long)f.X + f.Width > a.Width || (long)f.Y + f.Height > a.Height)
                    Issue(framePath, "Every complete cell and interior rectangle must lie inside the atlas");
                var border = (long)a.Padding + a.Extrusion;
                if (f.X != (long)f.CellX + border || f.Y != (long)f.CellY + border
                    || f.CellWidth != f.Width + 2 * border || f.CellHeight != f.Height + 2 * border)
                    Issue(framePath, "Interior, extrusion and transparent padding must exactly partition the cell");
                for (var previousIndex = 0; previousIndex < frameIndex; previousIndex++)
                {
                    var previous = a.Frames[previousIndex];
                    if (previous is not null && f.CellX < (long)previous.CellX + previous.CellWidth && previous.CellX < (long)f.CellX + f.CellWidth
                        && f.CellY < (long)previous.CellY + previous.CellHeight && previous.CellY < (long)f.CellY + f.CellHeight)
                    { Issue(framePath, "Frame cells must not overlap"); break; }
                }
            }
            if (a.Bindings is null || a.Bindings.Count is < 1 or > VfxLimits.MaximumLayers)
            {
                Issue(path + ".bindings", "A bounded nonempty exact effect/layer binding inventory is required");
                continue;
            }
            totalBindings += a.Bindings.Count;
            if (totalBindings > VfxLimits.MaximumLayers)
            {
                Issue("atlases", "Aggregate explicit bindings exceed the layer envelope");
                return Report();
            }
            foreach (var binding in a.Bindings)
            {
                if (binding is null || !VfxContractGuards.Id(binding.EffectId) || !VfxContractGuards.Id(binding.LayerId))
                { Issue(path + ".bindings", "Bindings require exact effect and layer identifiers"); continue; }
                if (!boundLayers.Add(binding.EffectId + "/" + binding.LayerId)) Issue(path + ".bindings", "Each layer has at most one explicit atlas binding");
                layers.TryGetValue(binding.EffectId + "/" + binding.LayerId, out var layer);
                if (layer?.ResourceIds is null || layer.ResourceIds.Count > VfxLimits.MaximumDependencies || !layer.ResourceIds.Contains(a.ResourceId, StringComparer.Ordinal))
                    Issue(path + ".bindings", "Atlas binding must match an existing layer's exact resource reference");
            }
        }
        foreach (var resource in resources.Values)
            if (resource.Kind == "texture-png" && !boundResources.Contains(resource.Id)) Issue("atlases", "Every package/v2 PNG requires explicit atlas metadata: " + resource.Id);
        foreach (var (key, layer) in layers)
        {
            if (layer.ResourceIds is null || layer.ResourceIds.Count > VfxLimits.MaximumDependencies) continue;
            var textures = layer.ResourceIds.Count(id => id is not null && resources.TryGetValue(id, out var resource) && resource.Kind == "texture-png");
            if (textures > 1) Issue("effects", "Package/v2 has one explicit atlas texture slot per layer; multiple PNG slots are unsupported: " + key);
            if (textures == 1 && !boundLayers.Contains(key)) Issue("effects", "Every texture-consuming layer requires its own explicit atlas binding: " + key);
        }
        return Report();
    }

    public static long GreatestCommonDivisor(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
