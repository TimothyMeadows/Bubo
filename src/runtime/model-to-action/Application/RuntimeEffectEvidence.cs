using System.Text.Json.Nodes;
using Hephaera.AssetProduction;
using static Hephaera.ModelAction.Application.ActionValidation;

namespace Hephaera.ModelAction.Application;

/// <summary>Checks current supplied receiving assertions and artifacts; it cannot authenticate a reviewer or render a frame.</summary>
public static class RuntimeEffectEvidence
{
    public static void Validate(IWorkspace workspace, JsonObject receipt, JsonObject package,
        JsonObject engine, string modelSha256, string packageSha256, string runtimeBuildSha256,
        string taskPrefix, CancellationToken cancellation = default)
    {
        var runtime = receipt["runtime"]?.AsObject() ?? throw new InvalidOperationException("V2 independent/human review requires actual selected-engine receiving evidence");
        Fields(runtime, "schemaVersion", "status", "engine", "engineIdentity", "modelSha256", "packageSha256", "runtimeBuildSha256", "effectClosureSha256", "receiving", "evidence");
        if (Text(runtime, "schemaVersion") != "bubo-action-runtime-evidence/v2" || Text(runtime, "status") != "passed" || Text(runtime, "engine") != Text(engine, "effectiveEngine") || !JsonNode.DeepEquals(runtime["engineIdentity"], engine) || Text(runtime, "modelSha256") != modelSha256 || Text(runtime, "packageSha256") != packageSha256 || Text(runtime, "runtimeBuildSha256") != runtimeBuildSha256 || Text(runtime, "effectClosureSha256") != Text(package, "effectClosureSha256"))
            throw new InvalidOperationException("V2 runtime assertion conflicts with exact current model/package/closure/engine/build");
        var budget = package["budgets"]!.AsObject();
        var fileLimit = Integer(budget, "maxReviewFrames", 1, 10000) + Integer(budget, "maxEffectFiles", 1, 16384) + 256;
        var byteLimit = ActionEffectDependencies.LongBudget(budget, "maxEffectDependencyBytes", 1, 4L * 1024 * 1024 * 1024);
        var observed = new Dictionary<string, (string Sha256, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        string Reference(JsonObject reference, bool taskLocal, long maximum = ActionEffectDependencies.MaximumFileBytes)
        {
            Fields(reference, "path", "sha256", "bytes");
            var path = Relative(workspace, Text(reference, "path")); var sha = Text(reference, "sha256"); Hash(sha);
            var length = ActionEffectDependencies.LongBudget(reference, "bytes", 1, maximum);
            if (taskLocal && !path.StartsWith(taskPrefix, StringComparison.Ordinal)) throw new InvalidOperationException("V2 runtime evidence must stay in its active task cache");
            if (observed.TryGetValue(path, out var old))
            {
                if (old.Sha256 != sha || old.Bytes != length || !observed.Keys.Contains(path, StringComparer.Ordinal)) throw new InvalidOperationException("Runtime evidence aliases a different identity");
                return sha;
            }
            var snapshot = new FileInfo(ActionEffectDependencies.Resolve(workspace, path)).Length;
            if (snapshot != length || snapshot > byteLimit - bytes || observed.Count >= fileLimit) throw new InvalidOperationException("Runtime evidence exceeds remaining finite file/byte allowance before read");
            var actual = ActionEffectDependencies.Observe(workspace, path, length, cancellation);
            if (actual.Bytes != length || actual.Sha256 != sha) throw new InvalidOperationException("Missing/stale v2 receiving input or evidence");
            observed.Add(path, (sha, length)); bytes = checked(bytes + length);
            return sha;
        }
        var records = Array(runtime, "evidence").Select(node => node!.AsObject()).ToArray();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        var rolePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            Fields(record, "kind", "path", "sha256", "bytes");
            var kind = Text(record, "kind");
            if (kind is not ("runtime-test" or "runtime-capture" or "performance-profile") || roles.ContainsKey(kind) || !rolePaths.Add(Text(record, "path"))) throw new InvalidOperationException("V2 runtime evidence roles must be exact, distinct and complete");
            var reference = record.DeepClone().AsObject(); reference.Remove("kind");
            roles.Add(kind, Reference(reference, true));
        }
        if (roles.Count != 3) throw new InvalidOperationException("V2 runtime review requires test, capture and performance evidence");
        var receivingReference = runtime["receiving"]?.AsObject() ?? throw new InvalidOperationException("V2 receiving packet missing");
        Reference(receivingReference, true, ActionEffectDependencies.MaximumJsonBytes);
        // Receiving manifests may cover the complete 10,000-frame allowance;
        // their separate finite envelope is larger than one source profile.
        var receiving = ActionEffectDependencies.ParseRuntimeData<JsonObject>(ActionEffectDependencies.Read(workspace, Text(receivingReference, "path"), ActionEffectDependencies.MaximumJsonBytes, cancellation));
        Fields(receiving, "schemaVersion", "status", "actualEngine", "evidenceKind", "engine", "runtimeBuildSha256", "modelSha256", "packageSha256", "effectClosureSha256", "effectSources", "resourceBindings", "engineInputs", "coverage", "layerCoverage", "tests", "performance");
        if (Text(receiving, "schemaVersion") != "bubo-action-effect-receiving/v1" || Text(receiving, "status") != "passed" || receiving["actualEngine"]?.GetValue<bool>() != true || Text(receiving, "evidenceKind") != "selected-engine-render" || !JsonNode.DeepEquals(receiving["engine"], engine) || Text(receiving, "runtimeBuildSha256") != runtimeBuildSha256 || Text(receiving, "modelSha256") != modelSha256 || Text(receiving, "packageSha256") != packageSha256 || Text(receiving, "effectClosureSha256") != Text(package, "effectClosureSha256"))
            throw new InvalidOperationException("Motion-only or proxy review cannot establish current selected-engine V2 FX receiving evidence");

        var requiredInputs = new HashSet<string>(["action-driver", "scene-scheduler", "particle-runtime", "vfx-runtime", "postprocessing-runtime", "library-inventory", "scene", "camera", "capture-settings", "target-device"], StringComparer.Ordinal);
        var inputs = Array(receiving, "engineInputs");
        if (inputs.Count != requiredInputs.Count) throw new InvalidOperationException("Exact selected-engine receiving inputs are incomplete");
        foreach (var node in inputs)
        {
            var input = node!.AsObject(); Fields(input, "role", "path", "sha256", "bytes");
            var role = Text(input, "role");
            if (!requiredInputs.Remove(role)) throw new InvalidOperationException("Unknown/duplicate receiving engine input role");
            var reference = input.DeepClone().AsObject(); reference.Remove("role");
            var extension = Path.GetExtension(Text(reference, "path")).ToLowerInvariant();
            if (role is "action-driver" or "scene-scheduler" or "particle-runtime" or "vfx-runtime" or "postprocessing-runtime")
            {
                if (extension is not (".mjs" or ".js")) throw new InvalidOperationException("Receiving runtime identities must name actual selected-engine JavaScript modules, never executable tools/shaders");
            }
            else if (role != "scene" && extension != ".json" || role == "scene" && extension is not (".json" or ".mjs" or ".js")) throw new InvalidOperationException("Receiving environment/library identities require constrained data or scene inputs");
            workspace.HostContext?.RequireReference(Text(reference, "path"));
            Reference(reference, false);
        }
        var sources = ActionEffectDependencies.RuntimeSources(Array(package, "effectSources"));
        var sourceAssertions = Array(receiving, "effectSources").Select(node => node!.AsObject()).ToArray();
        if (sourceAssertions.Length != sources.Count) throw new InvalidOperationException("Receiving packet omits an effect source identity");
        var sourceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asserted in sourceAssertions)
        {
            Fields(asserted, "id", "effectId", "packageSha256", "profileSha256", "nativePreparationSha256", "sourceClosureSha256", "productionReviewSha256", "rightsSha256");
            var id = Text(asserted, "id");
            if (!sourceNames.Add(id)) throw new InvalidOperationException("Duplicate receiving effect source");
            var source = sources.SingleOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("Receiving packet contains an unknown effect source");
            if (Text(asserted, "effectId") != source.EffectId || Text(asserted, "packageSha256") != source.Package.Sha256 || Text(asserted, "profileSha256") != source.Profile.Sha256 || asserted["nativePreparationSha256"]?.GetValue<string>() != source.Ancestry.NativePreparationSha256 || Text(asserted, "sourceClosureSha256") != source.Ancestry.SourceClosureSha256 || Text(asserted, "productionReviewSha256") != source.Ancestry.ProductionReviewSha256 || Text(asserted, "rightsSha256") != source.Ancestry.RightsSha256)
                throw new InvalidOperationException("Receiving effect source/profile/preparation/review ancestry differs");
        }
        var bindings = Array(receiving, "resourceBindings").Select(node => node!.AsObject()).ToArray();
        if (bindings.Length != sources.Sum(source => source.Resources.Count)) throw new InvalidOperationException("Receiving decoded resource inventory is incomplete");
        var bindingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            Fields(binding, "sourceId", "resourceId", "resourceSha256", "decodedSha256", "correspondenceSha256", "sourceEvidenceSha256");
            var sourceId = Text(binding, "sourceId"); var resourceId = Text(binding, "resourceId");
            if (!bindingKeys.Add(sourceId + "/" + resourceId)) throw new InvalidOperationException("Duplicate receiving decoded resource");
            var source = sources.SingleOrDefault(item => item.Id == sourceId) ?? throw new InvalidOperationException("Unknown receiving resource owner");
            var resource = source.Resources.SingleOrDefault(item => item.Id == resourceId) ?? throw new InvalidOperationException("Unknown receiving resource");
            var correspondence = source.Correspondences.Single(item => item.ResourceId == resourceId);
            var report = EffectProfileV2.ParseData<Hephaera.ModelAction.Domain.ActionEffectCorrespondenceReport>(ActionEffectDependencies.Read(workspace, correspondence.Report.Path, ActionEffectDependencies.MaximumJsonBytes, cancellation));
            if (Text(binding, "resourceSha256") != resource.Sha256 || Text(binding, "decodedSha256") != correspondence.DecodedSha256 || Text(binding, "correspondenceSha256") != correspondence.Report.Sha256 || Text(binding, "sourceEvidenceSha256") != report.SourceEvidenceSha256)
                throw new InvalidOperationException("Receiving source/decoded/correspondence proof identities differ");
        }

        var actions = Array(package, "actions").Select(node => node!.AsObject()).ToArray();
        var coverage = Array(receiving, "coverage").Select(node => node!.AsObject()).ToArray();
        if (coverage.Length != actions.Length) throw new InvalidOperationException("V2 whole-action FX capture coverage is incomplete");
        var covered = new HashSet<string>(StringComparer.Ordinal); var frameCount = 0;
        var combinedCaptureHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in coverage)
        {
            Fields(item, "action", "start", "end", "fps", "normalSpeed", "samples", "cleanup");
            var id = Text(item, "action");
            if (!covered.Add(id)) throw new InvalidOperationException("Duplicate action FX coverage");
            var action = actions.SingleOrDefault(candidate => Text(candidate, "id") == id) ?? throw new InvalidOperationException("Unknown action FX coverage");
            var retireAt = Number(action, "retireAt", .000001, 3660);
            if (Number(item, "start", 0, 3660) != 0 || Math.Abs(Number(item, "end", 0, 3660) - retireAt) > 1e-7) throw new InvalidOperationException("Normal-speed capture must cover the complete action and bounded drain");
            var normalSpeed = item["normalSpeed"]!.AsObject();
            if (Path.GetExtension(Text(normalSpeed, "path")).ToLowerInvariant() is not (".mp4" or ".webm")) throw new InvalidOperationException("Complete normal-speed FX evidence requires a supplied action video");
            Reference(normalSpeed, true);
            var requiredTimes = new HashSet<double>();
            void Time(double time) => requiredTimes.Add(Math.Round(time, 9, MidpointRounding.ToEven));
            Time(0); Time(Number(action, "duration", .000001, 3600)); Time(retireAt);
            var captureFps = Integer(item, "fps", 1, 120);
            if (Math.Ceiling(retireAt * captureFps) + 1 > Integer(budget, "maxReviewFrames", 1, 10000) - frameCount) throw new InvalidOperationException("Complete declared capture cadence exceeds finite review frame allowance");
            for (var frame = 0; frame <= Math.Ceiling(retireAt * captureFps); frame++) Time(Math.Min(retireAt, (double)frame / captureFps));
            foreach (var step in Array(action, "steps")) { Time(step!["start"]!.GetValue<double>()); Time(step["end"]!.GetValue<double>()); }
            foreach (var marker in Array(action, "markers")) Time(marker!["at"]!.GetValue<double>());
            foreach (var cue in Array(action, "cues"))
            {
                Time(cue!["start"]!.GetValue<double>()); Time(cue["end"]!.GetValue<double>()); Time(cue["retireAt"]!.GetValue<double>());
                foreach (var layer in cue["layers"]!.AsArray()) { Time(layer!["start"]!.GetValue<double>()); Time(layer["stopAt"]!.GetValue<double>()); Time(layer["retireAt"]!.GetValue<double>()); }
            }
            var samples = Array(item, "samples").Select(node => node!.AsObject()).ToArray();
            frameCount = checked(frameCount + samples.Length);
            if (frameCount > Integer(budget, "maxReviewFrames", 1, 10000) || samples.Length < requiredTimes.Count) throw new InvalidOperationException("Required FX boundary evidence exceeds or omits complete coverage");
            var sampleTimes = new HashSet<double>(); var samplePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var peak = false;
            foreach (var sample in samples)
            {
                Fields(sample, "time", "artifact", "maximumOccupancy");
                var time = Number(sample, "time", 0, retireAt);
                if (!sampleTimes.Add(Math.Round(time, 9, MidpointRounding.ToEven))) throw new InvalidOperationException("Duplicate FX boundary capture time");
                var artifact = sample["artifact"]!.AsObject();
                if (!Text(artifact, "path").EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !samplePaths.Add(Text(artifact, "path"))) throw new InvalidOperationException("FX samples require distinct exact PNG artifacts");
                combinedCaptureHashes.Add(Reference(artifact, true));
                if (sample["maximumOccupancy"]?.GetValue<bool>() == true) peak = true;
            }
            if (!peak || requiredTimes.Any(time => !sampleTimes.Contains(time))) throw new InvalidOperationException("FX capture omits an exact semantic boundary or maximum occupancy");
            var cleanup = item["cleanup"]!.AsObject(); Fields(cleanup, "ownedEffects", "resourceLeases", "rigWrites");
            foreach (var role in new[] { "ownedEffects", "resourceLeases", "rigWrites" }) if (Integer(cleanup, role, 0, int.MaxValue) != 0) throw new InvalidOperationException("FX final cleanup retains owned effects, resource leases or rig writes");
        }
        var requiredLayers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var profile = EffectProfileV2.Parse(ActionEffectDependencies.Read(workspace, source.Profile.Path, ActionEffectDependencies.MaximumJsonBytes, cancellation));
            foreach (var layer in profile.Layers.Where(layer => layer.EffectId == source.EffectId)) requiredLayers.Add(source.Id + "/" + source.EffectId + "/" + layer.LayerId, layer.Family);
        }
        var layerCoverage = Array(receiving, "layerCoverage");
        if (layerCoverage.Count != requiredLayers.Count) throw new InvalidOperationException("Every actual effect layer requires isolated and combined selected-engine captures");
        foreach (var node in layerCoverage)
        {
            var layer = node!.AsObject();
            Fields(layer, "sourceId", "effectId", "layerId", "family", "isolated", "dark", "light", "combinedSha256");
            var key = Text(layer, "sourceId") + "/" + Text(layer, "effectId") + "/" + Text(layer, "layerId");
            if (!requiredLayers.Remove(key, out var family) || family != Text(layer, "family") || !combinedCaptureHashes.Contains(Text(layer, "combinedSha256")))
                throw new InvalidOperationException("Layer capture identity/family/combined-action correspondence differs");
            var views = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "isolated", "dark", "light" })
            {
                var reference = layer[name]!.AsObject();
                if (!Text(reference, "path").EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !views.Add(Text(reference, "path"))) throw new InvalidOperationException("Isolated and contrasting-background layer captures need distinct exact PNGs");
                Reference(reference, true);
                if (++frameCount > Integer(budget, "maxReviewFrames", 1, 10000)) throw new InvalidOperationException("Layer capture coverage exceeds finite review frame budget");
            }
        }
        var requiredTests = new HashSet<string>(["exact-release", "normal-drain", "hard-cancel", "pause-resume", "replace-restart", "reentrant-callback", "actor-removal", "aggregate-scene-ownership", "concurrent-actors-and-ambient", "resource-disposal", "context-restoration", "isolated-layer-families", "contrasting-backgrounds"], StringComparer.Ordinal);
        if (Array(package, "targets").Any(target => target?["mode"]?.GetValue<string>() == "fixed-world-point")) requiredTests.Add("fixed-target-snapshot");
        if (Array(package, "targets").Any(target => target?["mode"]?.GetValue<string>() == "continuous-world-point")) { requiredTests.Add("continuous-target-freshness"); requiredTests.Add("invalid-target-retirement"); }
        if (actions.SelectMany(action => Array(action, "cues")).Any(cue => cue?["trail"] is not null)) requiredTests.Add("trail-discontinuity-reset");
        var tests = Array(receiving, "tests");
        if (tests.Count != requiredTests.Count) throw new InvalidOperationException("Complete FX lifecycle/concurrency/target tests are required");
        foreach (var node in tests)
        {
            var test = node!.AsObject(); Fields(test, "id", "status", "evidenceSha256");
            if (!requiredTests.Remove(Text(test, "id")) || Text(test, "status") != "passed" || Text(test, "evidenceSha256") != roles["runtime-test"]) throw new InvalidOperationException("FX lifecycle test assertion is missing, stale or failed");
        }
        var performance = receiving["performance"]!.AsObject();
        Fields(performance, "status", "baselineRuns", "candidateRuns", "measurement", "maxConcurrentActors", "artifactSha256");
        if (Text(performance, "status") != "passed" || Integer(performance, "baselineRuns", 5, 1000) < 5 || Integer(performance, "candidateRuns", 5, 1000) < 5 || Integer(performance, "maxConcurrentActors", 2, 1024) < 2 || Text(performance, "measurement") is not ("gpu" or "cpu-submission") || Text(performance, "artifactSha256") != roles["performance-profile"])
            throw new InvalidOperationException("V2 performance evidence needs five matched runs and actual concurrent-scene scope");
        foreach (var (path, identity) in observed)
        {
            var current = ActionEffectDependencies.Observe(workspace, path, identity.Bytes, cancellation);
            if (current.Bytes != identity.Bytes || current.Sha256 != identity.Sha256) throw new InvalidOperationException("Runtime input/evidence changed during receiving validation");
        }
        workspace.HostContext?.ValidateFresh();
    }
}
