using System.Text.Json.Nodes;
using Hephaera.AssetProduction;
using static Hephaera.ModelAnimation.Application.MotionValidation;

namespace Hephaera.ModelAnimation.Application;

/// <summary>Admission of a reviewed assembly; this never converts readiness into motion acceptance.</summary>
public static class MechanicalAdmission
{
    public sealed record NumericalPolicy(double Linear, double AngularRadians, double MinimumFeature, double MaxCoordinate)
    {
        public double ScaleTolerance => 1e-6;
    }
    public static NumericalPolicy Numerics(JsonObject design, JsonObject metadata)
    {
        var values = design["numerics"]?.AsObject() ?? throw new InvalidOperationException("Approved design numerical policy required");
        if (!JsonNode.DeepEquals(values, metadata["numerics"])) throw new InvalidOperationException("Mechanical metadata numerical policy differs from the bound approved design");
        Fields(values, "linearTolerance", "angularToleranceDegrees", "minimumFeatureSize", "maxCoordinateMagnitude");
        var linear = Number(values, "linearTolerance", double.Epsilon, 1_000_000);
        var angular = Number(values, "angularToleranceDegrees", double.Epsilon, 1);
        var feature = Number(values, "minimumFeatureSize", double.Epsilon, 1_000_000);
        var maximum = Number(values, "maxCoordinateMagnitude", feature, 1_000_000);
        if (linear >= feature) throw new InvalidOperationException("Mechanical tolerance must remain below the approved minimum feature");
        return new(linear, angular * Math.PI / 180, feature, maximum);
    }
    public const string Schema = "bubo-mechanical-assembly-handoff/v1";
    public static readonly string[] References = ["package", "model", "design", "metadata", "provenance", "technicalReview", "independentReview"];
    public static JsonObject VerifyToolchain(JsonObject package,string blender,string expectedBlenderSha256,CancellationToken cancellation=default)
    {
        var source=package["toolchain"]?.AsObject()??throw new InvalidOperationException("Mechanical source package requires its exact glTF toolchain");
        if(Text(source,"blenderSha256")!=expectedBlenderSha256)throw new InvalidOperationException("Mechanical motion must use the reviewed source Blender executable");
        var files=Array(source,"gltfFiles").Select(node=>node!.AsObject()).Select(file=>new BundledGltfFile(Text(file,"path"),Text(file,"sha256"),file["bytes"]!.GetValue<long>())).ToArray();
        BundledGltfSdk.Verify(blender,expectedBlenderSha256,files,cancellation);
        return new JsonObject{["blenderPath"]=blender,["blenderSha256"]=expectedBlenderSha256,["gltfFiles"]=source["gltfFiles"]!.DeepClone()};
    }

