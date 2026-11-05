using System.Text.Json.Nodes;
using HostExecutionContext = Bubo.Host.HostExecutionContext;
using Bubo.Vfx.Infrastructure;
using Hephaera.AssetProduction;
using Hephaera.ModelAction.Application;
using Hephaera.ModelAction.Domain;
using Hephaera.ModelAction.Infrastructure;

// Actual native math-mask preparation and filesystem verification. The selected
// policy is an original synthetic engineering fixture, not human asset approval.
static class PreparationContextTests
{
    public static int Run(string parent)
    {
        var root = Path.Combine(parent, "preparation-context"); Directory.CreateDirectory(root);
        var files = new Workspace(root); var passed = 0;
        var build = new string('b', 64);
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS REAL PREPARATION CONTEXT: " + name); }
        void Reject(Action run, string name)
        {
            try { run(); } catch (Exception error) when (error is InvalidOperationException or IOException) { Check(true, name); return; }
            throw new Exception("Expected original-context rejection: " + name);
        }
        files.Save("inputs/policy.json", new { kind = "original synthetic policy", engine = "THREEJS" });
        var style = files.HashProjectFile("inputs/policy.json");
        var engine = new JsonObject { ["effectiveEngine"] = "THREEJS", ["contractSha256"] = new string('1', 64), ["definitionSha256"] = new string('2', 64), ["registrySha256"] = new string('3', 64) };
        files.Save("inputs/request.json", new JsonObject
        {
            ["schemaVersion"] = "bubo-vfx-preparation/v1", ["id"] = "original-mask", ["version"] = "v1", ["styleSha256"] = style,
            ["engine"] = new JsonObject { ["id"] = "THREEJS", ["contractSha256"] = engine["contractSha256"]!.DeepClone(), ["definitionSha256"] = engine["definitionSha256"]!.DeepClone(), ["registrySha256"] = engine["registrySha256"]!.DeepClone() },
            ["profile"] = "math-mask", ["sources"] = new JsonArray(),
            ["timing"] = JsonNode.Parse("""{"firstFrame":0,"frameCount":1,"frameStep":1,"rateNumerator":24,"rateDenominator":1,"loop":"once"}"""),
            ["atlas"] = JsonNode.Parse("""{"columns":1,"padding":1,"extrusion":1,"alpha":"straight","colorSpace":"srgb"}"""),
            ["mask"] = JsonNode.Parse("""{"kind":"disc","width":9,"height":7,"innerRadius":0,"outerRadius":0.8,"softness":0.2,"angleDegrees":0}"""),
            ["budgets"] = JsonNode.Parse("""{"maxSourceBytes":4096,"maxTotalSourceBytes":16384,"maxChunkBytes":4096,"maxCompressedBytes":4096,"maxFrames":8,"maxDecodedPixels":4096,"maxAtlasPixels":65536,"maxPixelWork":1048576,"maxWorkingBytes":33554432,"maxOutputBytes":1048576,"maxOutputFiles":16,"maxSeconds":15,"minimumFreeDiskBytes":0}""")
        });
        JsonObject Ref(string path, string kind = "input") => new() { ["path"] = path, ["sha256"] = files.HashProjectFile(path), ["kind"] = kind };
        JsonObject Context(string task, bool oldState = false) => new()
        {
            ["schemaVersion"] = "bubo-host-context/v1", ["projectRoot"] = root, ["task"] = task,
            ["roots"] = new JsonObject { ["state"] = ".hephaera/.cache/tasks/" + task, ["staging"] = ".art-staging", ["tools"] = new JsonArray(),
                ["inputs"] = oldState ? new JsonArray("inputs", ".hephaera/.cache/tasks/origin") : new JsonArray("inputs"), ["outputs"] = new JsonArray() },
            ["policy"] = new JsonObject { ["pipeline"] = "PROOF_TO_MODEL", ["styleSha256"] = style, ["engine"] = engine.DeepClone(), ["libraries"] = new JsonArray() },
            ["references"] = new JsonArray(Ref("inputs/policy.json", "policy"), Ref("inputs/request.json")), ["tools"] = new JsonArray(),
            ["budgets"] = new JsonObject { ["timeoutSeconds"] = 30, ["maxLogBytes"] = 1048576 },
            ["permissions"] = new JsonObject { ["provisioning"] = false, ["promotion"] = false }
        };
        files.Save("inputs/origin-context.json", Context("origin"));
        var origin = HostExecutionContext.Load(root, "inputs/origin-context.json");
        var prepared = VfxPreparation.Prepare(new Workspace(root, origin), "inputs/request.json", ".art-staging/original-mask/v1", build);
        Check(prepared.IsValid, "actual original task creates a bounded immutable math-mask preparation");
        var currentValue = Context("receiver", true); currentValue["references"]!.AsArray().Add(Ref("inputs/origin-context.json"));
        files.Save("inputs/current-context.json", currentValue);
        Workspace Current() => new(root, HostExecutionContext.Load(root, "inputs/current-context.json"));
        var context = new ActionEffectInspectionContext(engine, style, build, new());
        var source = new ActionEffectSource
        {
            Id = "original-mask", EffectId = "prepared", Package = new() { Path = prepared.PackagePath, Sha256 = prepared.PackageSha256 },
            Profile = new() { SchemaVersion = "hephaera-three-vfx-profile/v1", Path = "inputs/unused-profile.json", Sha256 = new string('a', 64) },
            Preparation = new() { Path = prepared.ManifestPath, Sha256 = prepared.ManifestSha256 },
            PreparationContext = new() { Path = "inputs/origin-context.json", Sha256 = files.HashProjectFile("inputs/origin-context.json") },
            Correspondences = [], Rights = new() { Path = "inputs/unused-rights.json", Sha256 = new string('a', 64) }, ProductionReview = new() { Path = "inputs/unused-review.json", Sha256 = new string('a', 64) }
        };
        Dictionary<string, string> Snapshot() => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(files.Relative, Workspace.Hash);
        var before = Snapshot(); var admitted = new List<ActionEffectFile>();
        var verified = ActionPreparationContextVerifier.Verify(Current(), source, context, admitted.Add, default);
        Check(verified.Result.IsValid && verified.Result.PackageSha256 == prepared.PackageSha256, "explicit original-task context permits same-project readonly preparation reuse");
        Check(admitted.Any(file => file.Role == "native-preparation-claim" && file.Path.Contains("/origin/", StringComparison.Ordinal)), "receiving verification binds the original permanent task claim");
        var after = Snapshot();
        Check(before.Count == after.Count && before.All(pair => after[pair.Key] == pair.Value), "actual cross-task verification changes no source, seal, claim or context bytes");
        Reject(() => ActionPreparationContextVerifier.Verify(Current(), source with { PreparationContext = null }, context, _ => { }, default), "cross-task reuse cannot omit original context");
        Reject(() => ActionPreparationContextVerifier.Verify(Current(), source, context with { RuntimeBuildSha256 = new string('c', 64) }, _ => { }, default), "changed build cannot inherit prepared output verification");
        Reject(() => ActionPreparationContextVerifier.Verify(Current(), source, context with { StyleSha256 = new string('c', 64) }, _ => { }, default), "changed STYLE cannot inherit original preparation policy");
        var limited = Context("receiver"); limited["references"]!.AsArray().Add(Ref("inputs/origin-context.json")); files.Save("inputs/current-context.json", limited);
        Reject(() => ActionPreparationContextVerifier.Verify(Current(), source, context, _ => { }, default), "current read scope must explicitly admit original state ancestry");
        files.Save("inputs/current-context.json", currentValue);
        var old = File.ReadAllBytes(files.Resolve("inputs/origin-context.json"));
        var changed = Context("other-task"); files.Save("inputs/origin-context.json", changed);
        var changedHash = files.HashProjectFile("inputs/origin-context.json");
        var rehashed = currentValue.DeepClone().AsObject(); rehashed["references"]!.AsArray()[2] = Ref("inputs/origin-context.json"); files.Save("inputs/current-context.json", rehashed);
        Reject(() => ActionPreparationContextVerifier.Verify(Current(), source with { PreparationContext = source.PreparationContext! with { Sha256 = changedHash } }, context, _ => { }, default), "rehashed context cannot transplant original preparation task identity");
        File.WriteAllBytes(files.ResolveWrite("inputs/origin-context.json"), old); files.Save("inputs/current-context.json", currentValue);
        Check(ActionPreparationContextVerifier.Verify(Current(), source, context, _ => { }, default).Result.IsValid, "restored exact original contexts remain verifiable without resetting attempts");
        return passed;
    }
}
