using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Vfx.Domain;
using Hephaera.AssetProduction;
using Hephaera.ModelAction.Application;
using Hephaera.ModelAction.Domain;
using Hephaera.ModelAction.Infrastructure;

// Original engineering resources and synthetic motion/review assertions.
// These checks do not authenticate human decisions or establish renderer quality.
static class EffectsV2Tests
{
    public static int Run(string root)
    {
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("V2 assertion failed: " + name);
            assertions++;
            Console.WriteLine("PASS V2 DATA/OWNED FILES: " + name);
        }
        void Reject(Action action, string name, string? expectedMessage = null)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or IOException or JsonException or OperationCanceledException)
            {
                if (expectedMessage is not null && !error.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Wrong rejection for " + name + ": " + error.Message, error);
                Check(true, name); return;
            }
            throw new Exception("Expected v2 rejection: " + name);
        }
        var fixture = new OwnedEffectFixture(root, "fx");
        var recipe = fixture.Recipe;
        var catalog = fixture.Inspect(recipe);
        JsonObject Compile(JsonObject? input = null, JsonObject? motion = null) =>
            ActionCompiler.Compile(input ?? recipe, motion ?? fixture.Motion, "owned-action", "v2", catalog);
        var plan = Compile();
        fixture.Workspace.Save("fx-seed-recipe.json", recipe);
        fixture.Workspace.Save("fx-seed-plan.json", plan);
        fixture.Workspace.Save("fx-seed-source.json", fixture.Source);
        var allocations = catalog.Sources.Single().LayerAllocations;
        Check(allocations.Count == 9, "native allocation facts cover every selected layer");
        Check(allocations["trail"] == new ActionEffectAllocation(20000, 60000, 1600152, 4),
            "trail reservation includes the independent 10000-slot batch-floor vector");
        Check(allocations["ring"] == new ActionEffectAllocation(26, 72, 65120, 4),
            "twelve-segment ring matches independently counted vertices, indices and batch bytes");
        Check(allocations["beam"] == new ActionEffectAllocation(18, 48, 672, 0),
            "owned eight-sided beam matches its independent allocation vector");
        Check(allocations.Values.Sum(a => a.Vertices) == 20089 && allocations.Values.Sum(a => a.Indices) == 60216
            && allocations.Values.Sum(a => a.BufferBytes) == 2003768 && allocations.Values.Sum(a => a.TextureBytes) == 32,
            "complete nine-family allocation matches independent summed renderer reservations");
        var tinyRingIndices = new OwnedEffectFixture(Path.Combine(root, "ring-indices"), "fx", maxIndices: 1, selectedFamilies: ["ring"]);
        Reject(() => tinyRingIndices.Inspect(tinyRingIndices.Recipe), "single procedural ring cannot exceed its index budget", "allocation");
        var tinyRingBuffer = new OwnedEffectFixture(Path.Combine(root, "ring-buffer"), "fx", maxBufferBytes: 65119, selectedFamilies: ["ring"]);
        Reject(() => tinyRingBuffer.Inspect(tinyRingBuffer.Recipe), "single procedural ring cannot exceed its batch buffer budget", "allocation");
        var tinyTrailBuffer = new OwnedEffectFixture(Path.Combine(root, "trail-buffer"), "fx", maxBufferBytes: 1600151, selectedFamilies: ["trail"]);
        Reject(() => tinyTrailBuffer.Inspect(tinyTrailBuffer.Recipe), "single trail cannot understate its minimum batch allocation", "allocation");
        var sprite = new OwnedEffectFixture(Path.Combine(root, "sprite-overlap"), "fx", maxIndices: 6, selectedFamilies: ["sprite"]);
        var spriteCatalog = sprite.Inspect(sprite.Recipe);
        Check(ActionCompiler.Compile(sprite.Recipe, sprite.Motion, "sprite", "v2", spriteCatalog)["actions"]!.AsArray().Count == 1,
            "one sprite cue fits its exact six-index profile budget");
        var overlappingSprite = sprite.Recipe.DeepClone().AsObject();
        var duplicateSprite = overlappingSprite["actions"]![0]!["cues"]![0]!.DeepClone(); duplicateSprite["id"] = "overlapping-sprite";
        overlappingSprite["actions"]![0]!["cues"]!.AsArray().Add(duplicateSprite);
        Reject(() => ActionCompiler.Compile(overlappingSprite, sprite.Motion, "sprite", "v2", spriteCatalog),
            "two overlapping sprite cues cannot reuse one six-index reservation", "aggregate");
        var bufferBound = new OwnedEffectFixture(Path.Combine(root, "buffer-overlap"), "fx", maxBufferBytes: 2003768);
        var bufferCatalog = bufferBound.Inspect(bufferBound.Recipe);
        Check(ActionCompiler.Compile(bufferBound.Recipe, bufferBound.Motion, "buffers", "v2", bufferCatalog)["actions"]!.AsArray().Count == 1,
            "one complete nine-layer cue fits its exact independently expected buffer allowance");
        var overlappingBuffers = bufferBound.Recipe.DeepClone().AsObject();
        var duplicateBuffers = overlappingBuffers["actions"]![0]!["cues"]![0]!.DeepClone(); duplicateBuffers["id"] = "overlapping-buffer-owner";
        overlappingBuffers["actions"]![0]!["cues"]!.AsArray().Add(duplicateBuffers);
        Reject(() => ActionCompiler.Compile(overlappingBuffers, bufferBound.Motion, "buffers", "v2", bufferCatalog),
            "overlapping cues and tails reserve their full buffer union", "aggregate");
        var nativeSourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../"));
        if (!File.Exists(Path.Combine(nativeSourceRoot, "Bubo.slnx"))) throw new InvalidOperationException("Expected native test source root is unavailable");
        var exampleRoot = Path.Combine(nativeSourceRoot, "resources/model-to-action/recipes");
        var exampleRecipe = JsonNode.Parse(File.ReadAllBytes(Path.Combine(exampleRoot, "cast-vfx-v2.example.json")))!.AsObject();
        Check(ActionCompilerV2.Sources(exampleRecipe).Count == 3, "illustrative recipe template passes strict typed three-source parsing without resource acceptance");
        Check(EffectProfileV2.Parse(File.ReadAllBytes(Path.Combine(exampleRoot, "cast-vfx-v2.profile.example.json"))).Layers.Count == 4,
            "illustrative production profile template passes strict four-layer parsing without resource acceptance");
        var cue = plan["actions"]![0]!["cues"]![0]!;
        Check(plan["schemaVersion"]!.GetValue<string>() == "hephaera-action-package/v2", "explicit v2 produces an independently versioned action package");
        Check(catalog.Sources.Single().SourceInventory.Files.Count == 9, "native inspection includes all nine private source-closure files");
        Check(catalog.Sources.Single().RuntimeSource.Resources.Count == 2 && catalog.Sources.Single().RuntimeSource.Correspondences.Count == 2,
            "owned PNG and actual triangle GLB each retain canonical decoded correspondence");
        Check(cue["layers"]!.AsArray().Select(n => n!["family"]!.GetValue<string>()).ToHashSet().SetEquals(OwnedEffectFixture.Families),
            "compiled action retains all nine declared production layer families");
        Check(cue["start"]!.GetValue<double>() == .5 && cue["end"]!.GetValue<double>() == 1.75,
            "reviewed release marker and second source occurrence give independently expected cue times");
        var first = cue["layers"]![0]!;
        var last = cue["layers"]![8]!;
        Check(first["start"]!.GetValue<double>() == .5 && first["stopAt"]!.GetValue<double>() == .75 && first["retireAt"]!.GetValue<double>() == 1,
            "first layer preserves authored quarter-second emission and bounded tail");
        Check(last["start"]!.GetValue<double>() == 1 && last["stopAt"]!.GetValue<double>() == 1.25 && last["retireAt"]!.GetValue<double>() == 1.5,
            "last layer preserves its half-second offset instead of stretching to cue stop");
        var runtimeSource = plan["effectSources"]![0]!.ToJsonString();
        Check(!runtimeSource.Contains("private/", StringComparison.Ordinal) && !runtimeSource.Contains(OwnedEffectFixture.PrivateMarker, StringComparison.Ordinal)
            && !runtimeSource.Contains("\"rights\":", StringComparison.Ordinal) && !runtimeSource.Contains("\"productionReview\":", StringComparison.Ordinal),
            "runtime source exposes ancestry hashes without private rights/review/evidence payloads");
        Check(catalog.Sources.Single().SourceInventory.Files.Any(f => f.Role == "production-evidence" && f.Path.StartsWith("private/", StringComparison.Ordinal)),
            "private evidence remains in the source closure rather than being discarded");

        var vectorContext = new ActionEffectInspectionContext(OwnedEffectFixture.EngineIdentity(), new string('a', 64), new string('b', 64), new());
        ActionEffectFile[] vector = [new() { Role = "resource", SourceId = "effect", ResourceId = "spark", Path = "inputs/owned/spark.png", Bytes = 4, Sha256 = new string('c', 64) }];
        // Independent SHA-256 of the documented camel-case JSON vector, calculated
        // outside the production helper. This is a fixed protocol expectation.
        Check(ActionEffectDependencies.SourceFingerprint(vector, vectorContext) == "475897737b68f0b9688c4e2dd418ad21ecd0a134c7d7bc81863639309c8e30c2",
            "source fingerprint matches the independent fixed canonical JSON hash vector");
        Check(OwnedEffectFixture.HashBytes(OwnedEffectFixture.Rgba) == "c88af8be35eb83852f0ecdd33f1335ed9bb4fd417558c2d4692941fb3865a266",
            "owned PNG correspondence uses independently expected decoded RGBA bytes");
        Check(ActionEffectDependencies.SourceFingerprint(vector, vectorContext with { RuntimeBuildSha256 = new string('d', 64) })
            != ActionEffectDependencies.SourceFingerprint(vector, vectorContext), "native build drift changes private source fingerprint");
        Check(ActionEffectDependencies.SourceFingerprint([vector[0] with { Sha256 = new string('e', 64) }], vectorContext)
            != ActionEffectDependencies.SourceFingerprint(vector, vectorContext), "resource byte drift changes private source fingerprint");
        Check(ActionEffectDependencies.SourceFingerprint(vector, vectorContext with { Engine = new JsonObject { ["effectiveEngine"] = "UNITY" } })
            != ActionEffectDependencies.SourceFingerprint(vector, vectorContext), "engine identity drift changes private source fingerprint");
        Check(ActionEffectDependencies.SourceFingerprint([vector[0] with { SourceId = "another-action-alias" }], vectorContext)
            == ActionEffectDependencies.SourceFingerprint(vector, vectorContext), "action source aliases do not transplant or invalidate whole-effect review identity");
        Check(ActionEffectDependencies.SourceFingerprint(vector, vectorContext, "cast") != ActionEffectDependencies.SourceFingerprint(vector, vectorContext, "impact"),
            "selecting a different native effect invalidates the exact reviewed source subject");

        var oldRecipe = Fixture.Recipe();
        var oldPlan = ActionCompiler.Compile(oldRecipe, fixture.Motion, "legacy", "v1");
        Check(oldPlan["schemaVersion"]!.GetValue<string>() == "hephaera-action-package/v1"
            && oldPlan["actions"]![0]!["duration"]!.GetValue<double>() == 2.25
            && oldPlan["actions"]![0]!["cues"]![0]!["type"]!.GetValue<string>() == "projectile",
            "unchanged v1 compiler keeps original version, duration and legacy cue kind");
        Reject(() => ActionCompiler.Compile(oldRecipe, fixture.Motion, "legacy", "v1", catalog), "v1 cannot inherit a v2 inspected catalog");
        var transplantedV1 = oldRecipe.DeepClone().AsObject(); transplantedV1["effectSources"] = recipe["effectSources"]!.DeepClone();
        Reject(() => ActionEffectDependencies.ReadSources(transplantedV1), "v1 declaration cannot acquire effect source semantics");
        Reject(() => ActionCompiler.Compile(recipe, fixture.Motion, "owned", "v2"), "v2 cannot compile without current native inspection");
        foreach (var profile in new[] { "skinned", "articulated", "rigid" })
        {
            var motion = fixture.Motion.DeepClone().AsObject(); motion["profile"] = profile;
            if (profile == "skinned") motion["rig"]!["rootBone"] = "Root";
            Check(Compile(motion: motion)["profile"]!.GetValue<string>() == profile, "v2 preserves supported " + profile + " source profile");
        }
        var mechanical = fixture.Motion.DeepClone().AsObject(); mechanical["profile"] = "mechanical-assembly";
        Reject(() => Compile(motion: mechanical), "effect extension does not admit mixed mechanical-assembly actions");

        void RecipeReject(string name, Action<JsonObject> mutation)
        {
            var changed = recipe.DeepClone().AsObject(); mutation(changed);
            Reject(() => Compile(changed), name);
        }
        foreach (var (name, mutation) in new (string, Action<JsonObject>)[]
        {
            ("unknown top-level executable field", r => r["python"] = "never executed"),
            ("unknown action cue field", r => r["actions"]![0]!["cues"]![0]!["damage"] = 1),
            ("legacy cue cannot be relabeled as production effect", r => r["actions"]![0]!["cues"]![0]!["type"] = "projectile"),
            ("unknown effect source", r => r["actions"]![0]!["cues"]![0]!["source"] = "missing"),
            ("unknown actual source anchor", r => r["actions"]![0]!["cues"]![0]!["anchor"] = "guessed.hand"),
            ("missing actual source marker", r => r["actions"]![0]!["cues"]![0]!["start"]!["marker"] = "guessed-release"),
            ("ambiguous source address", r => r["actions"]![0]!["cues"]![0]!["start"]!["offset"] = .5),
            ("missing source occurrence", r => r["actions"]![0]!["cues"]![0]!["start"]!["occurrence"] = 3),
            ("cue stop cannot truncate authored layer duration", r => r["actions"]![0]!["cues"]![0]!["end"] = OwnedEffectFixture.Address(0, .7)),
            ("cue start cannot equal its stop", r => r["actions"]![0]!["cues"]![0]!["end"] = OwnedEffectFixture.Address(0, .5)),
            ("unsupported transform unit", r => r["actions"]![0]!["cues"]![0]!["transform"]!["units"] = "centimeters"),
            ("unsupported transform basis", r => r["actions"]![0]!["cues"]![0]!["transform"]!["up"] = "+Z"),
            ("mirrored parent policy", r => r["actions"]![0]!["cues"]![0]!["transform"]!["parentScale"] = "mirrored"),
            ("nonuniform parent policy", r => r["actions"]![0]!["cues"]![0]!["transform"]!["parentScale"] = "nonuniform"),
            ("unnormalized xyzw quaternion", r => r["actions"]![0]!["cues"]![0]!["transform"]!["orientation"] = new JsonArray(0, 0, 0, .5)),
            ("negative uniform scale", r => r["actions"]![0]!["cues"]![0]!["transform"]!["scale"] = -1),
            ("nonfinite local transform", r => r["actions"]![0]!["cues"]![0]!["transform"]!["position"] = JsonNode.Parse("[1e309,0,0]")),
            ("unknown transform field", r => r["actions"]![0]!["cues"]![0]!["transform"]!["matrix"] = "unbounded"),
            ("unknown target policy", r => r["targets"]![0]!["mode"] = "homing"),
            ("missing target definition", r => r["targets"] = new JsonArray()),
            ("missing review target point", r => r["review"]!["targets"] = new JsonObject()),
            ("unknown placement", r => r["actions"]![0]!["cues"]![0]!["placement"] = "homing"),
            ("beam cannot lose explicit placement", r => r["actions"]![0]!["cues"]![0]!["placement"] = "anchor-follow"),
            ("unbounded tail", r => r["actions"]![0]!["cues"]![0]!["lifetime"]!["tailSeconds"] = 2),
            ("unknown pause policy", r => r["actions"]![0]!["cues"]![0]!["lifetime"]!["pause"] = "global-pause"),
            ("tail cannot survive generation cancellation", r => r["actions"]![0]!["cues"]![0]!["lifetime"]!["cancel"] = "keep-alive"),
            ("trail cannot retain discontinuous history", r => r["actions"]![0]!["cues"]![0]!["trail"]!["reset"] = "never"),
            ("trail retained samples exceed actual layer history", r => r["actions"]![0]!["cues"]![0]!["trail"]!["maxSamples"] = 9),
            ("unknown finite budget", r => r["budgets"]!["maxUnboundedShaderBytes"] = 1),
            ("changed source package hash cannot reuse catalog", r => r["effectSources"]![0]!["package"]!["sha256"] = new string('f', 64)),
            ("changed source profile hash cannot reuse catalog", r => r["effectSources"]![0]!["profile"]!["sha256"] = new string('f', 64)),
            ("unknown source callback rejected by strict DTO", r => r["effectSources"]![0]!["callback"] = "never executed"),
            ("unknown source profile discriminator", r => r["effectSources"]![0]!["profile"]!["schemaVersion"] = "unknown/v1"),
            ("unknown source correspondence field", r => r["effectSources"]![0]!["correspondences"]![0]!["shader"] = "never executed"),
            ("source path traversal", r => r["effectSources"]![0]!["package"]!["path"] = "../outside.json")
        }) RecipeReject(name, mutation);
        var extracted = fixture.Motion.DeepClone().AsObject(); extracted["clips"]![0]!["rootMotion"] = "extract";
        Reject(() => Compile(motion: extracted), "source in-place restriction survives v2 effects");
        var ambiguous = fixture.Motion.DeepClone().AsObject();
        ambiguous["clips"]![0]!["markers"]!.AsArray().Add(new JsonObject { ["event"] = "release", ["time"] = .75 });
        Reject(() => Compile(motion: ambiguous), "duplicate source release markers do not silently select one");

        var continuous = recipe.DeepClone().AsObject();
        continuous["targets"]![0] = JsonNode.Parse("""{"id":"point","mode":"continuous-world-point","maxDistance":100,"maxAgeSeconds":0.1,"maxUpdatesPerSecond":60,"clock":"presentation-seconds","generation":"match-action","sampleAt":"interval-end-before-fx","onInvalid":"retire-effect"}""");
        Check(Compile(continuous)["targets"]![0]!["mode"]!.GetValue<string>() == "continuous-world-point",
            "beam declares bounded generation-bound endpoint freshness and sampling explicitly");
        foreach (var key in new[] { "maxAgeSeconds", "maxUpdatesPerSecond", "clock", "generation", "sampleAt", "onInvalid" })
        {
            var changed = continuous.DeepClone().AsObject(); changed["targets"]![0]!.AsObject().Remove(key);
            Reject(() => Compile(changed), "continuous beam cannot omit " + key);
        }
        var stalePolicy = continuous.DeepClone().AsObject(); stalePolicy["targets"]![0]!["maxAgeSeconds"] = 2;
        Reject(() => Compile(stalePolicy), "continuous target freshness has a finite one-second ceiling");
        var projectileContinuous = continuous.DeepClone().AsObject(); projectileContinuous["actions"]![0]!["cues"]![0]!["placement"] = "fixed-target-projectile";
        Reject(() => Compile(projectileContinuous), "continuous endpoints cannot implicitly create a homing projectile");
        var reduced = recipe.DeepClone().AsObject();
        reduced["actions"]![0]!["cues"]![0]!["overrides"] = JsonNode.Parse("""[{"layer":"sprite","sizeMultiplier":0.5,"colorMultiplier":[1,0.5,0.5,0.5],"emissionMultiplier":0.25}]""");
        Check(Compile(reduced)["actions"]![0]!["cues"]![0]!["overrides"]![0]!["sizeMultiplier"]!.GetValue<double>() == .5,
            "typed overrides preserve bounded authored reductions");
        foreach (var (label, value) in new[] { ("sizeMultiplier", 1.01), ("emissionMultiplier", 1.01), ("emissionMultiplier", -.1) })
        {
            var changed = reduced.DeepClone().AsObject(); changed["actions"]![0]!["cues"]![0]!["overrides"]![0]![label] = value;
            Reject(() => Compile(changed), "override cannot amplify or negate " + label + "=" + value);
        }
        RecipeReject("unknown override layer", r => r["actions"]![0]!["cues"]![0]!["overrides"] = JsonNode.Parse("""[{"layer":"missing","sizeMultiplier":0.5}]"""));
        RecipeReject("executable override string", r => r["actions"]![0]!["cues"]![0]!["overrides"] = JsonNode.Parse("""[{"layer":"sprite","shader":"never executed"}]"""));
        RecipeReject("duplicate overrides cannot compound amplification", r => r["actions"]![0]!["cues"]![0]!["overrides"] = JsonNode.Parse("""[{"layer":"sprite","sizeMultiplier":0.5},{"layer":"sprite","sizeMultiplier":0.5}]"""));
        var draining = recipe.DeepClone().AsObject();
        draining["actions"]![0]!["steps"]![0]!["repeat"] = 1;
        draining["actions"]![0]!["cues"]![0]!["start"] = OwnedEffectFixture.Address(0, 0);
        draining["actions"]![0]!["cues"]![0]!["end"] = OwnedEffectFixture.Address(0, 1);
        draining["actions"]![0]!["cues"]![0]!["lifetime"]!["tailSeconds"] = .75;
        var drained = Compile(draining);
        Check(drained["actions"]![0]!["duration"]!.GetValue<double>() == 1 && drained["actions"]![0]!["retireAt"]!.GetValue<double>() == 1.25,
            "normal completion may drain after motion while each layer caps requested tail to its authored half-second maximum");
        var doubled = recipe.DeepClone().AsObject();
        var duplicateCue = doubled["actions"]![0]!["cues"]![0]!.DeepClone(); duplicateCue["id"] = "second";
        doubled["actions"]![0]!["cues"]!.AsArray().Add(duplicateCue); doubled["budgets"]!["maxParticles"] = 17;
        Reject(() => Compile(doubled), "aggregate capacities count both layered cues and retained tails");

        var native = catalog.Sources.Single().Package;
        var profileNode = JsonSerializer.SerializeToNode(catalog.Sources.Single().Profile, EffectProfileV2.Json)!.AsObject();
        foreach (var family in OwnedEffectFixture.Families)
        {
            var changed = profileNode.DeepClone().AsObject();
            var layers = changed["layers"]!.AsArray();
            layers.Remove(layers.Single(layer => layer!["family"]!.GetValue<string>() == family));
            Reject(() => EffectProfileV2.Validate(EffectProfileV2.Parse(Encoding.UTF8.GetBytes(changed.ToJsonString())), native, "cast"),
                "production profile cannot omit native " + family + " layer");
        }
        foreach (var (name, mutation) in new (string, Action<JsonObject>)[]
        {
            ("unknown shader field", p => p["layers"]![0]!["shader"] = "never executed"),
            ("duplicate layer identity", p => p["layers"]!.AsArray().Add(p["layers"]![0]!.DeepClone())),
            ("wrong native family", p => p["layers"]![0]!["family"] = "beam"),
            ("unknown layer space", p => p["layers"]![0]!["space"] = "implicit"),
            ("mixed burst/rate emission", p => p["layers"]![0]!["emission"]!["rate"] = 4),
            ("native duration cannot contain excessive profile tail", p => p["layers"]![0]!["maxTailSeconds"] = .75),
            ("missing actual mesh binding", p => p["layers"]![2]!.AsObject().Remove("geometry")),
            ("trail cannot invent history", p => p["layers"]![5]!["trail"]!["historyPoints"] = 9),
            ("beam cannot borrow texture silently", p => p["layers"]![8]!["resourceId"] = "spark")
        })
        {
            var changed = profileNode.DeepClone().AsObject(); mutation(changed);
            Reject(() => EffectProfileV2.Validate(EffectProfileV2.Parse(Encoding.UTF8.GetBytes(changed.ToJsonString())), native, "cast"), name);
        }
        var rawProfile = profileNode.ToJsonString();
        Reject(() => EffectProfileV2.Parse(Encoding.UTF8.GetBytes(rawProfile.Insert(1, "\"schemaVersion\":\"hephaera-three-vfx-profile/v1\","))),
            "duplicate profile JSON fields rejected before typed deserialization");
        var nullProfile = profileNode.DeepClone().AsObject(); nullProfile["layers"]![0]!["geometry"] = null;
        Reject(() => EffectProfileV2.Parse(Encoding.UTF8.GetBytes(nullProfile.ToJsonString())), "explicit null nested profile fields do not bypass admission");

        foreach (var correspondence in fixture.Source.Correspondences)
        {
            var missing = fixture.Source with { Correspondences = fixture.Source.Correspondences.Where(c => c.ResourceId != correspondence.ResourceId).ToArray() };
            Reject(() => fixture.InspectSource(missing), "complete correspondence is required for resource " + correspondence.ResourceId);
            var resource = catalog.Sources.Single().RuntimeSource.Resources.Single(r => r.Id == correspondence.ResourceId);
            var report = EffectProfileV2.ParseData<ActionEffectCorrespondenceReport>(File.ReadAllBytes(fixture.Full(correspondence.Report.Path)));
            Reject(() => ActionEffectDependencies.ValidateCorrespondence(correspondence with { DecodedSha256 = new string('f', 64) }, resource, report),
                "decoded correspondence drift rejected for " + resource.Id);
            Reject(() => ActionEffectDependencies.ValidateCorrespondence(correspondence, resource, report with { Independent = false }),
                "non-independent correspondence rejected for " + resource.Id);
            Reject(() => ActionEffectDependencies.ValidateCorrespondence(correspondence, resource, report with { SourceEvidenceSha256 = "invalid" }),
                "malformed private evidence identity rejected for " + resource.Id);
        }
        var unknownCorrespondence = fixture.Source with { Correspondences = fixture.Source.Correspondences.Append(fixture.Source.Correspondences[0] with { ResourceId = "extra" }).ToArray() };
        Reject(() => fixture.InspectSource(unknownCorrespondence), "unused extra correspondence cannot enter source closure");
        foreach (var reference in catalog.Sources.Single().SourceInventory.Files)
        {
            var path = fixture.Full(reference.Path); var bytes = File.ReadAllBytes(path);
            try { File.AppendAllText(path, " "); Reject(() => fixture.InspectSource(fixture.Source), "changed source bytes invalidate " + reference.Path); }
            finally { File.WriteAllBytes(path, bytes); }
        }
        void RehashedReviewReject(string field, JsonNode value, string name)
        {
            var path = fixture.Full(fixture.Source.ProductionReview.Path); var original = File.ReadAllBytes(path);
            try
            {
                var review = JsonNode.Parse(original)!.AsObject(); review[field] = value;
                File.WriteAllText(path, review.ToJsonString());
                var source = fixture.Source with { ProductionReview = fixture.Source.ProductionReview with { Sha256 = Workspace.Hash(path) } };
                Reject(() => fixture.InspectSource(source), name, "production review");
            }
            finally { File.WriteAllBytes(path, original); }
        }
        RehashedReviewReject("decision", JsonValue.Create("pending"), "freshly hashed pending source review cannot create an inspected catalog");
        RehashedReviewReject("independent", JsonValue.Create(false), "freshly hashed non-independent source review cannot create an inspected catalog");
        RehashedReviewReject("reviewer", JsonValue.Create("OWNED-FIXTURE-AUTHOR"), "case changes cannot turn the source author into an independent reviewer");
        var rightsFile = fixture.Full(fixture.Source.Rights.Path); var originalRights = File.ReadAllBytes(rightsFile);
        try
        {
            var rights = JsonNode.Parse(originalRights)!.AsObject(); rights["authorized"] = false;
            File.WriteAllText(rightsFile, rights.ToJsonString());
            var denied = fixture.Source with { Rights = fixture.Source.Rights with { Sha256 = Workspace.Hash(rightsFile) } };
            Reject(() => fixture.InspectSource(denied), "freshly hashed denied rights cannot create an inspected catalog", "rights assertions");
        }
        finally { File.WriteAllBytes(rightsFile, originalRights); }
        var draft = JsonNode.Parse(File.ReadAllBytes(fixture.Full(fixture.Source.ProductionReview.Path)))!.AsObject();
        var reviewedSubject = draft["subjectSha256"]!.GetValue<string>();
        draft["decision"] = "pending"; draft["independent"] = false; draft["reviewer"] = "";
        draft["subjectSha256"] = new string('0', 64);
        var draftPath = "inputs/fx/draft-review.json";
        File.WriteAllText(fixture.Full(draftPath), draft.ToJsonString());
        var draftSource = fixture.Source with { ProductionReview = new() { Path = draftPath, Sha256 = Workspace.Hash(fixture.Full(draftPath)) } };
        var beforeDraft = Workspace.Hash(fixture.Full(draftPath));
        var subject = new EffectDependencyInspector().ReviewSubject(fixture.Workspace, draftSource, fixture.Context, default);
        Check(!subject.Accepted && subject.SubjectSha256 == reviewedSubject && subject.Inventory.Count == 9,
            "read-only draft review returns the exact complete subject with acceptance explicitly false");
        Reject(() => fixture.InspectSource(draftSource), "subject discovery does not approve a pending source review", "production review");
        Check(Workspace.Hash(fixture.Full(draftPath)) == beforeDraft && draft["decision"]!.GetValue<string>() == "pending",
            "review-subject discovery preserves the caller's pending review bytes");
        foreach (var field in new[] { "contractSha256", "definitionSha256", "registrySha256" })
        {
            var engine = fixture.Context.Engine.DeepClone().AsObject(); engine[field] = new string('e', 64);
            Reject(() => fixture.InspectSource(fixture.Source, fixture.Context with { Engine = engine }), "native effect rejects changed engine " + field);
        }
        Reject(() => fixture.InspectSource(fixture.Source, fixture.Context with { StyleSha256 = new string('e', 64) }), "native effect rejects changed style identity");
        Reject(() => fixture.InspectSource(fixture.Source, fixture.Context with { RuntimeBuildSha256 = new string('e', 64) }), "production review cannot transplant across native build identity");
        Reject(() => fixture.InspectSource(fixture.Source, cancellation: new CancellationToken(true)), "cancelled inspection has no passing closure");
        foreach (var (field, limit) in new[] { ("maxEffectFiles", 8L), ("maxEffectDependencyBytes", 1L), ("maxEffectJsonBytes", 1L) })
        {
            var budgets = fixture.Context.Budgets.DeepClone().AsObject(); budgets[field] = JsonNode.Parse(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Reject(() => fixture.InspectSource(fixture.Source, fixture.Context with { Budgets = budgets }), "source admission enforces remaining aggregate " + field + " before reads");
        }

        var second = new OwnedEffectFixture(root, "other");
        var combined = recipe.DeepClone().AsObject();
        combined["effectSources"]!.AsArray().Add(JsonSerializer.SerializeToNode(second.Source, EffectProfileV2.Json));
        var secondCue = combined["actions"]![0]!["cues"]![0]!.DeepClone(); secondCue["id"] = "other-cue"; secondCue["source"] = "other";
        combined["actions"]![0]!["cues"]!.AsArray().Add(secondCue);
        var combinedCatalog = fixture.Inspect(combined);
        Check(ActionCompiler.Compile(combined, fixture.Motion, "combined", "v2", combinedCatalog)["effectSources"]!.AsArray().Count == 2,
            "multiple distinct owned effect sources compile under one aggregate contract");
        var exact = combined.DeepClone().AsObject();
        exact["budgets"]!["maxEffectSources"] = 2;
        exact["budgets"]!["maxEffectFiles"] = combinedCatalog.Sources.Sum(s => s.SourceInventory.Files.Count);
        exact["budgets"]!["maxEffectDependencyBytes"] = combinedCatalog.Sources.Sum(s => s.SourceInventory.Files.Sum(f => f.Bytes));
        exact["budgets"]!["maxEffectJsonBytes"] = combinedCatalog.Sources.Sum(s => s.SourceInventory.Files.Where(f => f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Bytes));
        Check(fixture.Inspect(exact).Sources.Count == 2, "exact complete aggregate file/byte/JSON limits admit both sources without hidden slack");
        foreach (var (field, limit) in new[]
        {
            ("maxEffectSources", 1L),
            ("maxEffectFiles", combinedCatalog.Sources.Sum(s => (long)s.SourceInventory.Files.Count) - 1),
            ("maxEffectDependencyBytes", combinedCatalog.Sources.Sum(s => s.SourceInventory.Files.Sum(f => f.Bytes)) - 1),
            ("maxEffectJsonBytes", combinedCatalog.Sources.Sum(s => s.SourceInventory.Files.Where(f => f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Bytes)) - 1)
        })
        {
            var changed = combined.DeepClone().AsObject(); changed["budgets"]![field] = JsonNode.Parse(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Reject(() => fixture.Inspect(changed), "multiple source closures share finite " + field + " allowance");
            Reject(() => ActionCompiler.Compile(changed, fixture.Motion, "combined", "v2", combinedCatalog), "compiler independently enforces aggregate " + field);
        }

        var exportRoot = ".art-staging/action-v2/owned/export";
        var stage = new Stage();
        var portable = Compile(); portable["engine"] = fixture.Context.Engine.DeepClone();
        portable["effectSources"] = ActionEffectDependencies.Export(fixture.Workspace, catalog, exportRoot, stage, default);
        portable["effectClosureSha256"] = ActionEffectDependencies.RuntimeClosureFingerprint(portable["effectSources"]!.AsArray());
        var modelSource = fixture.Source.Package.Path[..fixture.Source.Package.Path.LastIndexOf('/')] + "/triangle.glb";
        var deliveredModel = exportRoot + "/model.glb";
        File.Copy(fixture.Full(modelSource), fixture.Full(deliveredModel));
        portable["model"] = new JsonObject { ["path"] = deliveredModel, ["sha256"] = Workspace.Hash(fixture.Full(deliveredModel)) };
        ActionEffectDependencies.ValidateRuntimeClosure(fixture.Workspace, portable);
        Check(stage.Outputs.Count == 6, "portable closure contains exactly package, profile, two resources and two canonical reports");
        var exportedFiles = Directory.EnumerateFiles(fixture.Full(exportRoot), "*", SearchOption.AllDirectories).ToArray();
        Check(exportedFiles.Length == 7 && exportedFiles.All(p => !Encoding.UTF8.GetString(File.ReadAllBytes(p)).Contains(OwnedEffectFixture.PrivateMarker, StringComparison.Ordinal)),
            "portable closure carries no private source evidence or rights/review bytes");
        var partialRuntimeBudget = portable.DeepClone().AsObject();
        partialRuntimeBudget["budgets"]!["maxRuntimeBytes"] = JsonNode.Parse(exportedFiles.Sum(path => new FileInfo(path).Length).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Reject(() => ActionEffectDependencies.ValidateRuntimeClosure(fixture.Workspace, partialRuntimeBudget),
            "maxRuntimeBytes includes delivered model, complete effects and outer action descriptor", "maxRuntimeBytes");
        foreach (var resource in catalog.Sources.Single().RuntimeSource.Resources)
        {
            var row = portable["effectSources"]![0]!["resources"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == resource.Id)!;
            Check(Workspace.Hash(fixture.Full(row["path"]!.GetValue<string>())) == resource.Sha256, "portable copy preserves actual " + resource.Kind + " bytes");
        }
        var stray = fixture.Full(exportRoot + "/effects/fx/unowned.txt");
        File.WriteAllText(stray, "OWNED NEGATIVE TEST; not an admitted runtime dependency");
        try { Reject(() => ActionEffectDependencies.ValidateRuntimeClosure(fixture.Workspace, portable), "undeclared runtime file cannot hide beside portable resources"); }
        finally { File.Delete(stray); }
        foreach (var key in new[] { "resources", "correspondences" })
        {
            var changed = portable.DeepClone().AsObject(); changed["effectSources"]![0]![key]!.AsArray().RemoveAt(0);
            changed["effectClosureSha256"] = ActionEffectDependencies.RuntimeClosureFingerprint(changed["effectSources"]!.AsArray());
            Reject(() => ActionEffectDependencies.ValidateRuntimeClosure(fixture.Workspace, changed), "fresh hash cannot conceal partial portable " + key);
        }
        var tamperedProfile = fixture.Full(portable["effectSources"]![0]!["profile"]!["path"]!.GetValue<string>());
        var originalProfile = File.ReadAllBytes(tamperedProfile);
        try { File.AppendAllText(tamperedProfile, " "); Reject(() => ActionEffectDependencies.ValidateRuntimeClosure(fixture.Workspace, portable), "changed portable production profile invalidates closure"); }
        finally { File.WriteAllBytes(tamperedProfile, originalProfile); }
        var receiving = new SyntheticReceivingFixture(fixture, portable, "fixed");
        receiving.Validate();
        Check(true, "complete SYNTHETIC receiving packet exercises protocol admission without a browser or authentic acceptance");
        void ReceivingReject(string name, Action<JsonObject> mutation)
        {
            var changed = receiving.Packet.DeepClone().AsObject(); mutation(changed);
            Reject(() => receiving.Validate(packet: changed), "SYNTHETIC receiving rejection: " + name);
        }
        foreach (var (name, mutation) in new (string, Action<JsonObject>)[]
        {
            ("motion/proxy flag", p => p["actualEngine"] = false),
            ("technical-proxy evidence kind", p => p["evidenceKind"] = "technical-proxies-only"),
            ("wrong engine identity", p => p["engine"]!["definitionSha256"] = new string('e', 64)),
            ("wrong native build", p => p["runtimeBuildSha256"] = new string('e', 64)),
            ("wrong model", p => p["modelSha256"] = new string('e', 64)),
            ("wrong action package", p => p["packageSha256"] = new string('e', 64)),
            ("wrong complete effect closure", p => p["effectClosureSha256"] = new string('e', 64)),
            ("omitted engine input", p => p["engineInputs"]!.AsArray().RemoveAt(0)),
            ("omitted source assertion", p => p["effectSources"]!.AsArray().Clear()),
            ("transplanted source production review", p => p["effectSources"]![0]!["productionReviewSha256"] = new string('e', 64)),
            ("transplanted profile", p => p["effectSources"]![0]!["profileSha256"] = new string('e', 64)),
            ("omitted decoded resource", p => p["resourceBindings"]!.AsArray().RemoveAt(0)),
            ("changed decoded resource hash", p => p["resourceBindings"]![0]!["decodedSha256"] = new string('e', 64)),
            ("changed raw resource hash", p => p["resourceBindings"]![0]!["resourceSha256"] = new string('e', 64)),
            ("changed canonical report hash", p => p["resourceBindings"]![0]!["correspondenceSha256"] = new string('e', 64)),
            ("changed private correspondence evidence hash", p => p["resourceBindings"]![0]!["sourceEvidenceSha256"] = new string('e', 64)),
            ("omitted complete action", p => p["coverage"]!.AsArray().Clear()),
            ("truncated full range", p => p["coverage"]![0]!["end"] = 1.75),
            ("omitted regular capture cadence", p => p["coverage"]![0]!.AsObject().Remove("fps")),
            ("sparse regular-grid capture", p => p["coverage"]![0]!["samples"]!.AsArray().RemoveAt(1)),
            ("nearest frame replaces exact semantic boundary", p => p["coverage"]![0]!["samples"]![5]!["time"] = .5626),
            ("duplicate capture timestamp", p => p["coverage"]![0]!["samples"]![5]!["time"] = .5),
            ("invented peak occupancy evidence", p => { foreach (var sample in p["coverage"]![0]!["samples"]!.AsArray()) sample!["maximumOccupancy"] = false; }),
            ("owned effect leak after final drain", p => p["coverage"]![0]!["cleanup"]!["ownedEffects"] = 1),
            ("resource lease leak after final drain", p => p["coverage"]![0]!["cleanup"]!["resourceLeases"] = 1),
            ("rig write leak after final drain", p => p["coverage"]![0]!["cleanup"]!["rigWrites"] = 1),
            ("missing isolated layer family", p => p["layerCoverage"]!.AsArray().RemoveAt(0)),
            ("wrong isolated layer family", p => p["layerCoverage"]![0]!["family"] = "beam"),
            ("missing combined-action correspondence", p => p["layerCoverage"]![0]!["combinedSha256"] = new string('e', 64)),
            ("dark/light capture role alias", p => p["layerCoverage"]![0]!["light"] = p["layerCoverage"]![0]!["dark"]!.DeepClone()),
            ("missing concurrent actors and ambient test", p => p["tests"]!.AsArray().Remove(p["tests"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == "concurrent-actors-and-ambient"))),
            ("missing fixed target snapshot test", p => p["tests"]!.AsArray().Remove(p["tests"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == "fixed-target-snapshot"))),
            ("missing trail discontinuity test", p => p["tests"]!.AsArray().Remove(p["tests"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == "trail-discontinuity-reset"))),
            ("failed lifecycle test", p => p["tests"]![0]!["status"] = "failed"),
            ("stale lifecycle test artifact", p => p["tests"]![0]!["evidenceSha256"] = new string('e', 64)),
            ("too few baseline performance runs", p => p["performance"]!["baselineRuns"] = 4),
            ("too few candidate performance runs", p => p["performance"]!["candidateRuns"] = 4),
            ("single-actor performance scope", p => p["performance"]!["maxConcurrentActors"] = 1),
            ("invented GPU measurement category", p => p["performance"]!["measurement"] = "estimated-gpu"),
            ("stale performance artifact", p => p["performance"]!["artifactSha256"] = new string('e', 64))
        }) ReceivingReject(name, mutation);
        foreach (var field in new[] { "engine", "modelSha256", "packageSha256", "runtimeBuildSha256", "effectClosureSha256" })
        {
            var changed = receiving.Envelope.DeepClone().AsObject();
            changed["runtime"]![field] = field == "engine" ? "UNITY" : new string('e', 64);
            Reject(() => receiving.Validate(envelope: changed), "SYNTHETIC receiving outer assertion rejects stale " + field);
        }
        var missingRuntime = receiving.Envelope.DeepClone().AsObject(); missingRuntime.Remove("runtime");
        Reject(() => receiving.Validate(envelope: missingRuntime), "SYNTHETIC motion-only review cannot omit the v2 runtime envelope");
        var roleAlias = receiving.Envelope.DeepClone().AsObject();
        roleAlias["runtime"]!["evidence"]![1]!["path"] = roleAlias["runtime"]!["evidence"]![0]!["path"]!.DeepClone();
        Reject(() => receiving.Validate(envelope: roleAlias), "SYNTHETIC receiving evidence roles cannot alias one artifact");
        foreach (var input in receiving.Packet["engineInputs"]!.AsArray())
        {
            var path = fixture.Full(input!["path"]!.GetValue<string>()); var bytes = File.ReadAllBytes(path);
            try { File.AppendAllText(path, "changed"); Reject(() => receiving.Validate(), "SYNTHETIC transitive receiving input drift: " + input["role"]!.GetValue<string>()); }
            finally { File.WriteAllBytes(path, bytes); }
        }
        var samplePath = fixture.Full(receiving.Packet["coverage"]![0]!["samples"]![0]!["artifact"]!["path"]!.GetValue<string>());
        var originalSample = File.ReadAllBytes(samplePath);
        try { File.AppendAllText(samplePath, "changed"); Reject(() => receiving.Validate(), "SYNTHETIC capture byte drift invalidates receiving evidence"); }
        finally { File.WriteAllBytes(samplePath, originalSample); }
        var continuousPackage = portable.DeepClone().AsObject(); continuousPackage["targets"]![0]!["mode"] = "continuous-world-point";
        var continuousReceiving = new SyntheticReceivingFixture(fixture, continuousPackage, "continuous");
        continuousReceiving.Validate();
        Check(true, "SYNTHETIC continuous beam protocol requires its explicit freshness and invalid-input tests");
        foreach (var name in new[] { "continuous-target-freshness", "invalid-target-retirement" })
        {
            var changed = continuousReceiving.Packet.DeepClone().AsObject();
            changed["tests"]!.AsArray().Remove(changed["tests"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == name));
            Reject(() => continuousReceiving.Validate(packet: changed), "SYNTHETIC continuous beam cannot omit " + name);
        }
        var undersizedCaptureBudget = portable.DeepClone().AsObject(); undersizedCaptureBudget["budgets"]!["maxReviewFrames"] = 51;
        var boundedReceiving = new SyntheticReceivingFixture(fixture, undersizedCaptureBudget, "bounded");
        Reject(() => boundedReceiving.Validate(), "SYNTHETIC whole-action plus isolated layer captures share one finite frame allowance");
        var pending = "assets/models/owned-effects.pending-fixture";
        var destination = "assets/models/owned-effects";
        var rebound = portable.DeepClone().AsObject(); var before = rebound["effectClosureSha256"]!.GetValue<string>();
        Directory.CreateDirectory(fixture.Full(pending));
        File.Copy(fixture.Full(deliveredModel), fixture.Full(pending + "/model.glb"));
        var copied = ActionEffectDependencies.CopyAndRebind(fixture.Workspace, rebound, pending, destination, default);
        Check(copied.Count == 6 && rebound["effectClosureSha256"]!.GetValue<string>() == before,
            "fixture-only copy rebinds every closure file while preserving root-independent fingerprint");
        Check(rebound["effectSources"]![0]!["package"]!["path"]!.GetValue<string>().StartsWith(destination + "/", StringComparison.Ordinal)
            && !Directory.Exists(fixture.Full(destination)), "copy helper binds final logical paths without publishing a completed destination");
        ActionEffectDependencies.ValidateRuntimeClosure(fixture.Workspace, rebound, pending);
        Reject(() => ActionEffectDependencies.CopyAndRebind(fixture.Workspace, portable.DeepClone().AsObject(), pending, destination, default),
            "closure copy cannot overwrite an existing owned pending payload");
        var pathEscape = fixture.Source with { Package = fixture.Source.Package with { Path = "../escape.json" } };
        Reject(() => fixture.InspectSource(pathEscape), "source inspector rejects path escape without fallback");
        Check(!Directory.Exists(Path.Combine(root, "assets/models/production")), "engineering checks grant no production acceptance or promotion");
        IntegratedV2PipelineTests.Run(Path.Combine(root, "integrated"), Check, (action, name) => Reject(action, name));
        File.WriteAllText(Path.Combine(root, "effects-v2-verification.json"), JsonSerializer.Serialize(new
        {
            status = "passed", assertions, actualResourceBytes = true, syntheticMotionMetadata = true,
            syntheticIndependentReview = true, actualBlender = false, actualRenderer = false, humanAcceptance = false, promotion = false
        }, EffectProfileV2.Json));
        Console.WriteLine("PASS " + assertions + " action v2 data/resource/closure assertions; motion and review assertions are SYNTHETIC, no Blender/renderer/human acceptance");
        return assertions;
    }
}

// Deliberately synthetic protocol references. The PNGs are owned one-pixel
// resource copies, the movie is labeled inert text, and modules never execute.
// actualEngine=true is a supplied assertion under test, never a browser claim.
sealed class SyntheticReceivingFixture
{
    readonly OwnedEffectFixture fixture;
    readonly string taskPrefix = ".hephaera/.cache/tasks/receiving-fixture/";
    readonly string prefix;
    readonly JsonObject package;
    readonly string modelSha256;
    readonly string packageSha256;
    readonly Dictionary<string, JsonObject> evidence = new(StringComparer.Ordinal);
    public JsonObject Packet { get; }
    public JsonObject Envelope { get; }
    JsonObject Reference(string path) => new() { ["path"] = path, ["sha256"] = Workspace.Hash(fixture.Full(path)), ["bytes"] = new FileInfo(fixture.Full(path)).Length };
    JsonObject Write(string path, byte[] bytes)
    {
        var full = fixture.Workspace.ResolveWrite(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, bytes);
        return Reference(path);
    }
    JsonObject Save(string path, JsonObject node) => Write(path, Encoding.UTF8.GetBytes(node.ToJsonString()));
    public SyntheticReceivingFixture(OwnedEffectFixture owned, JsonObject actionPackage, string name, string? modelHash = null, string? packageHash = null)
    {
        fixture = owned; package = actionPackage; prefix = taskPrefix + name + "/";
        modelSha256 = modelHash ?? fixture.Motion["model"]!["sha256"]!.GetValue<string>();
        packageSha256 = packageHash ?? OwnedEffectFixture.HashBytes(Encoding.UTF8.GetBytes(package.ToJsonString()));
        foreach (var role in new[] { "runtime-test", "runtime-capture", "performance-profile" })
            evidence[role] = Save(prefix + role + ".json", new JsonObject { ["simulationOnly"] = true, ["role"] = role, ["reason"] = "SYNTHETIC PROTOCOL ASSERTION; no browser, performance or human acceptance" });
        var inputs = new JsonArray();
        foreach (var role in new[] { "action-driver", "scene-scheduler", "particle-runtime", "vfx-runtime", "postprocessing-runtime", "library-inventory", "scene", "camera", "capture-settings", "target-device" })
        {
            var module = role is "action-driver" or "scene-scheduler" or "particle-runtime" or "vfx-runtime" or "postprocessing-runtime";
            var reference = Write(prefix + "engine/" + role + (module ? ".mjs" : ".json"),
                Encoding.UTF8.GetBytes(module ? "// SYNTHETIC PROTOCOL MODULE IDENTITY; NEVER EXECUTED\n" : "{\"simulationOnly\":true}"));
            reference["role"] = role; inputs.Add(reference);
        }
        var sources = ActionEffectDependencies.RuntimeSources(package["effectSources"]!.AsArray());
        var sourceAssertions = new JsonArray(); var bindings = new JsonArray();
        foreach (var source in sources)
        {
            var row = new JsonObject
            {
                ["id"] = source.Id, ["effectId"] = source.EffectId, ["packageSha256"] = source.Package.Sha256, ["profileSha256"] = source.Profile.Sha256,
                ["sourceClosureSha256"] = source.Ancestry.SourceClosureSha256, ["productionReviewSha256"] = source.Ancestry.ProductionReviewSha256, ["rightsSha256"] = source.Ancestry.RightsSha256
            };
            if (source.Ancestry.NativePreparationSha256 is { } prep) row["nativePreparationSha256"] = prep;
            sourceAssertions.Add(row);
            foreach (var resource in source.Resources)
            {
                var correspondence = source.Correspondences.Single(c => c.ResourceId == resource.Id);
                var report = EffectProfileV2.ParseData<ActionEffectCorrespondenceReport>(File.ReadAllBytes(fixture.Full(correspondence.Report.Path)));
                bindings.Add(new JsonObject { ["sourceId"] = source.Id, ["resourceId"] = resource.Id, ["resourceSha256"] = resource.Sha256, ["decodedSha256"] = correspondence.DecodedSha256,
                    ["correspondenceSha256"] = correspondence.Report.Sha256, ["sourceEvidenceSha256"] = report.SourceEvidenceSha256 });
            }
        }
        // Independent schedule for this fixture: two one-second source spans,
        // 8fps capture, release at .5 and nine staggered 1/16-second layers.
        double[] times = [0, .125, .25, .375, .5, .5625, .625, .6875, .75, .8125, .875, .9375, 1, 1.0625, 1.125, 1.1875, 1.25, 1.3125, 1.375, 1.4375, 1.5, 1.625, 1.75, 1.875, 2];
        var png = File.ReadAllBytes(fixture.Full(fixture.Source.Package.Path[..fixture.Source.Package.Path.LastIndexOf('/')] + "/spark.png"));
        var samples = new JsonArray();
        for (var i = 0; i < times.Length; i++)
            samples.Add(new JsonObject { ["time"] = times[i], ["artifact"] = Write(prefix + "captures/combined-" + i + ".png", png), ["maximumOccupancy"] = times[i] == 1 });
        var movie = Write(prefix + "captures/normal-speed.webm", Encoding.UTF8.GetBytes("SYNTHETIC PROTOCOL FILE, NOT AN ACTUAL VIDEO OR RENDER"));
        var action = package["actions"]!.AsArray().Single()!;
        var coverage = new JsonArray(new JsonObject
        {
            ["action"] = action["id"]!.DeepClone(), ["start"] = 0, ["end"] = 2, ["fps"] = 8, ["normalSpeed"] = movie, ["samples"] = samples,
            ["cleanup"] = new JsonObject { ["ownedEffects"] = 0, ["resourceLeases"] = 0, ["rigWrites"] = 0 }
        });
        var layers = new JsonArray();
        foreach (var source in sources)
        {
            var profile = EffectProfileV2.Parse(File.ReadAllBytes(fixture.Full(source.Profile.Path)));
            foreach (var layer in profile.Layers.Where(l => l.EffectId == source.EffectId))
                layers.Add(new JsonObject { ["sourceId"] = source.Id, ["effectId"] = source.EffectId, ["layerId"] = layer.LayerId, ["family"] = layer.Family,
                    ["isolated"] = Write(prefix + "captures/" + layer.LayerId + "-isolated.png", png),
                    ["dark"] = Write(prefix + "captures/" + layer.LayerId + "-dark.png", png),
                    ["light"] = Write(prefix + "captures/" + layer.LayerId + "-light.png", png),
                    ["combinedSha256"] = samples[12]!["artifact"]!["sha256"]!.DeepClone() });
        }
        var testIds = new List<string> { "exact-release", "normal-drain", "hard-cancel", "pause-resume", "replace-restart", "reentrant-callback", "actor-removal", "aggregate-scene-ownership", "concurrent-actors-and-ambient", "resource-disposal", "context-restoration", "isolated-layer-families", "contrasting-backgrounds", "trail-discontinuity-reset" };
        if (package["targets"]!.AsArray().Any(t => t!["mode"]!.GetValue<string>() == "fixed-world-point")) testIds.Add("fixed-target-snapshot");
        if (package["targets"]!.AsArray().Any(t => t!["mode"]!.GetValue<string>() == "continuous-world-point")) testIds.AddRange(["continuous-target-freshness", "invalid-target-retirement"]);
        Packet = new JsonObject
        {
            ["schemaVersion"] = "bubo-action-effect-receiving/v1", ["status"] = "passed", ["actualEngine"] = true, ["evidenceKind"] = "selected-engine-render",
            ["engine"] = fixture.Context.Engine.DeepClone(), ["runtimeBuildSha256"] = fixture.Context.RuntimeBuildSha256,
            ["modelSha256"] = modelSha256, ["packageSha256"] = packageSha256, ["effectClosureSha256"] = package["effectClosureSha256"]!.DeepClone(),
            ["effectSources"] = sourceAssertions, ["resourceBindings"] = bindings, ["engineInputs"] = inputs, ["coverage"] = coverage, ["layerCoverage"] = layers,
            ["tests"] = new JsonArray(testIds.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["status"] = "passed", ["evidenceSha256"] = evidence["runtime-test"]["sha256"]!.DeepClone() }).ToArray()),
            ["performance"] = new JsonObject { ["status"] = "passed", ["baselineRuns"] = 5, ["candidateRuns"] = 5, ["measurement"] = "cpu-submission", ["maxConcurrentActors"] = 2, ["artifactSha256"] = evidence["performance-profile"]["sha256"]!.DeepClone() }
        };
        var evidenceRows = new JsonArray();
        foreach (var (role, reference) in evidence)
        {
            var row = reference.DeepClone().AsObject(); row["kind"] = role; evidenceRows.Add(row);
        }
        Envelope = new JsonObject { ["runtime"] = new JsonObject
        {
            ["schemaVersion"] = "bubo-action-runtime-evidence/v2", ["status"] = "passed", ["engine"] = "THREEJS", ["engineIdentity"] = fixture.Context.Engine.DeepClone(),
            ["modelSha256"] = modelSha256, ["packageSha256"] = packageSha256, ["runtimeBuildSha256"] = fixture.Context.RuntimeBuildSha256,
            ["effectClosureSha256"] = package["effectClosureSha256"]!.DeepClone(), ["receiving"] = Save(prefix + "receiving.json", Packet), ["evidence"] = evidenceRows
        } };
    }
    public void Validate(JsonObject? packet = null, JsonObject? envelope = null)
    {
        var current = (envelope ?? Envelope).DeepClone().AsObject();
        current["runtime"]?.AsObject().Remove("receiving");
        if (current["runtime"] is JsonObject runtime) runtime["receiving"] = Save(prefix + "receiving.json", packet ?? Packet);
        RuntimeEffectEvidence.Validate(fixture.Workspace, current, package, fixture.Context.Engine, modelSha256, packageSha256,
            fixture.Context.RuntimeBuildSha256, taskPrefix);
    }
}

