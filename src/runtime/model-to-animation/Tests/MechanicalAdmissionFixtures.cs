using System.Text.Json.Nodes;
using Hephaera.AssetProduction;
using Hephaera.ModelAnimation.Application;

static class MechanicalAdmissionFixtures
{
    public static int Run(Workspace workspace, JsonObject originalRecipe)
    {
        var passed = 0;
        void Check(Action operation, string description, bool rejects = false)
        {
            var rejected = false;
            try { operation(); } catch (InvalidOperationException) { rejected = true; }
            if (rejected != rejects) throw new Exception("Mechanical admission fixture: " + description);
            Console.WriteLine("PASS SIMULATED: " + description); passed++;
        }
        var recipe = originalRecipe.DeepClone().AsObject();
        recipe["profile"] = "mechanical-assembly";
        recipe["parts"] = new JsonArray(); recipe["anchors"] = new JsonArray();
        Check(() => MotionValidation.Recipe(recipe), "preserve-only zero-rig mechanical recipe is valid");
        foreach (var change in new Action<JsonObject>[] {
            r => r["rig"]!["mode"] = "declared", r => r["rig"]!["rootBone"] = "unowned-root",
            r => r["parts"]!.AsArray().Add(new JsonObject { ["node"] = "Body" }),
            r => r["clips"]![0]!["channels"]![0]!["target"] = new JsonObject { ["bone"] = "ambiguous" } })
        {
            var invalid = recipe.DeepClone().AsObject(); change(invalid);
            Check(() => MotionValidation.Recipe(invalid), "mechanical rig creation, hierarchy rewrite or ambiguous target rejected", true);
        }
        var glb = MotionValidation.Glb(workspace.Resolve("model.glb"));
        var mesh = glb["nodes"]!.AsArray().Select(n => n!.AsObject()).First(n => n["mesh"] is not null);
        var component = new JsonObject { ["instanceId"] = "one", ["partId"] = "part-one", ["node"] = mesh["name"]!.DeepClone(), ["kind"] = "rigid", ["rigNode"] = null };
        var components = new JsonArray(component);
        const string folder = "mechanical-fixtures/";
        Directory.CreateDirectory(workspace.Resolve(folder));
        File.Copy(workspace.Resolve("model.glb"), workspace.Resolve(folder + "assembly.glb"));
        var numerics = new JsonObject { ["linearTolerance"] = 1e-6, ["angularToleranceDegrees"] = .01, ["minimumFeatureSize"] = .0001, ["maxCoordinateMagnitude"] = 10000 };
        var design = new JsonObject { ["simulationOnly"] = true, ["numerics"] = numerics.DeepClone() };
        var metadata = design.DeepClone().AsObject();
        workspace.Save(folder + "design.json", design); workspace.Save(folder + "metadata.json", metadata);
        workspace.Save(folder + "provenance.json", new { simulationOnly = true });
        JsonObject Reference(string path) => new() { ["path"] = path, ["sha256"] = workspace.HashProjectFile(path) };
        var payload = new JsonArray();
        foreach (var name in new[] { "assembly.glb", "design.json", "metadata.json", "provenance.json" })
            payload.Add(new JsonObject { ["path"] = name, ["sha256"] = workspace.HashProjectFile(folder + name), ["bytes"] = new FileInfo(workspace.Resolve(folder + name)).Length });
        var sdkRoot="4.5/scripts/addons_core/io_scene_gltf2/";
        Directory.CreateDirectory(workspace.Resolve(sdkRoot));File.WriteAllText(workspace.Resolve(sdkRoot+"__init__.py"),"# synthetic SDK; never imported\n");
        var sdkFiles=new JsonArray(new JsonObject{["path"]="__init__.py",["sha256"]=workspace.HashProjectFile(sdkRoot+"__init__.py"),["bytes"]=new FileInfo(workspace.Resolve(sdkRoot+"__init__.py")).Length});
        var toolchain=new JsonObject{["blenderSha256"]=workspace.HashProjectFile("blender.exe"),["gltfFiles"]=sdkFiles};
        workspace.Save(folder + "package.json", new JsonObject { ["schemaVersion"] = "bubo-schematic-export-package/v1", ["components"] = components.DeepClone(), ["files"] = payload, ["numerics"] = numerics.DeepClone(),["toolchain"]=toolchain });
        var sdkPackage=workspace.Load<JsonObject>(folder+"package.json");
        Check(()=>MechanicalAdmission.VerifyToolchain(sdkPackage,workspace.Resolve("blender.exe"),workspace.HashProjectFile("blender.exe")),"exact synthetic selected SDK admitted without execution");
        File.WriteAllText(workspace.Resolve(sdkRoot+"unexpected.py"),"# unreviewed\n");
        Check(()=>MechanicalAdmission.VerifyToolchain(sdkPackage,workspace.Resolve("blender.exe"),workspace.HashProjectFile("blender.exe")),"new unreviewed SDK file rejects before native execution",true);
        File.Delete(workspace.Resolve(sdkRoot+"unexpected.py"));
        var sdkOriginal=File.ReadAllBytes(workspace.Resolve(sdkRoot+"__init__.py"));File.AppendAllText(workspace.Resolve(sdkRoot+"__init__.py"),"# changed\n");
        Check(()=>MechanicalAdmission.VerifyToolchain(sdkPackage,workspace.Resolve("blender.exe"),workspace.HashProjectFile("blender.exe")),"modified SDK file rejects before native execution",true);
        File.WriteAllBytes(workspace.Resolve(sdkRoot+"__init__.py"),sdkOriginal);
        var handoff = new JsonObject { ["schemaVersion"] = MechanicalAdmission.Schema, ["components"] = components.DeepClone(), ["bindings"] = new JsonObject { ["nodes"] = glb["nodes"]!.DeepClone(), ["skins"] = glb["skins"]?.DeepClone() ?? new JsonArray() } };
        foreach (var name in new[] { "package", "model", "design", "metadata", "provenance" })
            handoff[name] = Reference(folder + (name == "model" ? "assembly.glb" : name + ".json"));
        foreach (var name in new[] { "technicalReview", "independentReview" })
        {
            workspace.Save(folder + name + ".json", new JsonObject { ["decision"] = "approved", ["reviewer"] = "synthetic-" + name, ["independent"] = name == "independentReview", ["modelSha256"] = handoff["model"]!["sha256"]!.DeepClone(), ["packageSha256"] = handoff["package"]!["sha256"]!.DeepClone(), ["simulationOnly"] = true });
            handoff[name] = Reference(folder + name + ".json");
        }
        var sourceState = new JsonObject();
        foreach (var name in new[] { "design", "parts", "assembly", "export" })
        {
            var statePath = folder + "state/" + (name == "design" ? "manifest" : name == "export" ? "exports" : name) + ".json";
            workspace.Save(statePath, new { simulationOnly = true, latestState = name });
            sourceState[name] = Reference(statePath);
        }
        handoff["sourceState"] = sourceState;
        void Admit(JsonObject candidate) => MechanicalAdmission.Validate(candidate, glb, folder + "assembly.glb", workspace);
        Check(() => Admit(handoff), "source-bound reviewed mechanical handoff with zero rigs admitted");
        foreach (var change in new Action<JsonObject>[] {
            h => h["model"]!["sha256"] = new string('0', 64),
            h => h["components"]!.AsArray().Clear(),
            h => h["bindings"]!["nodes"]![0]!["name"] = "substituted",
            h => h["technicalReview"] = h["independentReview"]!.DeepClone(),
            h => h.Remove("sourceState"),
            h => h["sourceState"]!["export"]!["sha256"] = new string('0',64),
            h => h["callback"] = "never-executed" })
        {
            var invalid = handoff.DeepClone().AsObject(); change(invalid);
            Check(() => Admit(invalid), "changed source, inventory, bindings or unsupported handoff field rejected", true);
        }
        Check(() => MechanicalAdmission.ValidateRestBindings(handoff, glb, design, metadata), "exact mechanical rest hierarchy admitted");
        var moved = glb.DeepClone().AsObject(); moved["nodes"]![0]!["translation"] = new JsonArray(1, 0, 0);
        Check(() => MechanicalAdmission.ValidateRestBindings(handoff, moved, design, metadata), "changed approved rest pivot rejected", true);
        foreach(var origin in new[]{0.0,1000.0,.001})
        {
            var preciseHandoff = handoff.DeepClone().AsObject(); preciseHandoff["bindings"]!["nodes"]![0]!["translation"] = new JsonArray(origin,0,0);
            var preciseCandidate = glb.DeepClone().AsObject(); preciseCandidate["nodes"]![0]!["translation"] = new JsonArray(origin+1e-5,0,0);
            Check(() => MechanicalAdmission.ValidateRestBindings(preciseHandoff,preciseCandidate,design,metadata), "10um rest shift rejected under1um tolerance at origin " + origin, true);
        }
        var incompatibleMetadata = metadata.DeepClone().AsObject(); incompatibleMetadata["numerics"]!["linearTolerance"] = .001;
        Check(() => MechanicalAdmission.Numerics(design,incompatibleMetadata), "metadata cannot widen approved design numerical policy", true);
        var normalizedHandoff = handoff.DeepClone().AsObject(); var normalizedCandidate = glb.DeepClone().AsObject();
        var q = Math.Sqrt(.5);
        normalizedHandoff["bindings"]!["nodes"]![0]!["rotation"] = new JsonArray(0,0,q*(1+1e-8),q*(1+1e-8));
        normalizedCandidate["nodes"]![0]!["rotation"] = new JsonArray(0,0,q,q);
        Check(()=>MechanicalAdmission.ValidateRestBindings(normalizedHandoff,normalizedCandidate,design,metadata), "unit quaternion normalization does not become a scale change");
        var mixed = JsonNode.Parse("""{"nodes":[{"name":"root","children":[1,2,4,5]},{"name":"rigid","mesh":0},{"name":"flex-a","mesh":0,"skin":0},{"name":"bone-a"},{"name":"rig-a","children":[3]},{"name":"flex-b","mesh":0,"skin":1},{"name":"rig-b","children":[7]},{"name":"bone-b"}],"skins":[{"joints":[3]},{"joints":[7]}]}""")!.AsObject();
        var mixedHandoff = new JsonObject { ["components"] = new JsonArray(
            new JsonObject { ["instanceId"] = "rigid", ["partId"] = "part", ["node"] = "rigid", ["kind"] = "rigid", ["rigNode"] = null },
            new JsonObject { ["instanceId"] = "flex-a", ["partId"] = "flex", ["node"] = "flex-a", ["kind"] = "flexible", ["rigNode"] = "rig-a" },
            new JsonObject { ["instanceId"] = "flex-b", ["partId"] = "flex", ["node"] = "flex-b", ["kind"] = "flexible", ["rigNode"] = "rig-b" }) };
        Check(() => MechanicalAdmission.ValidateComponents(mixedHandoff, mixed), "mixed rigid and multiple independently owned rigs admitted");
        var foreign = mixed.DeepClone().AsObject(); foreign["skins"]![0]!["joints"] = new JsonArray(7);
        Check(() => MechanicalAdmission.ValidateComponents(mixedHandoff, foreign), "foreign-rig joint ownership rejected", true);
        var accidental = mixed.DeepClone().AsObject(); accidental["nodes"]![1]!["skin"] = 0;
        Check(() => MechanicalAdmission.ValidateComponents(mixedHandoff, accidental), "accidental rigid skin rejected", true);
        workspace.Save(folder + "handoff.json", handoff);
        workspace.Save(folder + "recipe.json", recipe);
        return passed;
    }
}