    public static Dictionary<string, string> Validate(JsonObject handoff, JsonObject glb, string model, IWorkspace workspace)
    {
        Fields(handoff, "schemaVersion", "package", "model", "design", "metadata", "provenance", "technicalReview", "independentReview", "components", "bindings", "sourceState", "humanAcceptance");
        if (Text(handoff, "schemaVersion") != Schema)
            throw new InvalidOperationException("Mechanical assembly requires its versioned reviewed handoff");
        NoExcludedProviders(handoff);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in References)
        {
            var reference = handoff[name]?.AsObject() ?? throw new InvalidOperationException("Missing mechanical handoff " + name);
            Fields(reference, "path", "sha256");
            var path = Relative(workspace, Text(reference, "path"));
            var hash = Text(reference, "sha256");
            Hash(hash);
            if (workspace.HashProjectFile(path) != hash)
                throw new InvalidOperationException("Changed mechanical handoff source: " + name);
            if (name == "model" && path != model)
                throw new InvalidOperationException("Mechanical handoff substituted the selected model");
            sources[path] = hash;
        }
        var sourceState = handoff["sourceState"]?.AsObject() ?? throw new InvalidOperationException("Current mechanical source-state fence required");
        Fields(sourceState, "design", "parts", "assembly", "export");
        var stateManifest = sourceState["design"]?.AsObject() ?? throw new InvalidOperationException("Missing mechanical design-state reference");
        var stateDesign = Relative(workspace, Text(stateManifest, "path"));
        if (!stateDesign.EndsWith("/manifest.json", StringComparison.Ordinal)) throw new InvalidOperationException("Mechanical design state must identify its manifest");
        var stateRoot = stateDesign[..^"manifest.json".Length];
        foreach (var name in new[] { "design", "parts", "assembly", "export" })
        {
            var reference = sourceState[name]?.AsObject() ?? throw new InvalidOperationException("Incomplete mechanical source-state fence");
            Fields(reference, "path", "sha256");
            var path = Relative(workspace, Text(reference, "path"));
            var expectedPath = name == "design" ? stateDesign : stateRoot + (name == "export" ? "exports" : name) + ".json";
            var hash = Text(reference, "sha256"); Hash(hash);
            if (path != expectedPath || new FileInfo(workspace.Resolve(path)).Length is < 2 or > 8 * 1024 * 1024 || workspace.HashProjectFile(path) != hash)
                throw new InvalidOperationException("Latest mechanical source state invalidated this handoff: " + name);
            sources[path] = hash;
        }
        foreach (var name in new[] { "technicalReview", "independentReview" })
        {
            var file = workspace.Resolve(Text(handoff[name]!.AsObject(), "path"));
            if (new FileInfo(file).Length is < 2 or > 8 * 1024 * 1024)
                throw new InvalidOperationException("Missing/oversized mechanical review");
            var review = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if (review["decision"]?.GetValue<string>() != "approved" ||
                review["modelSha256"]?.GetValue<string>() != handoff["model"]!["sha256"]!.GetValue<string>() ||
                review["packageSha256"]?.GetValue<string>() != handoff["package"]!["sha256"]!.GetValue<string>() ||
                string.IsNullOrWhiteSpace(review["reviewer"]?.GetValue<string>()))
                throw new InvalidOperationException("Current source-bound mechanical " + name + " approval required");
            if (name == "independentReview" && review["independent"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Mechanical handoff requires an explicit independent review");
        }
        if (Text(handoff["technicalReview"]!.AsObject(), "path") == Text(handoff["independentReview"]!.AsObject(), "path"))
            throw new InvalidOperationException("Mechanical technical and independent decisions must remain separate records");
        var packagePath = Text(handoff["package"]!.AsObject(), "path");
        var packageFile = workspace.Resolve(packagePath);
        if (new FileInfo(packageFile).Length is < 2 or > 8 * 1024 * 1024)
            throw new InvalidOperationException("Missing/oversized mechanical package");
        var package = JsonNode.Parse(File.ReadAllText(packageFile))!.AsObject();
        if (Text(package, "schemaVersion") != "bubo-schematic-export-package/v1" || !JsonNode.DeepEquals(package["components"], handoff["components"]))
            throw new InvalidOperationException("Mechanical handoff does not match the sealed component inventory");
        JsonObject ReadBoundJson(string name)
        {
            var file = workspace.Resolve(Text(handoff[name]!.AsObject(), "path"));
            if (new FileInfo(file).Length is < 2 or > 8 * 1024 * 1024) throw new InvalidOperationException("Missing/oversized mechanical " + name);
            return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        }
        var approvedDesign = ReadBoundJson("design");
        _ = Numerics(approvedDesign, ReadBoundJson("metadata"));
        if (!JsonNode.DeepEquals(package["numerics"], approvedDesign["numerics"])) throw new InvalidOperationException("Sealed package changed approved numerical policy");
        var packageRoot = packagePath[..(packagePath.LastIndexOf('/') + 1)];
        var entries = Array(package, "files").Select(node => node!.AsObject()).ToArray();
        if (entries.Length is < 1 or > 16384) throw new InvalidOperationException("Invalid bounded mechanical payload inventory");
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long payloadBytes = 0;
        foreach (var entry in entries)
        {
            var relative = Text(entry, "path");
            if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidOperationException("Mechanical package payload path escapes its package");
            var path = Relative(workspace, packageRoot + relative);
            var hash = Text(entry, "sha256"); Hash(hash);
            if (entry["bytes"] is not JsonValue count || !count.TryGetValue<long>(out var bytes) || bytes < 1 || bytes > 17_179_869_184 - payloadBytes || new FileInfo(workspace.Resolve(path)).Length != bytes || !payload.TryAdd(path, hash) || workspace.HashProjectFile(path) != hash)
                throw new InvalidOperationException("Changed/ambiguous mechanical package payload");
            payloadBytes += bytes;
            sources[path] = hash;
        }
        foreach (var name in new[] { "model", "design", "metadata", "provenance" })
        {
            var reference = handoff[name]!.AsObject();
            if (!payload.TryGetValue(Text(reference, "path"), out var hash) || hash != Text(reference, "sha256"))
                throw new InvalidOperationException("Mechanical source is outside its sealed package: " + name);
        }
        var bindings = handoff["bindings"]?.AsObject() ?? throw new InvalidOperationException("Missing measured mechanical bindings");
        Fields(bindings, "nodes", "skins");
        if (!JsonNode.DeepEquals(bindings["nodes"], glb["nodes"]) || !JsonNode.DeepEquals(bindings["skins"], glb["skins"] ?? new JsonArray()))
            throw new InvalidOperationException("Mechanical handoff does not describe actual GLB node and skin bindings");
        ValidateComponents(handoff, glb);
        return sources;
    }

    public static void ValidateComponents(JsonObject handoff, JsonObject glb)
    {
        var nodes = Array(glb, "nodes").Select(node => node!.AsObject()).ToArray();
        var names = nodes.Select(node => Text(node, "name")).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidOperationException("Mechanical node names must be globally unambiguous");
        var components = Array(handoff, "components").Select(node => node!.AsObject()).ToArray();
        if (components.Length is < 1 or > 4096 || components.Select(c => Text(c, "instanceId")).Distinct(StringComparer.Ordinal).Count() != components.Length)
            throw new InvalidOperationException("Invalid mechanical physical component inventory");
        var declaredMeshes = new HashSet<string>(StringComparer.Ordinal);
        var declaredRigs = new HashSet<string>(StringComparer.Ordinal);
        var usedSkins = new HashSet<int>();
        var ownedJoints = new HashSet<int>();
        var skins = (glb["skins"]?.AsArray() ?? []).Select(node => node!.AsObject()).ToArray();
        foreach (var component in components)
        {
            Fields(component, "instanceId", "partId", "node", "kind", "rigNode");
            _ = Id(Text(component, "instanceId")); _ = Id(Text(component, "partId"));
            var name = Text(component, "node");
            var index = System.Array.IndexOf(names, name);
            if (index < 0 || nodes[index]["mesh"] is null || !declaredMeshes.Add(name))
                throw new InvalidOperationException("Missing or multiply-owned mechanical component mesh");
            if (Text(component, "kind") == "rigid")
            {
                if (nodes[index]["skin"] is not null || component["rigNode"] is not null)
                    throw new InvalidOperationException("Rigid mechanical component acquired an accidental skin");
            }
            else if (Text(component, "kind") == "flexible")
            {
                var rig = Text(component, "rigNode");
                if (!names.Contains(rig) || !declaredRigs.Add(rig) || nodes[index]["skin"] is not JsonValue skinValue || !skinValue.TryGetValue<int>(out var skinIndex) || skinIndex < 0 || skinIndex >= skins.Length || !usedSkins.Add(skinIndex))
                    throw new InvalidOperationException("Flexible mechanical component requires a separately owned real skin and rig");
                var joints = Array(skins[skinIndex], "joints").Select(j => j!.GetValue<int>()).ToArray();
                if (joints.Length == 0 || joints.Distinct().Count() != joints.Length || joints.Any(j => j < 0 || j >= nodes.Length || !ownedJoints.Add(j)))
                    throw new InvalidOperationException("Invalid owned mechanical joints");
                var rigIndex = System.Array.IndexOf(names, rig);
                var descendants = new HashSet<int>();
                void Visit(int node)
                {
                    if (node < 0 || node >= nodes.Length || !descendants.Add(node))
                        throw new InvalidOperationException("Invalid mechanical rig hierarchy");
                    foreach (var child in nodes[node]["children"]?.AsArray() ?? []) Visit(child!.GetValue<int>());
                }
                Visit(rigIndex);
                if (joints.Any(j => !descendants.Contains(j)))
                    throw new InvalidOperationException("Mechanical joints belong to another rig");
            }
            else throw new InvalidOperationException("Unknown mechanical component kind");
        }
        if (!declaredMeshes.SetEquals(nodes.Where(n => n["mesh"] is not null).Select(n => Text(n, "name"))) || usedSkins.Count != skins.Length)
            throw new InvalidOperationException("Mechanical export contains missing, fused or undeclared components/skins");
    }

    public static void ValidateRestBindings(JsonObject handoff, JsonObject glb, JsonObject design, JsonObject metadata)
    {
        var numerics = Numerics(design, metadata);
        static Dictionary<string, (string? Parent, double[] Matrix, bool Mesh, string[]? Joints)> Describe(JsonObject document)
        {
            var nodes = Array(document, "nodes").Select(node => node!.AsObject()).ToArray();
            var names = nodes.Select(node => Text(node, "name")).ToArray();
            var parents = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in nodes)
                foreach (var child in node["children"]?.AsArray() ?? [])
                {
                    var index = child!.GetValue<int>();
                    if (index < 0 || index >= nodes.Length || !parents.TryAdd(names[index], Text(node, "name")))
                        throw new InvalidOperationException("Invalid mechanical rest hierarchy");
                }
            var result = new Dictionary<string, (string?, double[], bool, string[]?)>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                double[] Numbers(string field, double[] fallback) => node[field]?.AsArray().Select(value => value is JsonValue number && number.GetValueKind()==System.Text.Json.JsonValueKind.Number && double.TryParse(number.ToJsonString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var parsed) ? parsed : throw new InvalidOperationException("Invalid mechanical transform number")).ToArray() ?? fallback;
                var matrix = Numbers("matrix", []);
                if (matrix.Length == 0)
                {
                    var t = Numbers("translation", [0, 0, 0]); var q = Numbers("rotation", [0, 0, 0, 1]); var s = Numbers("scale", [1, 1, 1]);
                    if (t.Length != 3 || q.Length != 4 || s.Length != 3) throw new InvalidOperationException("Invalid mechanical node transform");
                    var length = Math.Sqrt(q.Sum(value=>value*value));
                    if (!double.IsFinite(length)||Math.Abs(length-1)>1e-6) throw new InvalidOperationException("Unsupported non-unit mechanical rotation");
                    q = q.Select(value=>value/length).ToArray();
                    var x = q[0]; var y = q[1]; var z = q[2]; var w = q[3];
                    matrix = [(1-2*y*y-2*z*z)*s[0],(2*x*y+2*z*w)*s[0],(2*x*z-2*y*w)*s[0],0,
                        (2*x*y-2*z*w)*s[1],(1-2*x*x-2*z*z)*s[1],(2*y*z+2*x*w)*s[1],0,
                        (2*x*z+2*y*w)*s[2],(2*y*z-2*x*w)*s[2],(1-2*x*x-2*y*y)*s[2],0,t[0],t[1],t[2],1];
                }
                if (matrix.Length != 16 || matrix.Any(value => !double.IsFinite(value))) throw new InvalidOperationException("Invalid mechanical rest matrix");
                string[]? joints = null;
                if (node["skin"] is not null)
                {
                    var skin = document["skins"]![node["skin"]!.GetValue<int>()]!.AsObject();
                    joints = Array(skin, "joints").Select(index => names[index!.GetValue<int>()]).Order(StringComparer.Ordinal).ToArray();
                }
                if (!result.TryAdd(Text(node, "name"), (parents.GetValueOrDefault(Text(node, "name")), matrix, node["mesh"] is not null, joints)))
                    throw new InvalidOperationException("Duplicate mechanical rest node");
            }
            return result;
        }
        var expected = Describe(handoff["bindings"]!.AsObject()); var actual = Describe(glb);
        if (!expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actual.Keys)) throw new InvalidOperationException("Mechanical rest node inventory changed");
        foreach (var (name, source) in expected)
        {
            var target = actual[name];
            if (source.Parent != target.Parent || source.Mesh != target.Mesh || (source.Joints is null) != (target.Joints is null) || source.Joints is not null && !source.Joints.SequenceEqual(target.Joints!) ||
                !TransformMatches(source.Matrix, target.Matrix, numerics))
                throw new InvalidOperationException("Mechanical rest hierarchy, pivot or owned skin changed: " + name);
        }
    }
    public static bool TransformMatches(double[] source, double[] target, NumericalPolicy n)
    {
        if (source.Length != 16 || target.Length != 16 || source.Concat(target).Any(value=>!double.IsFinite(value))) return false;
        double Distance(double[] a, double[] b) => Math.Sqrt(a.Zip(b).Sum(pair => (pair.First-pair.Second)*(pair.First-pair.Second)));
        if (Distance(source[12..15], target[12..15]) > n.Linear) return false;
        double trace = 0;
        List<double[]> sourceBasis = []; List<double[]> targetBasis = [];
        for (var axis = 0; axis < 3; axis++)
        {
            var first = source[(axis*4)..(axis*4+3)]; var second = target[(axis*4)..(axis*4+3)];
            var a = Math.Sqrt(first.Sum(value=>value*value)); var b = Math.Sqrt(second.Sum(value=>value*value));
            if (a <= 0 || b <= 0 || Math.Abs(a-b) > n.ScaleTolerance) return false;
            trace += first.Zip(second).Sum(pair=>pair.First*pair.Second)/(a*b);
            sourceBasis.Add(first.Select(value=>value/a).ToArray()); targetBasis.Add(second.Select(value=>value/b).ToArray());
        }
        var orthogonality = Math.Min(1e-6,Math.Sin(n.AngularRadians));
        foreach(var basis in new[]{sourceBasis,targetBasis})
            foreach(var (first,second) in new[]{(0,1),(0,2),(1,2)})
                if(Math.Abs(basis[first].Zip(basis[second]).Sum(pair=>pair.First*pair.Second))>orthogonality) return false;
        if (Math.Acos(Math.Clamp((trace-1)/2,-1,1)) > n.AngularRadians) return false;
        return new[]{3,7,11}.All(index=>source[index]==0&&target[index]==0)&&source[15]==1&&target[15]==1;
    }
}