static class IntegratedV2PipelineTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> reject)
    {
        var fixture = new OwnedEffectFixture(root, "fx"); var w = fixture.Workspace;
        Fixture.WriteGlb(w.ResolveWrite("actor.glb"), ["kick", "punch"]);
        fixture.Motion["model"]!["path"] = "actor.glb"; fixture.Motion["model"]!["sha256"] = Workspace.Hash(w.Resolve("actor.glb"));
        w.Save("motion.json", fixture.Motion); w.Save("recipe.json", fixture.Recipe);
        var task = "receiving-fixture"; var taskPrefix = ".hephaera/.cache/tasks/" + task + "/";
        var reviewPath = taskPrefix + "source-review.json";
        w.Save(reviewPath, new JsonObject
        {
            ["schemaVersion"] = 1, ["kind"] = "source-motion", ["decision"] = "approved", ["human"] = true, ["reviewer"] = "synthetic-source-reviewer",
            ["reason"] = "SYNTHETIC PROTOCOL SOURCE REVIEW; not real Blender/motion/human acceptance",
            ["rights"] = new JsonObject { ["authorized"] = true, ["license"] = "Original controlled synthetic model/motion fixture" },
            ["motionPackageSha256"] = Workspace.Hash(w.Resolve("motion.json")), ["modelSha256"] = Workspace.Hash(w.Resolve("actor.glb")),
            ["rigId"] = fixture.Motion["rig"]!["id"]!.DeepClone(),
            ["clips"] = new JsonArray(fixture.Motion["clips"]!.AsArray().Select(c => (JsonNode)new JsonObject { ["id"] = c!["id"]!.DeepClone(), ["motionSha256"] = c["motionSha256"]!.DeepClone() }).ToArray())
        });
        var baseline = w.Resolve("baseline"); var scripts = Path.Combine(baseline, "resources/model-to-action/blender");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "action.py"), "# INERT SYNTHETIC AUDITED FIXTURE; NEVER EXECUTED\n");
        File.WriteAllText(Path.Combine(scripts, "scripts.json"), JsonSerializer.Serialize(new { schemaVersion = 1, files = new Dictionary<string, string> { ["action.py"] = Workspace.Hash(Path.Combine(scripts, "action.py")) } }));
        File.WriteAllText(w.ResolveWrite("blender.exe"), "INERT SYNTHETIC TOOL IDENTITY; NEVER EXECUTED");
        var settings = new Dictionary<string, string>
        {
            ["task"] = task, ["asset"] = "integrated", ["version"] = "v2", ["actor"] = "synthetic-builder",
            ["motion-package"] = "motion.json", ["recipe"] = "recipe.json", ["source-review"] = reviewPath,
            ["blender"] = w.Resolve("blender.exe"), ["baseline-root"] = baseline,
            ["host-style-sha256"] = fixture.Context.StyleSha256, ["runtime-build-sha256"] = fixture.Context.RuntimeBuildSha256
        };
        var native = new V2ProtocolProcess(w);
        var engine = new V2ProtocolEngine(fixture.Context.Engine);
        void Execute(string command, Dictionary<string, string>? arguments = null) =>
            new Pipeline(w, arguments ?? settings, native, engine, new EffectDependencyInspector()).Execute(command, default).GetAwaiter().GetResult();
        Execute("init");
        settings["manifest"] = taskPrefix + "model-to-action/integrated/v2/manifest.json";
        Manifest State() => w.Load<Manifest>(settings["manifest"]);
        JsonObject Status()
        {
            var original = Console.Out; using var text = new StringWriter();
            try { Console.SetOut(text); Execute("status"); } finally { Console.SetOut(original); }
            return JsonNode.Parse(text.ToString())!.AsObject();
        }
        var initialized = State();
        check(initialized.SchemaVersion == "hephaera-model-action/v2" && initialized.EffectSourceInventories?.Single().Files.Count == 9,
            "SYNTHETIC Pipeline init persists complete v2 private source inventory");
        check(initialized.SourceInputs.Keys.Any(p => p.StartsWith("private/", StringComparison.Ordinal))
            && initialized.Originals.Keys.Count(p => p.Contains("/effects/fx/", StringComparison.Ordinal)) == 9,
            "SYNTHETIC Pipeline init snapshots all private resources, rights and review evidence");
        _ = Status();
        settings["host-style-sha256"] = new string('e', 64);
        reject(() => Execute("status"), "SYNTHETIC Pipeline rejects changed selected STYLE");
        settings["host-style-sha256"] = fixture.Context.StyleSha256;
        settings["runtime-build-sha256"] = new string('e', 64);
        reject(() => Execute("status"), "SYNTHETIC Pipeline rejects changed native build identity");
        settings["runtime-build-sha256"] = fixture.Context.RuntimeBuildSha256;
        foreach (var operation in new[] { "doctor", "inspect", "compose", "export", "review", "validate" }) Execute(operation);
        var current = State(); var export = current.Stages["export"]; var packagePath = export.Artifacts["package"];
        var package = w.Load<JsonObject>(packagePath);
        check(native.IndependentCalls == 1 && export.Artifacts.ContainsKey("independentVerification"),
            "SYNTHETIC Pipeline export requires a distinct verification invocation and exact report role");
        check(package["effectSources"]![0]!["resources"]!.AsArray().Count == 2
            && w.Load<JsonObject>(current.Stages["review"].Artifacts["reviewReport"])["actualEngineFX"]!.GetValue<string>() == "not-rendered",
            "SYNTHETIC Pipeline permits only declared static effect GLBs and labels native review as motion/anchors only");
        settings["destination"] = "assets/models/integrated-fixture";
        reject(() => Execute("promote"), "SYNTHETIC Pipeline native stages cannot manufacture promotion acceptance");
        var runtime = new SyntheticReceivingFixture(fixture, package, "integrated", Workspace.Hash(w.Resolve(export.Artifacts["glb"])), Workspace.Hash(w.Resolve(packagePath)));
        JsonObject Receipt(string kind, string reviewer, bool receiving)
        {
            var receipt = receiving ? runtime.Envelope.DeepClone().AsObject() : new JsonObject();
            receipt["kind"] = kind; receipt["reviewer"] = reviewer; receipt["decision"] = "approved"; receipt["human"] = true;
            receipt["reason"] = "SYNTHETIC PIPELINE RECEIPT ONLY; no actual engine, human, art or production acceptance";
            receipt["fingerprint"] = Status()["approvalFingerprints"]![kind]!.DeepClone();
            return receipt;
        }
        void Record(string kind, string reviewer, bool receiving = false)
        {
            var receiptPath = taskPrefix + kind + "-receipt.json"; w.Save(receiptPath, Receipt(kind, reviewer, receiving));
            settings["kind"] = kind; settings["reviewer"] = reviewer; settings["evidence"] = receiptPath; Execute("record");
        }
        Record("action", "synthetic-action-human");
        reject(() => Record("independent", "synthetic-independent", false), "SYNTHETIC independent review cannot omit selected-engine v2 receiving evidence");
        Record("independent", "synthetic-independent", true);
        reject(() => Record("human", "synthetic-final-human", false), "SYNTHETIC human review cannot omit selected-engine v2 receiving evidence");
        Record("human", "synthetic-final-human", true);
        Execute("promote");
        var destination = settings["destination"]; var promoted = w.Load<JsonObject>(destination + "/action-package.json");
        ActionEffectDependencies.ValidateRuntimeClosure(w, promoted);
        check(promoted["model"]!["path"]!.GetValue<string>() == destination + "/model.glb"
            && promoted["effectSources"]![0]!["package"]!["path"]!.GetValue<string>().StartsWith(destination + "/", StringComparison.Ordinal),
            "SYNTHETIC Pipeline promotion copies and rebinds actor and complete effect payload together");
        check(Directory.EnumerateFiles(w.Resolve(destination), "*", SearchOption.AllDirectories)
                .All(p => !Encoding.UTF8.GetString(File.ReadAllBytes(p)).Contains(OwnedEffectFixture.PrivateMarker, StringComparison.Ordinal)),
            "SYNTHETIC Pipeline promotion excludes private source review/evidence bytes");
        reject(() => Execute("promote"), "SYNTHETIC Pipeline promotion cannot overwrite accepted fixture destination");
        var inputs = runtime.Packet["engineInputs"]!.AsArray(); var cameraPath = w.Resolve(inputs.Single(i => i!["role"]!.GetValue<string>() == "camera")!["path"]!.GetValue<string>());
        var camera = File.ReadAllBytes(cameraPath);
        try { File.AppendAllText(cameraPath, "changed"); reject(() => Execute("validate"), "SYNTHETIC accepted Pipeline evidence becomes stale after transitive camera drift"); }
        finally { File.WriteAllBytes(cameraPath, camera); }
        var failure = new Dictionary<string, string>(settings) { ["asset"] = "missing-independent" };
        failure.Remove("manifest"); Execute("init", failure); failure["manifest"] = taskPrefix + "model-to-action/missing-independent/v2/manifest.json";
        Execute("inspect", failure); Execute("compose", failure);
        native.OmitIndependentProof = true;
        try { reject(() => Execute("export", failure), "SYNTHETIC producing export cannot omit its independent verification proof"); }
        finally { native.OmitIndependentProof = false; }
        check(w.Load<Manifest>(failure["manifest"]).Stages["export"].Status == "failed", "SYNTHETIC failed independent export remains retained and cannot appear current");
    }
}

