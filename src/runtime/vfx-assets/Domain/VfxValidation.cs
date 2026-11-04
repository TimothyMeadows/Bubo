namespace Bubo.Vfx.Domain;

/// <summary>Absolute admission ceilings; caller budgets may only narrow these limits.</summary>
public static class VfxLimits
{
    public const long MaximumDependencyBytes = 1024L * 1024 * 1024;
    public const long MaximumTotalDependencyBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumDependencies = 1024;
    public const int MaximumEffects = 64;
    public const int MaximumLayers = 1024;
    public const int MaximumParticles = 1048576;
    public const int MaximumVertices = 16777216;
    public const int MaximumDrawCalls = 65536;
    public const int MaximumTrailPoints = 1048576;
    public const double MaximumDurationSeconds = 3600;
}

public static class VfxValidation
{
    static readonly HashSet<string> Engines = new(StringComparer.Ordinal)
        { "THREEJS", "BABYLONJS", "PHASERJS", "UNITY", "UNREAL", "GODOT" };
    static readonly HashSet<string> LayerKinds = new(StringComparer.Ordinal)
        { "sprite", "ring", "mesh", "burst", "projectile", "trail", "impact", "ground-indicator", "beam" };

    public static ValidationReport Validate(VfxPackage package)
    {
        var issues = new List<ValidationIssue>();
        void Issue(string code, string path, string message)
        {
            if (issues.Count < 256) issues.Add(new(code, path, message));
        }
        ValidationReport Report() => new(issues.Count == 0, issues.AsReadOnly());
        if (package is null)
        {
            Issue("invalid-field", "$", "Package is required");
            return Report();
        }
        if (package.SchemaVersion is not ("bubo-vfx-package/v1" or "bubo-vfx-package/v2"))
            Issue("invalid-schema", "schemaVersion", "Expected explicit bubo-vfx-package/v1 or bubo-vfx-package/v2");
        issues.AddRange(VfxAtlasValidation.Validate(package).Issues);
        CheckId(package.Id, "id"); CheckId(package.Version, "version");
        CheckHash(package.StyleSha256, "styleSha256");
        if (package.Engine is null) Issue("invalid-field", "engine", "Engine identity is required");
        else
        {
            if (package.Engine.Id is null || !Engines.Contains(package.Engine.Id))
                Issue("invalid-engine", "engine.id", "A registered selected engine identity is required");
            CheckHash(package.Engine.ContractSha256, "engine.contractSha256");
            CheckHash(package.Engine.DefinitionSha256, "engine.definitionSha256");
            CheckHash(package.Engine.RegistrySha256, "engine.registrySha256");
        }
        if (package.Budgets is null)
        {
            Issue("invalid-field", "budgets", "Explicit finite budgets are required");
            return Report();
        }
        var budget = package.Budgets;
        Budget(budget.MaxPackageBytes, VfxJson.MaximumPackageBytes, "maxPackageBytes");
        Budget(budget.MaxDependencyBytes, VfxLimits.MaximumDependencyBytes, "maxDependencyBytes");
        Budget(budget.MaxTotalDependencyBytes, VfxLimits.MaximumTotalDependencyBytes, "maxTotalDependencyBytes");
        Budget(budget.MaxDependencies, VfxLimits.MaximumDependencies, "maxDependencies");
        Budget(budget.MaxEffects, VfxLimits.MaximumEffects, "maxEffects");
        Budget(budget.MaxLayers, VfxLimits.MaximumLayers, "maxLayers");
        Budget(budget.MaxParticles, VfxLimits.MaximumParticles, "maxParticles");
        Budget(budget.MaxVertices, VfxLimits.MaximumVertices, "maxVertices");
        Budget(budget.MaxDrawCalls, VfxLimits.MaximumDrawCalls, "maxDrawCalls");
        Budget(budget.MaxTrailPoints, VfxLimits.MaximumTrailPoints, "maxTrailPoints");
        if (!double.IsFinite(budget.MaxDurationSeconds) || budget.MaxDurationSeconds <= 0 || budget.MaxDurationSeconds > VfxLimits.MaximumDurationSeconds)
            Issue("invalid-budget", "budgets.maxDurationSeconds", "Duration budget must be positive, finite and at most 3600 seconds");

        if (package.Resources is null || package.Effects is null)
        {
            Issue("invalid-field", package.Resources is null ? "resources" : "effects", "Resource and effect arrays are required");
            return Report();
        }
        if (package.Resources.Count is < 1 or > VfxLimits.MaximumDependencies || package.Resources.Count > budget.MaxDependencies)
            Issue("budget-exceeded", "resources", "A nonempty resource inventory must fit dependency count limits");
        if (package.Effects.Count is < 1 or > VfxLimits.MaximumEffects || package.Effects.Count > budget.MaxEffects)
            Issue("budget-exceeded", "effects", "A nonempty effect inventory must fit effect count limits");
        // Bound work for callers constructing typed objects directly rather than parsing JSON.
        if (package.Resources.Count > VfxLimits.MaximumDependencies || package.Effects.Count > VfxLimits.MaximumEffects)
            return Report();

        var resourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, VfxResource>(StringComparer.Ordinal);
        long dependencyBytes = 0;
        for (var index = 0; index < package.Resources.Count; index++)
        {
            var path = $"resources[{index}]";
            var resource = package.Resources[index];
            if (resource is null) { Issue("invalid-field", path, "Resource must not be null"); continue; }
            CheckId(resource.Id, path + ".id");
            if (resource.Id is not null)
            {
                if (!resourceIds.Add(resource.Id)) Issue("duplicate-id", path + ".id", "Resource IDs must be unique without case aliases");
                resources.TryAdd(resource.Id, resource);
            }
            if (resource.Kind is not ("texture-png" or "mesh-glb"))
                Issue("invalid-resource-kind", path + ".kind", "Only texture-png and mesh-glb resource data are admitted");
            if (!CanonicalPath(resource.Path))
                Issue("invalid-path", path + ".path", "Expected a portable canonical package-relative path without traversal or aliases");
            else
            {
                if (!resourcePaths.Add(resource.Path)) Issue("duplicate-path", path + ".path", "Resource paths must be unique without case aliases");
                if ((resource.Kind == "texture-png" && !resource.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    || (resource.Kind == "mesh-glb" && !resource.Path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)))
                    Issue("invalid-resource-kind", path + ".path", "Resource extension must match the declared data kind");
            }
            CheckHash(resource.Sha256, path + ".sha256");
            if (resource.Bytes <= 0 || resource.Bytes > VfxLimits.MaximumDependencyBytes)
                Issue("invalid-resource-size", path + ".bytes", "Resource byte length must be positive and fit the absolute dependency limit");
            else if (resource.Bytes > budget.MaxDependencyBytes)
                Issue("budget-exceeded", path + ".bytes", "Resource byte length exceeds the explicit dependency budget");
            Add(ref dependencyBytes, resource.Bytes, budget.MaxTotalDependencyBytes, path + ".bytes", "Aggregate dependency bytes exceed the explicit budget");
        }

        var effectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        long layers = 0, particles = 0, vertices = 0, drawCalls = 0, trailPoints = 0;
        double duration = 0;
        for (var effectIndex = 0; effectIndex < package.Effects.Count; effectIndex++)
        {
            var effectPath = $"effects[{effectIndex}]";
            var effect = package.Effects[effectIndex];
            if (effect is null) { Issue("invalid-field", effectPath, "Effect must not be null"); continue; }
            CheckId(effect.Id, effectPath + ".id");
            if (effect.Id is not null && !effectIds.Add(effect.Id)) Issue("duplicate-id", effectPath + ".id", "Effect IDs must be unique without case aliases");
            if (!double.IsFinite(effect.DurationSeconds) || effect.DurationSeconds <= 0 || effect.DurationSeconds > VfxLimits.MaximumDurationSeconds)
                Issue("invalid-duration", effectPath + ".durationSeconds", "Effect duration must be positive, finite and within its absolute duration limit");
            else
            {
                if (effect.DurationSeconds > budget.MaxDurationSeconds)
                    Issue("budget-exceeded", effectPath + ".durationSeconds", "Effect duration exceeds the explicit duration budget");
                duration += effect.DurationSeconds;
                if (!double.IsFinite(duration) || duration > budget.MaxDurationSeconds)
                    Issue("budget-exceeded", effectPath + ".durationSeconds", "Aggregate effect duration exceeds the explicit budget");
            }
            if (effect.Layers is null) { Issue("invalid-field", effectPath + ".layers", "Layers are required"); continue; }
            if (effect.Layers.Count < 1) Issue("invalid-field", effectPath + ".layers", "Each effect requires a nonempty layer inventory");
            Add(ref layers, effect.Layers.Count, budget.MaxLayers, effectPath + ".layers", "Aggregate layers exceed the explicit budget");
            if (effect.Layers.Count > VfxLimits.MaximumLayers)
            {
                Issue("budget-exceeded", effectPath + ".layers", "Layer inventory exceeds the absolute count limit");
                continue;
            }
            var layerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var layerIndex = 0; layerIndex < effect.Layers.Count; layerIndex++)
            {
                var layerPath = $"{effectPath}.layers[{layerIndex}]";
                var layer = effect.Layers[layerIndex];
                if (layer is null) { Issue("invalid-field", layerPath, "Layer must not be null"); continue; }
                CheckId(layer.Id, layerPath + ".id");
                if (layer.Id is not null && !layerIds.Add(layer.Id)) Issue("duplicate-id", layerPath + ".id", "Layer IDs must be unique within their effect without case aliases");
                if (layer.Kind is null || !LayerKinds.Contains(layer.Kind))
                    Issue("invalid-layer-kind", layerPath + ".kind", "Unknown declarative layer kind");
                if (!double.IsFinite(layer.StartSeconds) || layer.StartSeconds < 0 || !double.IsFinite(layer.DurationSeconds) || layer.DurationSeconds <= 0
                    || !double.IsFinite(layer.StartSeconds + layer.DurationSeconds) || layer.StartSeconds + layer.DurationSeconds > effect.DurationSeconds)
                    Issue("invalid-interval", layerPath, "Layer interval must be finite, positive and wholly within the owning effect");
                LayerLimit(layer.MaxParticles, VfxLimits.MaximumParticles, layerPath + ".maxParticles");
                LayerLimit(layer.MaxVertices, VfxLimits.MaximumVertices, layerPath + ".maxVertices");
                LayerLimit(layer.MaxDrawCalls, VfxLimits.MaximumDrawCalls, layerPath + ".maxDrawCalls");
                LayerLimit(layer.MaxTrailPoints, VfxLimits.MaximumTrailPoints, layerPath + ".maxTrailPoints");
                if (layer.MaxVertices == 0 || layer.MaxDrawCalls == 0)
                    Issue("invalid-layer-limit", layerPath, "A visible layer must declare positive vertex and draw-call bounds");
                Add(ref particles, layer.MaxParticles, budget.MaxParticles, layerPath + ".maxParticles", "Aggregate particles exceed the explicit budget");
                Add(ref vertices, layer.MaxVertices, budget.MaxVertices, layerPath + ".maxVertices", "Aggregate vertices exceed the explicit budget");
                Add(ref drawCalls, layer.MaxDrawCalls, budget.MaxDrawCalls, layerPath + ".maxDrawCalls", "Aggregate draw calls exceed the explicit budget");
                Add(ref trailPoints, layer.MaxTrailPoints, budget.MaxTrailPoints, layerPath + ".maxTrailPoints", "Aggregate trail points exceed the explicit budget");
                if (layer.ResourceIds is null) { Issue("invalid-field", layerPath + ".resourceIds", "Explicit resource IDs are required"); continue; }
                if (layer.ResourceIds.Count > VfxLimits.MaximumDependencies)
                {
                    Issue("budget-exceeded", layerPath + ".resourceIds", "Layer references exceed the absolute resource count limit");
                    continue;
                }
                var layerReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var meshes = 0;
                for (var resourceIndex = 0; resourceIndex < layer.ResourceIds.Count; resourceIndex++)
                {
                    var id = layer.ResourceIds[resourceIndex]; var referencePath = $"{layerPath}.resourceIds[{resourceIndex}]";
                    CheckId(id, referencePath);
                    if (id is null) continue;
                    if (!layerReferences.Add(id)) Issue("duplicate-resource-reference", referencePath, "Layer resource references must be unique without case aliases");
                    if (!resources.TryGetValue(id, out var resource)) { Issue("missing-resource", referencePath, "Layer must reference an exact declared resource ID"); continue; }
                    referenced.Add(id);
                    if (resource.Kind == "mesh-glb") meshes++;
                    if (resource.Kind == "mesh-glb" && layer.Kind is not ("mesh" or "projectile" or "impact"))
                        Issue("incompatible-resource", referencePath, "This layer kind accepts PNG resources only");
                }
                if ((layer.Kind == "mesh" && meshes != 1) || meshes > 1)
                    Issue("incompatible-resource", layerPath + ".resourceIds", "Mesh layers require exactly one GLB; projectile and impact layers admit at most one GLB");
                if (layer.Kind is "sprite" or "projectile" or "impact" && layer.ResourceIds.Count == 0)
                    Issue("incompatible-resource", layerPath + ".resourceIds", "Sprite, projectile and impact layers require declared visual resources");
            }
        }
        foreach (var id in resources.Keys)
            if (!referenced.Contains(id)) Issue("unreferenced-resource", "resources", "Every resource must be referenced by a layer: " + id);
        return Report();

