using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaera.ModelAction.Domain;
using static Hephaera.ModelAction.Application.ActionValidation;
using static Hephaera.ModelAction.Application.EffectProfileV2;

namespace Hephaera.ModelAction.Application;

/// <summary>Compiles versioned effect data. Scene clocks, geometry and gameplay remain with their owners.</summary>
public static class ActionCompilerV2
{
    public static readonly string[] EffectBudgetNames = ["maxEffectSources", "maxEffectFiles", "maxEffectDependencyBytes", "maxEffectJsonBytes", "maxTailSeconds", "maxTrailSamples"];

    public static IReadOnlyList<ActionEffectSource> Sources(JsonObject recipe)
    {
        var values = Array(recipe, "effectSources");
        if (values.Count is < 1 or > 64) throw new InvalidOperationException("Action v2 needs 1 to 64 explicit effect sources");
        var sources = ParseData<ActionEffectSource[]>(Encoding.UTF8.GetBytes(values.ToJsonString()));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            Id(source.Id); Id(source.EffectId);
            if (!ids.Add(source.Id)) throw new InvalidOperationException("Effect source IDs must be unique without case aliases");
            Reference(source.Package.Path, source.Package.Sha256); Reference(source.Profile.Path, source.Profile.Sha256);
            if (source.Profile.SchemaVersion != "hephaera-three-vfx-profile/v1") throw new InvalidOperationException("Unknown action effect profile schema");
            if (source.Preparation is { } preparation) Reference(preparation.Path, preparation.Sha256);
            Reference(source.ProductionReview.Path, source.ProductionReview.Sha256); Reference(source.Rights.Path, source.Rights.Sha256);
            if (source.Correspondences is null || source.Correspondences.Count is < 1 or > 1024)
                throw new InvalidOperationException("Every effect source requires a bounded resource correspondence inventory");
            var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var correspondence in source.Correspondences)
            {
                Id(correspondence.ResourceId); Hash(correspondence.ResourceSha256); Hash(correspondence.DecodedSha256);
                if (!resources.Add(correspondence.ResourceId) || correspondence.Kind is not ("texture" or "geometry"))
                    throw new InvalidOperationException("Effect source correspondence IDs/kinds are invalid or duplicated");
                Reference(correspondence.Report.Path, correspondence.Report.Sha256);
                IntegerBound(correspondence.Report.Bytes, 2, MaximumJsonBytes, "correspondence report bytes");
            }
        }
        return sources;
    }

    public static JsonObject Compile(JsonObject recipe, JsonObject motion, string asset, string version, ActionEffectCatalog catalog)
    {
        Fields(recipe, "schemaVersion", "fps", "actions", "targets", "budgets", "review", "provenance", "effectSources");
        if (Integer(recipe, "schemaVersion", 2, 2) != 2) throw new InvalidOperationException("Explicit action recipe v2 required");
        NoExcludedOrigins(recipe);
        if (Text(motion, "schemaVersion") != "hephaera-motion-package/v1" || Text(motion, "profile") is not ("skinned" or "articulated" or "rigid"))
            throw new InvalidOperationException("Action v2 requires a supported reviewed motion profile; mechanical-assembly remains unavailable");
        var budget = recipe["budgets"]?.AsObject() ?? throw new InvalidOperationException("Explicit action v2 budgets required");
        var maximumSources = Integer(budget, "maxEffectSources", 1, 64);
        var maximumFiles = Integer(budget, "maxEffectFiles", 1, 16384);
        var maximumDependencyBytes = Whole(budget, "maxEffectDependencyBytes", 1, 4L * 1024 * 1024 * 1024);
        var maximumJsonBytes = Whole(budget, "maxEffectJsonBytes", 1, 256L * 1024 * 1024);
        var maximumTail = Number(budget, "maxTailSeconds", 0, 60);
        var maximumTrail = Integer(budget, "maxTrailSamples", 1, 65536);
        var sourceDeclarations = Sources(recipe);
        if (sourceDeclarations.Count > maximumSources || catalog.Sources.Count != sourceDeclarations.Count)
            throw new InvalidOperationException("Inspected effect source inventory differs from the explicit recipe or source budget");
        var inspected = catalog.Sources.ToDictionary(item => item.Source.Id, StringComparer.Ordinal);
        var allFiles = new Dictionary<string, ActionEffectFile>(StringComparer.OrdinalIgnoreCase);
        long dependencyBytes = 0, jsonBytes = 0;
        foreach (var source in sourceDeclarations)
        {
            if (!inspected.TryGetValue(source.Id, out var item)
                || JsonSerializer.Serialize(source, Json) != JsonSerializer.Serialize(item.Source, Json))
                throw new InvalidOperationException("Effect catalog is not bound to the exact recipe source declaration");
            Validate(item.Profile, item.Package, source.EffectId);
            if (item.Profile.NativePackageSha256 != source.Package.Sha256
                || item.Profile.PreparationSha256 != source.Preparation?.Sha256)
                throw new InvalidOperationException("Effect profile package/preparation identity differs from the source");
            foreach (var file in item.SourceInventory.Files)
            {
                Reference(file.Path, file.Sha256); IntegerBound(file.Bytes, 1, maximumDependencyBytes, "effect source file bytes");
                if (allFiles.TryGetValue(file.Path, out var previous))
                {
                    if (previous.Path != file.Path || previous.Sha256 != file.Sha256 || previous.Bytes != file.Bytes)
                        throw new InvalidOperationException("Effect source files alias inconsistent identities");
                    continue;
                }
                allFiles.Add(file.Path, file);
                dependencyBytes = checked(dependencyBytes + file.Bytes);
                if (file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    jsonBytes = checked(jsonBytes + file.Bytes);
            }
        }
        if (allFiles.Count > maximumFiles || dependencyBytes > maximumDependencyBytes || jsonBytes > maximumJsonBytes)
            throw new InvalidOperationException("Aggregate effect source file/byte/JSON budget exceeded");

        var targets = Array(recipe, "targets"); Unique(targets, "id");
        if (targets.Count > 64) throw new InvalidOperationException("Action v2 target inventory is oversized");
        var targetMap = targets.Select(node => node!.AsObject()).ToDictionary(target => Text(target, "id"), StringComparer.Ordinal);
        foreach (var target in targetMap.Values)
        {
            var mode = Text(target, "mode");
            Fields(target, mode == "continuous-world-point"
                ? ["id", "mode", "maxDistance", "maxAgeSeconds", "maxUpdatesPerSecond", "clock", "generation", "sampleAt", "onInvalid"]
                : ["id", "mode", "maxDistance"]);
            Number(target, "maxDistance", .001, 100000);
            if (mode == "continuous-world-point")
            {
                Number(target, "maxAgeSeconds", .001, 1); Integer(target, "maxUpdatesPerSecond", 1, 1000);
                if (Text(target, "clock") != "presentation-seconds" || Text(target, "generation") != "match-action"
                    || Text(target, "sampleAt") != "interval-end-before-fx" || Text(target, "onInvalid") != "retire-effect")
                    throw new InvalidOperationException("Continuously aimed beams require explicit fresh generation-bound endpoint sampling and invalid-input retirement");
            }
            else if (mode != "fixed-world-point") throw new InvalidOperationException("Unknown action v2 target policy");
        }
        var review = recipe["review"]?.AsObject() ?? throw new InvalidOperationException("Explicit v2 review required");
        var preview = review["targets"]?.AsObject() ?? throw new InvalidOperationException("Explicit technical review target points required");
        foreach (var pair in preview)
        {
            if (!targetMap.ContainsKey(pair.Key) || pair.Value is not JsonArray point) throw new InvalidOperationException("Unknown technical review target");
            Vector(point, 3, -1000000, 1000000, "technical review target");
        }

        // Only the unchanged reviewed clip/marker timeline is shared with v1.
        // Every original v2 cue is separately validated and compiled below.
        var timelineRecipe = recipe.DeepClone().AsObject(); timelineRecipe["schemaVersion"] = 1;
        timelineRecipe.Remove("effectSources"); timelineRecipe["targets"] = new JsonArray();
        timelineRecipe["review"]!["targets"] = new JsonObject();
        foreach (var name in EffectBudgetNames) timelineRecipe["budgets"]!.AsObject().Remove(name);
        foreach (var action in Array(timelineRecipe, "actions")) action!["cues"] = new JsonArray();
        var package = ActionCompiler.CompileV1(timelineRecipe, motion, asset, version);
        package["schemaVersion"] = "hephaera-action-package/v2";
        package["targets"] = targets.DeepClone(); package["budgets"] = budget.DeepClone();
        package["budgets"]!["maxVerificationBytes"] = VerificationBytes(budget);
        package["effectSources"] = JsonSerializer.SerializeToNode(sourceDeclarations.Select(source => inspected[source.Id].RuntimeSource).ToArray(), Json);
        package["coordinateSystem"] = new JsonObject { ["runtimeUp"] = "+Y", ["runtimeForward"] = "+Z", ["units"] = "meters", ["handedness"] = "right", ["parentScale"] = "positive-uniform" };
        var motionClips = Array(motion, "clips").Select(node => node!.AsObject()).ToDictionary(clip => Text(clip, "id"), StringComparer.Ordinal);
        var anchors = Array(motion, "anchors").Select(node => Text(node!.AsObject(), "id")).ToHashSet(StringComparer.Ordinal);
        var definitions = Array(recipe, "actions").Select(node => node!.AsObject()).ToDictionary(action => Text(action, "id"), StringComparer.Ordinal);
        var usedSources = new HashSet<string>(StringComparer.Ordinal);
        long reviewFrames = 0;
        foreach (var action in Array(package, "actions").Select(node => node!.AsObject()))
        {
            var duration = Number(action, "duration", .000001, 3600);
            var definitionsForAction = Array(definitions[Text(action, "id")], "cues"); Unique(definitionsForAction, "id");
            if (definitionsForAction.Count is < 1 || definitionsForAction.Count > Integer(budget, "maxEffects", 1, 256))
                throw new InvalidOperationException("Action v2 requires real effect cues within its explicit effect budget");
            var occurrences = Array(action, "steps").Select(node => node!.AsObject()).ToDictionary(step => Text(step, "id") + ":" + Integer(step, "occurrence", 0, 15), StringComparer.Ordinal);
            double Address(JsonObject address)
            {
                Fields(address, "step", "occurrence", "offset", "marker");
                if (!occurrences.TryGetValue(Text(address, "step") + ":" + Integer(address, "occurrence", 0, 15), out var step))
                    throw new InvalidOperationException("V2 cue references a missing source step occurrence");
                if ((address["offset"] is null) == (address["marker"] is null)) throw new InvalidOperationException("V2 cue address needs exactly an offset or actual source marker");
                if (address["offset"] is not null)
                    return Number(step, "start", 0, duration) + Number(address, "offset", 0, Number(step, "end", 0, duration) - Number(step, "start", 0, duration));
                var clip = motionClips[Text(step, "clip")];
                var matches = (clip["markers"] as JsonArray ?? []).Select(node => node!.AsObject())
                    .Where(marker => Text(marker, "event") == Text(address, "marker") && Number(marker, "time", 0, 3600) >= Number(step, "sourceStart", 0, 3600) - 1e-7
                        && Number(marker, "time", 0, 3600) <= Number(step, "sourceEnd", 0, 3600) + 1e-7).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("V2 cue marker must resolve exactly once inside the reviewed source trim");
                return Number(step, "start", 0, duration) + (Number(matches[0], "time", 0, 3600) - Number(step, "sourceStart", 0, 3600)) / Number(step, "rate", .1, 4);
            }
            var compiledCues = new JsonArray(); long particles = 0, systems = 0, vertices = 0, draws = 0, history = 0, indices = 0, buffers = 0, textures = 0;
            var particlesLimit = Integer(budget, "maxParticles", 1, 65536);
            var systemsLimit = 128; var verticesLimit = 16777216; var drawsLimit = 128; var historyLimit = maximumTrail;
            long indicesLimit = 50331648, bufferLimit = 268435456, textureLimit = 268435456;
            var retirement = duration; var boundaries = new HashSet<double> { 0, duration };
            foreach (var cue in definitionsForAction.Select(node => node!.AsObject()))
            {
                Fields(cue, "id", "type", "source", "anchor", "start", "end", "placement", "target", "transform", "lifetime", "overrides", "trail");
                if (Text(cue, "type") != "effect") throw new InvalidOperationException("V2 cues require real versioned effect references; legacy cue kinds remain on v1");
                var sourceId = Id(Text(cue, "source"));
                if (!inspected.TryGetValue(sourceId, out var source)) throw new InvalidOperationException("V2 cue effect source is missing");
                usedSources.Add(sourceId);
                if (!anchors.Contains(Text(cue, "anchor"))) throw new InvalidOperationException("V2 cue anchor is not an actual reviewed source anchor");
                var start = Address(cue["start"]?.AsObject() ?? throw new InvalidOperationException("V2 cue start address required"));
                var end = Address(cue["end"]?.AsObject() ?? throw new InvalidOperationException("V2 cue end address required"));
                if (end <= start || end > duration + 1e-7) throw new InvalidOperationException("V2 cue emission interval must be positive and inside the action");
                var transform = Data<ActionEffectTransform>(cue["transform"]);
                if (transform.Units != "meters" || transform.Up != "+Y" || transform.Forward != "+Z" || transform.Handedness != "right" || transform.ParentScale != "positive-uniform")
                    throw new InvalidOperationException("V2 transforms require explicit meters, runtime basis and positive uniform nonmirrored parents");
                Components(transform.Position, 3, -1000000, 1000000, "local position"); Components(transform.Orientation, 4, -1, 1, "xyzw orientation");
                Finite(transform.Scale, .0001, 1000, "positive uniform scale");
                if (Math.Abs(transform.Orientation.Sum(value => value * value) - 1) > 1e-6) throw new InvalidOperationException("Effect orientation must be a normalized xyzw quaternion");
                var lifetime = Data<ActionEffectLifetime>(cue["lifetime"]);
                Finite(lifetime.TailSeconds, 0, maximumTail, "cue tail");
                if (lifetime.Pause is not ("follow-action" or "continue-on-scene") || lifetime.TailAttachment is not ("follow-anchor" or "capture-world-on-stop") || lifetime.Cancel != "retire-generation")
                    throw new InvalidOperationException("Unknown pause/attachment/retirement policy");
                var placement = Text(cue, "placement");
                if (placement is not ("anchor-follow" or "captured-world" or "fixed-target-projectile" or "fixed-target" or "beam")) throw new InvalidOperationException("Unknown V2 placement policy");
                if (lifetime.TailAttachment == "follow-anchor" && placement is not ("anchor-follow" or "beam"))
                    throw new InvalidOperationException("World-captured, target and projectile tails require captured world transforms");
                if (placement is "fixed-target-projectile" or "fixed-target" or "beam")
                {
                    var targetId = Text(cue, "target");
                    if (!targetMap.TryGetValue(targetId, out var target) || preview[targetId] is null) throw new InvalidOperationException("Targeted V2 effects require explicit target policy and technical preview point");
                    if (Text(target, "mode") == "continuous-world-point" && placement != "beam") throw new InvalidOperationException("Only explicit beam placement may use continuously supplied endpoints");
                }
                else if (cue.ContainsKey("target")) throw new InvalidOperationException("Untargeted effect placement cannot silently consume a target");
                var nativeEffect = source.Package.Effects.Single(effect => effect.Id == source.Source.EffectId);
                var profileLayers = source.Profile.Layers.Where(layer => layer.EffectId == source.Source.EffectId).ToDictionary(layer => layer.LayerId, StringComparer.Ordinal);
                if (source.LayerAllocations.Count != profileLayers.Count || profileLayers.Keys.Any(id => !source.LayerAllocations.ContainsKey(id))) throw new InvalidOperationException("Inspected effect allocation facts must cover every selected layer");
                if (profileLayers.Values.Any(layer => layer.Family == "beam") != (placement == "beam"))
                    throw new InvalidOperationException("Beam layer capability requires explicit beam placement and endpoints");
                if (placement == "fixed-target-projectile" && !profileLayers.Values.Any(layer => layer.Family == "projectile"))
                    throw new InvalidOperationException("Projectile placement requires an actual declared projectile layer");
                var overrides = cue["overrides"] is null ? [] : Data<ActionEffectOverride[]>(cue["overrides"]);
                var overrideLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in overrides)
                {
                    Id(value.Layer);
                    if (!profileLayers.ContainsKey(value.Layer) || !overrideLayers.Add(value.Layer)) throw new InvalidOperationException("Effect override layer is missing or duplicated");
                    if (value.SizeMultiplier is null && value.ColorMultiplier is null && value.EmissionMultiplier is null) throw new InvalidOperationException("Empty effect parameter override");
                    if (value.SizeMultiplier is { } size) Finite(size, .0001, 1, "size reduction");
                    if (value.ColorMultiplier is not null) Components(value.ColorMultiplier, 4, 0, 1, "color reduction");
                    if (value.EmissionMultiplier is { } emission) Finite(emission, 0, 1, "emission reduction");
                }
                var trails = profileLayers.Values.Where(layer => layer.Family == "trail").ToArray();
                if (trails.Length > 0)
                {
                    var policy = Data<ActionEffectTrailPolicy>(cue["trail"]);
                    if (policy.Reset != "teleport-or-discontinuity") throw new InvalidOperationException("Trails require history reset on teleport and discontinuity");
                    IntegerBound(policy.MaxSamples, 2, trails.Min(layer => layer.Trail!.HistoryPoints), "trail retained samples");
                    Finite(policy.DistanceMeters, .0001, 100000, "trail discontinuity distance");
                }
                else if (cue.ContainsKey("trail")) throw new InvalidOperationException("Trail policy requires an actual source trail layer");
                var layerTimes = new JsonArray(); var retireAt = start;
                foreach (var nativeLayer in nativeEffect.Layers)
                {
                    var layer = profileLayers[nativeLayer.Id];
                    if (nativeLayer.StartSeconds >= end - start - 1e-10) throw new InvalidOperationException("An effect layer cannot start at or after its cue emission stop");
                    var layerStart = start + nativeLayer.StartSeconds;
                    var layerStop = layerStart + layer.Emission.DurationSeconds;
                    if (layerStop > end + 1e-7) throw new InvalidOperationException("Cue window cannot truncate or stretch the exact authored profile emission duration");
                    var layerRetire = layerStop + Math.Min(lifetime.TailSeconds, layer.MaxTailSeconds);
                    if (layerRetire > layerStart + nativeLayer.DurationSeconds + 1e-7 || layerRetire > start + nativeEffect.DurationSeconds + 1e-7)
                        throw new InvalidOperationException("Effect retirement exceeds native layer/effect lifetime");
                    layerTimes.Add(new JsonObject { ["id"] = nativeLayer.Id, ["family"] = layer.Family, ["space"] = layer.Space,
                        ["start"] = layerStart, ["stopAt"] = layerStop, ["retireAt"] = layerRetire,
                        ["emissionDurationSeconds"] = layer.Emission.DurationSeconds, ["particleLifetimeSeconds"] = layer.Emission.LifetimeSeconds,
                        ["tailSeconds"] = Math.Min(lifetime.TailSeconds, layer.MaxTailSeconds) });
                    retireAt = Math.Max(retireAt, layerRetire);
                    var allocation = source.LayerAllocations[layer.LayerId];
                    systems++; particles = checked(particles + layer.Emission.Capacity); vertices = checked(vertices + allocation.Vertices); draws = checked(draws + nativeLayer.MaxDrawCalls);
                    indices = checked(indices + allocation.Indices); buffers = checked(buffers + allocation.BufferBytes); textures = checked(textures + allocation.TextureBytes);
                    history = checked(history + (long)(layer.Trail?.HistoryPoints ?? 0) * layer.Emission.Capacity);
                    boundaries.Add(layerStart); boundaries.Add(layerStop); boundaries.Add(layerRetire);
                }
                systemsLimit = Math.Min(systemsLimit, source.Profile.Budgets.MaxSystems); verticesLimit = Math.Min(verticesLimit, source.Profile.Budgets.MaxVertices);
                particlesLimit = Math.Min(particlesLimit, source.Profile.Budgets.MaxParticles);
                drawsLimit = Math.Min(drawsLimit, source.Profile.Budgets.MaxDrawCalls); historyLimit = Math.Min(historyLimit, source.Profile.Budgets.MaxTrailPoints);
                indicesLimit = Math.Min(indicesLimit, source.Profile.Budgets.MaxIndices); bufferLimit = Math.Min(bufferLimit, source.Profile.Budgets.MaxBufferBytes); textureLimit = Math.Min(textureLimit, source.Profile.Budgets.MaxTextureBytes);
                var compiled = cue.DeepClone().AsObject(); compiled["start"] = start; compiled["end"] = end; compiled["retireAt"] = retireAt; compiled["layers"] = layerTimes;
                compiledCues.Add(compiled); retirement = Math.Max(retirement, retireAt); boundaries.Add(start); boundaries.Add(end); boundaries.Add(retireAt);
            }
            if (particles > particlesLimit || systems > systemsLimit || vertices > verticesLimit || draws > drawsLimit || history > historyLimit || indices > indicesLimit || buffers > bufferLimit || textures > textureLimit)
                throw new InvalidOperationException("Aggregate action effects including tails exceed selected particle/system/vertex/index/draw/history/texture/buffer budgets");
            if (retirement > duration + maximumTail + 1e-7) throw new InvalidOperationException("Action drain exceeds its explicit bounded tail");
            if (Array(action, "markers").Count + Array(action, "steps").Count * 2 + boundaries.Count > 8192)
                throw new InvalidOperationException("V2 complete presentation boundaries exceed the finite scheduler subdivision inventory");
            action["cues"] = compiledCues; action["retireAt"] = retirement;
            var samples = new HashSet<double>(); void Sample(double value) => samples.Add(Math.Round(value, 9, MidpointRounding.ToEven));
            foreach (var value in boundaries) Sample(value);
            foreach (var marker in Array(action, "markers")) Sample(marker!["at"]!.GetValue<double>());
            foreach (var step in Array(action, "steps")) { Sample(step!["start"]!.GetValue<double>()); Sample(step["end"]!.GetValue<double>()); }
            var reviewFps = Integer(review, "fps", 1, 60);
            for (var frame = 0; frame <= Math.Ceiling(retirement * reviewFps); frame++) Sample(Math.Min(retirement, (double)frame / reviewFps));
            reviewFrames = checked(reviewFrames + samples.Count * Array(review, "cameras").Count);
        }
        if (usedSources.Count != inspected.Count) throw new InvalidOperationException("Every declared effect source must be used by a real v2 cue");
        var resolution = Array(review, "resolution");
        if (reviewFrames > Integer(budget, "maxReviewFrames", 1, 10000) || reviewFrames * resolution[0]!.GetValue<int>() * resolution[1]!.GetValue<int>() > 1073741824)
            throw new InvalidOperationException("Complete V2 motion/effect/tail review exceeds its finite frame/pixel budget");
        return package;
    }

    static T Data<T>(JsonNode? node) => node is null ? throw new InvalidOperationException("Missing typed action effect data") : ParseData<T>(Encoding.UTF8.GetBytes(node.ToJsonString()));

    static long Whole(JsonObject obj, string key, long minimum, long maximum)
    {
        if (obj[key] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var result)) throw new InvalidOperationException("Explicit integer effect budget required: " + key);
        IntegerBound(result, minimum, maximum, key); return result;
    }

    static void Vector(JsonArray array, int count, double minimum, double maximum, string name)
    {
        if (array.Count != count) throw new InvalidOperationException("Invalid " + name + " component count");
        foreach (var node in array)
            if (node is not JsonValue value || !value.TryGetValue<double>(out var number)) throw new InvalidOperationException("Invalid numeric " + name);
            else Finite(number, minimum, maximum, name);
    }

    static void Reference(string path, string sha256)
    {
        Hash(sha256);
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || Path.IsPathRooted(path) || path.Any(character => char.IsControl(character) || character is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
            throw new InvalidOperationException("Effect references require bounded canonical project-relative paths");
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.EndsWith('.') || part.EndsWith(' '))
                throw new InvalidOperationException("Effect reference has unsafe path components");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && stem[3] is >= '1' and <= '9' && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
                throw new InvalidOperationException("Effect reference uses a reserved device path");
        }
    }
}