sealed class V2ProtocolEngine(JsonObject identity) : IEngineResolver
{
    public Task<JsonObject> Resolve(CancellationToken cancellation) => Task.FromResult(identity.DeepClone().AsObject());
}

sealed class V2ProtocolProcess(Workspace workspace) : INativeProcess
{
    readonly FakeProcess inherited = new(workspace);
    public int IndependentCalls { get; private set; }
    public bool OmitIndependentProof { get; set; }
    public async Task Run(string executable, IEnumerable<string> arguments, string cwd, string log, int seconds,
        CancellationToken cancellation = default, long maximumLogBytes = 16777216, IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = arguments.ToArray();
        if (args.Contains("--version")) { await inherited.Run(executable, args, cwd, log, seconds, cancellation, maximumLogBytes, environment); return; }
        var job = JsonNode.Parse(File.ReadAllText(args[System.Array.IndexOf(args, "--job") + 1]))!.AsObject();
        var operation = job["operation"]!.GetValue<string>(); var output = job["outputDir"]!.GetValue<string>();
        Directory.CreateDirectory(workspace.ResolveWrite(output)); Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        if (operation is not ("verify-export" or "review"))
        {
            await inherited.Run(executable, args, cwd, log, seconds, cancellation, maximumLogBytes, environment);
            if (operation == "export" && !OmitIndependentProof)
            {
                var proof = output + "/verification/export-proof.json";
                workspace.Save(proof, new { simulationOnly = true, note = "INERT SYNTHETIC PROOF; no geometry or fresh Blender execution" });
                var result = workspace.Load<JsonObject>(job["result"]!.GetValue<string>()); result["artifacts"]!["exportProof"] = proof;
                workspace.Save(job["result"]!.GetValue<string>(), result);
            }
            return;
        }
        File.WriteAllText(log, "SYNTHETIC NATIVE PROCESS; no Blender or renderer launched\n");
        var artifacts = new JsonObject(); var metrics = new JsonObject { ["simulationOnly"] = true };
        if (operation == "verify-export")
        {
            IndependentCalls++; var report = output + "/verification-report.json";
            workspace.Save(report, new { simulationOnly = true, note = "SYNTHETIC verification assertion; no real independent import" });
            artifacts["verificationReport"] = report; metrics["freshProcess"] = true;
        }
        else
        {
            double[] times = [0, .125, .25, .375, .5, .5625, .625, .6875, .75, .8125, .875, .9375, 1, 1.0625, 1.125, 1.1875, 1.25, 1.3125, 1.375, 1.4375, 1.5, 1.625, 1.75, 1.875, 2];
            var packets = new JsonArray(); var count = 0;
            foreach (var action in job["plan"]!["actions"]!.AsArray())
            {
                var frames = new JsonArray();
                foreach (var camera in job["review"]!["cameras"]!.AsArray())
                foreach (var time in times)
                {
                    var file = output + "/full-" + count++ + ".png";
                    File.WriteAllBytes(workspace.ResolveWrite(file), [137, 80, 78, 71, 13, 10, 26, 10]);
                    frames.Add(new JsonObject { ["path"] = file, ["sha256"] = Workspace.Hash(workspace.Resolve(file)), ["time"] = time, ["camera"] = camera!.DeepClone() });
                }
                packets.Add(new JsonObject { ["id"] = action!["id"]!.DeepClone(), ["frames"] = frames });
            }
            var report = output + "/report.json"; var page = output + "/review.html";
            File.WriteAllText(workspace.ResolveWrite(page), "<!doctype html><html>SYNTHETIC motion-only review</html>");
            workspace.Save(report, new JsonObject { ["frameCount"] = count, ["requestedFrameCount"] = count, ["humanApproval"] = "pending",
                ["cueRendering"] = "motion-and-anchors-only", ["actualEngineFX"] = "not-rendered", ["packets"] = packets });
            artifacts["review"] = page; artifacts["report"] = report; artifacts["reviewReport"] = report;
        }
        workspace.Save(job["result"]!.GetValue<string>(), new JsonObject { ["schemaVersion"] = "hephaera-action-stage/v1", ["operation"] = operation,
            ["inputSha256"] = Workspace.Hash(workspace.Resolve(job["input"]!.GetValue<string>())), ["artifacts"] = artifacts, ["metrics"] = metrics, ["findings"] = new JsonArray() });
    }
}