        void CheckId(string? value, string path)
        {
            if (value is null || value.Length is < 1 or > 80 || !char.IsAsciiLetterOrDigit(value[0])
                || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
                Issue("invalid-id", path, "Expected 1 to 80 ASCII identifier characters beginning with a letter or digit");
        }
        void CheckHash(string? value, string path)
        {
            if (value is null || value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                Issue("invalid-hash", path, "Expected a lowercase SHA-256 identity");
        }
        void Budget(long value, long ceiling, string name)
        {
            if (value <= 0 || value > ceiling) Issue("invalid-budget", "budgets." + name, $"Budget must be a positive integer no greater than {ceiling}");
        }
        void LayerLimit(int value, int ceiling, string path)
        {
            if (value < 0 || value > ceiling) Issue("invalid-layer-limit", path, "Layer capacity must be nonnegative and within its absolute ceiling");
        }
        void Add(ref long sum, long value, long limit, string path, string message)
        {
            if (value < 0) return;
            if (sum > long.MaxValue - value) { sum = long.MaxValue; Issue("budget-exceeded", path, message); return; }
            sum += value;
            if (sum > limit) Issue("budget-exceeded", path, message);
        }
    }

    static bool CanonicalPath(string? path)
    {
        if (path is null || path.Length is < 1 or > 1024 || path.Any(character => char.IsControl(character) || character is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*')) return false;
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.EndsWith('.') || part.EndsWith(' ')) return false;
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')) return false;
        }
        return true;
    }
}