sealed class OwnedEffectFixture
{
    public static readonly string[] Families = ["sprite", "ring", "mesh", "burst", "projectile", "trail", "impact", "ground-indicator", "beam"];
    public static readonly byte[] Rgba = [255, 64, 32, 128];
    public const string PrivateMarker = "PRIVATE_FIXTURE_ONLY_NOT_RUNTIME_PAYLOAD";
    readonly string id;
    public Workspace Workspace { get; }
    public ActionEffectSource Source { get; private set; } = null!;
    public ActionEffectInspectionContext Context { get; }
    public JsonObject Recipe { get; }
    public JsonObject Motion { get; }
    public static JsonObject EngineIdentity() => new() { ["effectiveEngine"] = "THREEJS", ["contractSha256"] = new string('1', 64), ["definitionSha256"] = new string('2', 64), ["registrySha256"] = new string('3', 64) };
    public static JsonObject Address(int occurrence, double offset) => new() { ["step"] = "cast", ["occurrence"] = occurrence, ["offset"] = offset };
    public string Full(string relative) => Workspace.Resolve(relative);
    public static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    string Save(string relative, JsonNode node)
    {
        Write(relative, Encoding.UTF8.GetBytes(node.ToJsonString())); return Workspace.Hash(Full(relative));
    }
    void Write(string relative, byte[] bytes)
    {
        var path = Workspace.ResolveWrite(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
    }
    ActionEffectFile Row(string role, string path, string? resource = null) => new()
    { Role = role, SourceId = id, ResourceId = resource, Path = path, Bytes = new FileInfo(Full(path)).Length, Sha256 = Workspace.Hash(Full(path)) };
    ActionSizedFileReference Sized(string path) => new() { Path = path, Bytes = new FileInfo(Full(path)).Length, Sha256 = Workspace.Hash(Full(path)) };
    static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, EffectProfileV2.Json)!;
    public ActionEffectInspection InspectSource(ActionEffectSource source, ActionEffectInspectionContext? context = null, CancellationToken cancellation = default) =>
        new EffectDependencyInspector().Inspect(Workspace, source, context ?? Context, cancellation);
    public ActionEffectCatalog Inspect(JsonObject recipe) => ActionEffectDependencies.Inspect(Workspace, recipe,
        Context with { Budgets = recipe["budgets"]!.AsObject() }, new EffectDependencyInspector(), default);

    public OwnedEffectFixture(string root, string identifier, int maxIndices = 300000, long maxBufferBytes = 8388608,
        IReadOnlyCollection<string>? selectedFamilies = null)
    {
        id = identifier; Directory.CreateDirectory(root); Workspace = new Workspace(root);
        var prefix = "inputs/" + id;
        var png = prefix + "/package/spark.png"; var mesh = prefix + "/package/triangle.glb";
        var packagePath = prefix + "/package/package.json"; var profilePath = prefix + "/profile.json";
        var rightsPath = prefix + "/rights.json"; var reviewPath = prefix + "/review.json";
        var evidencePath = "private/" + id + "/production-evidence.json";
        Write(png, Png()); Write(mesh, TriangleGlb());
        var selected = new HashSet<string>(selectedFamilies ?? Families, StringComparer.Ordinal);
        var resources = new[] { ("spark", "texture-png", png), ("triangle", "mesh-glb", mesh) }.Where(r => r.Item1 != "triangle" || selected.Contains("mesh")).ToArray();
        var nativeLayers = new JsonArray(); var profileLayers = new JsonArray();
        for (var i = 0; i < Families.Length; i++)
        {
            var family = Families[i]; var ids = family == "beam" ? new JsonArray() : family == "mesh" ? new JsonArray("spark", "triangle") : new JsonArray("spark");
            if (!selected.Contains(family)) continue;
            nativeLayers.Add(new JsonObject
            {
                ["id"] = family, ["kind"] = family, ["startSeconds"] = i / 16.0, ["durationSeconds"] = .75, ["resourceIds"] = ids,
                ["maxParticles"] = 4, ["maxVertices"] = 128, ["maxDrawCalls"] = 1, ["maxTrailPoints"] = family == "trail" ? 8 : 0
            });
            var layer = new JsonObject
            {
                ["effectId"] = "cast", ["layerId"] = family, ["family"] = family,
                ["emission"] = new JsonObject { ["kind"] = "burst", ["capacity"] = 1, ["count"] = 1, ["rate"] = 0, ["lifetimeSeconds"] = .125, ["durationSeconds"] = .25 },
                ["size"] = .1, ["speed"] = 0, ["color"] = new JsonArray(1, .5, .25, 1), ["space"] = "world", ["maxTailSeconds"] = .5
            };
            if (family != "beam") layer["resourceId"] = "spark";
            if (family == "mesh") layer["geometry"] = new JsonObject { ["kind"] = "binding", ["resourceId"] = "triangle" };
            if (family is "ring" or "ground-indicator") layer["geometry"] = new JsonObject { ["kind"] = "ring", ["innerRadius"] = .25, ["outerRadius"] = .5, ["segments"] = 12 };
            if (family == "trail") layer["trail"] = new JsonObject { ["historyPoints"] = 8 };
            profileLayers.Add(layer);
        }
        var package = new JsonObject
        {
            ["schemaVersion"] = "bubo-vfx-package/v1", ["id"] = id, ["version"] = "v1", ["styleSha256"] = new string('a', 64),
            ["engine"] = new JsonObject { ["id"] = "THREEJS", ["contractSha256"] = new string('1', 64), ["definitionSha256"] = new string('2', 64), ["registrySha256"] = new string('3', 64) },
            ["budgets"] = JsonNode.Parse("""{"maxPackageBytes":65536,"maxDependencyBytes":4096,"maxTotalDependencyBytes":8192,"maxDependencies":2,"maxEffects":1,"maxLayers":9,"maxParticles":256,"maxVertices":4096,"maxDrawCalls":32,"maxTrailPoints":64,"maxDurationSeconds":4}"""),
            ["resources"] = new JsonArray(resources.Select(r => (JsonNode)new JsonObject { ["id"] = r.Item1, ["kind"] = r.Item2, ["path"] = Path.GetFileName(r.Item3), ["sha256"] = Workspace.Hash(Full(r.Item3)), ["bytes"] = new FileInfo(Full(r.Item3)).Length }).ToArray()),
            ["effects"] = new JsonArray(new JsonObject { ["id"] = "cast", ["durationSeconds"] = 2, ["layers"] = nativeLayers })
        };
        var packageHash = Save(packagePath, package);
        var profile = new JsonObject
        {
            ["schemaVersion"] = "hephaera-three-vfx-profile/v1", ["nativePackageSha256"] = packageHash,
            ["budgets"] = JsonNode.Parse("""{"maxSystems":64,"maxParticles":64,"maxTrailPoints":64,"maxVertices":100000,"maxIndices":300000,"maxDrawCalls":64,"maxTextureBytes":4096,"maxBufferBytes":8388608,"maxDurationSeconds":4,"maxUpdateSeconds":0.1}"""),
            ["layers"] = profileLayers
        };
        profile["budgets"]!["maxIndices"] = maxIndices;
        profile["budgets"]!["maxBufferBytes"] = maxBufferBytes;
        var profileHash = Save(profilePath, profile);
        var evidenceHash = Save(evidencePath, new JsonObject { ["simulationOnly"] = true, ["privateMarker"] = PrivateMarker, ["reason"] = "Original controlled fixture; synthetic independent/human assertions, no quality or production acceptance" });
        var rightsHash = Save(rightsPath, Node(new ActionEffectRights
        {
            SchemaVersion = "bubo-vfx-source-rights/v1", Authorized = true, License = "Original controlled engineering resource bytes",
            Owner = "owned-fixture-author", Reason = "SYNTHETIC RIGHTS ASSERTION for original test PNG/triangle only", Inputs = resources.Select(r => Sized(r.Item3)).ToArray()
        }));
        var correspondences = new List<ActionEffectCorrespondence>();
        foreach (var resource in resources)
        {
            var geometry = resource.Item2 == "mesh-glb"; var path = prefix + "/" + resource.Item1 + "-correspondence.json";
            var decoded = geometry ? HashBytes(Encoding.UTF8.GetBytes("position:Float32Array:3:3\n").Concat(TrianglePositions())
                .Concat(Encoding.UTF8.GetBytes("uv:Float32Array:2:3\n")).Concat(TriangleUvs()).ToArray()) : HashBytes(Rgba);
            var measurements = geometry
                ? new ActionEffectResourceMeasurements { Bytes = 60, Vertices = 3, Indices = 0 }
                : new ActionEffectResourceMeasurements { Bytes = 4, Width = 1, Height = 1 };
            var reportHash = Save(path, Node(new ActionEffectCorrespondenceReport
            {
                SchemaVersion = "bubo-vfx-resource-correspondence/v1", ResourceId = resource.Item1, ResourceSha256 = Workspace.Hash(Full(resource.Item3)),
                Kind = geometry ? "geometry" : "texture", DecodedSha256 = decoded, Independent = true,
                Measurements = measurements, SourceEvidenceSha256 = evidenceHash
            }));
            correspondences.Add(new() { ResourceId = resource.Item1, ResourceSha256 = Workspace.Hash(Full(resource.Item3)), Kind = geometry ? "geometry" : "texture", DecodedSha256 = decoded,
                Report = new() { Path = path, Sha256 = reportHash, Bytes = new FileInfo(Full(path)).Length } });
        }
        Recipe = Fixture.Recipe(); Recipe["schemaVersion"] = 2; Recipe["fps"] = 16;
        Recipe["budgets"]!["maxEffectSources"] = 8; Recipe["budgets"]!["maxEffectFiles"] = 128;
        Recipe["budgets"]!["maxEffectDependencyBytes"] = 1048576L; Recipe["budgets"]!["maxEffectJsonBytes"] = 1048576L;
        Recipe["budgets"]!["maxTailSeconds"] = 1; Recipe["budgets"]!["maxTrailSamples"] = 64;
        Recipe["actions"] = new JsonArray(new JsonObject
        {
            ["id"] = "cast", ["role"] = "presentation", ["steps"] = JsonNode.Parse("""[{"id":"cast","clip":"kick","from":0,"to":1,"rate":1,"repeat":2,"blendIn":0}]"""),
            ["cues"] = new JsonArray(new JsonObject
            {
                ["id"] = "cast-effects", ["type"] = "effect", ["source"] = id, ["anchor"] = "hand",
                ["start"] = new JsonObject { ["step"] = "cast", ["occurrence"] = 0, ["marker"] = "release" }, ["end"] = Address(1, .75),
                ["placement"] = "beam", ["target"] = "point",
                ["transform"] = JsonNode.Parse("""{"units":"meters","up":"+Y","forward":"+Z","handedness":"right","position":[0.125,0.25,0.5],"orientation":[0,0,0,1],"scale":1,"parentScale":"positive-uniform"}"""),
                ["lifetime"] = JsonNode.Parse("""{"tailSeconds":0.25,"pause":"follow-action","tailAttachment":"capture-world-on-stop","cancel":"retire-generation"}"""),
                ["trail"] = JsonNode.Parse("""{"reset":"teleport-or-discontinuity","maxSamples":8,"distanceMeters":1}""")
            })
        });
        if (!selected.Contains("beam")) { Recipe["actions"]![0]!["cues"]![0]!["placement"] = "anchor-follow"; Recipe["actions"]![0]!["cues"]![0]!.AsObject().Remove("target"); }
        if (!selected.Contains("trail")) Recipe["actions"]![0]!["cues"]![0]!.AsObject().Remove("trail");
        Motion = Fixture.Motion(Workspace.Hash(Full(mesh))); Motion["engine"] = EngineIdentity();
        Context = new(EngineIdentity(), new string('a', 64), new string('b', 64), Recipe["budgets"]!.AsObject());
        var files = new List<ActionEffectFile> { Row("native-package", packagePath), Row("production-profile", profilePath), Row("rights", rightsPath), Row("production-evidence", evidencePath) };
        files.AddRange(resources.Select(resource => Row("resource", resource.Item3, resource.Item1)));
        files.AddRange(correspondences.Select(c => Row("source-correspondence", c.Report.Path, c.ResourceId)));
        var reviewHash = Save(reviewPath, Node(new ActionEffectProductionReview
        {
            SchemaVersion = "bubo-vfx-source-production-review/v1", Decision = "approved", Independent = true,
            Reviewer = "synthetic-independent", Author = "owned-fixture-author",
            SubjectSha256 = ActionEffectDependencies.SourceFingerprint(files, Context, "cast"),
            Reason = "SYNTHETIC independent fixture assertion only; no authentic human or renderer acceptance", Evidence = [Sized(evidencePath)]
        }));
        Source = new() { Id = id, EffectId = "cast", Package = new() { Path = packagePath, Sha256 = packageHash },
            Profile = new() { SchemaVersion = "hephaera-three-vfx-profile/v1", Path = profilePath, Sha256 = profileHash },
            Correspondences = correspondences, Rights = new() { Path = rightsPath, Sha256 = rightsHash }, ProductionReview = new() { Path = reviewPath, Sha256 = reviewHash } };
        Recipe["effectSources"] = new JsonArray(Node(Source));
    }

    static byte[] TrianglePositions()
    {
        float[] values = [0, 0, 0, 1, 0, 0, 0, 1, 0]; var bytes = new byte[36];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }
    static byte[] TriangleGlb()
    {
        var json = Encoding.UTF8.GetBytes("""{"asset":{"version":"2.0","generator":"Original owned engineering triangle"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0,"name":"OwnedTriangle"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0,"TEXCOORD_0":1},"mode":4}]}],"buffers":[{"byteLength":60}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":24}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},{"bufferView":1,"componentType":5126,"count":3,"type":"VEC2"}]}""");
        var padded = (json.Length + 3) & ~3; var bytes = new byte[12 + 8 + padded + 8 + 60];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546c67); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), bytes.Length); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), padded);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4e4f534a); bytes.AsSpan(20, padded).Fill(32); json.CopyTo(bytes, 20);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + padded), 60); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + padded), 0x004e4942);
        TrianglePositions().CopyTo(bytes, 28 + padded); TriangleUvs().CopyTo(bytes, 64 + padded); return bytes;
    }
    static byte[] TriangleUvs()
    {
        float[] values = [0, 0, 1, 0, 0, 1]; var bytes = new byte[24];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }
    static byte[] Png()
    {
        using var result = new MemoryStream(); result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(ihdr, 1); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 1); ihdr[8] = 8; ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(new byte[] { 0 }.Concat(Rgba).ToArray());
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return result.ToArray();
        void Chunk(string name, byte[] content)
        {
            Span<byte> word = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, content.Length); result.Write(word);
            var type = Encoding.ASCII.GetBytes(name); result.Write(type); result.Write(content); var crc = uint.MaxValue;
            foreach (var value in type.Concat(content)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320U & (uint)-(int)(crc & 1)); }
            BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); result.Write(word);
        }
    }
}
