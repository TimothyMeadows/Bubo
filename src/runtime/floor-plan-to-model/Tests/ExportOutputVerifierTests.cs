using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.FloorPlan.Domain;
using Bubo.FloorPlan.Infrastructure;
using Hephaera.AssetProduction;

namespace Bubo.FloorPlan.Tests;

/// <summary>Deliberately simulated fixed-process protocol receipts. No Blender execution or geometry/image-quality verdict.</summary>
public static class ExportOutputVerifierTests
{
    public static void Run()
    {
        Check.Case("export output protocol: simulated producer validates exact envelope and immutable files", () =>
        {
            var f = new Fixture(); var report = f.Produce(); f.Verify(report);
            foreach (var change in new Action<JsonObject>[]
            {
                n => n["jobSha256"] = H('f'), n => n["humanModelAccepted"] = true, n => n["runtimeAccepted"] = true,
                n => n["blenderVersion"] = "4.6.0", n => n["representation"] = "visual", n => n["unknown"] = 1,
                n => n["identities"]!["runtimeSha256"] = H('d'), n => n["bundle"]!["export.py"] = H('d'),
                n => n["toolchain"]!["files"]![0]!["sha256"] = H('e'), n => n["limits"]!["maxNodes"] = 1,
                n => n["measured"]!["sourceUnchanged"] = false, n => n["measured"]!["geometryChecks"] = 1,
                n => n["outputs"]![0]!["sha256"] = H('f'), n => n["outputs"]![0]!["bytes"] = 999
            }) f.RejectMutation(report, change);
            f.Append(report, "visual.glb", "changed"); Check.Throws<InvalidOperationException>(() => f.Verify(report));
        });
        Check.Case("export output protocol: missing/extra files, directories, job bytes and input hashes fail", () =>
        {
            var f = new Fixture(); var report = f.Produce(); var extra = f.Output(report, "extra.txt"); File.WriteAllText(extra, "unlisted"); Check.Throws<InvalidOperationException>(() => f.Verify(report)); File.Delete(extra);
            Directory.CreateDirectory(extra); Check.Throws<InvalidOperationException>(() => f.Verify(report)); Directory.Delete(extra);
            File.AppendAllText(f.JobPath(report), " "); Check.Throws<InvalidOperationException>(() => f.Verify(report));
            var changed = new Fixture(); var other = changed.Produce(); File.AppendAllText(changed.PlanFile, " "); Check.Throws<InvalidOperationException>(() => changed.Verify(other));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Check.Throws<OperationCanceledException>(() => changed.Verify(other, cancelled.Token));
        });
        Check.Case("export output protocol: source mesh counts/bounds/material assignments and angular policy are mandatory", () =>
        {
            var f = new Fixture(); var report = f.Produce();
            foreach (var change in new Action<JsonObject>[]
            {
                n => n["measured"]!["visual"]!["meshes"]![0]!["id"] = "invented",
                n => n["measured"]!["visual"]!["meshes"]![0]!["observedTriangleCount"] = 3,
                n => n["measured"]!["visual"]!["meshes"]![0]!["minimum"]!["x"] = .25,
                n => n["measured"]!["visual"]!["meshes"]![0]!["materialTriangleCounts"]![f.Material.Id] = 3,
                n => n["measured"]!["visual"]!["maxSourceFaceAngularErrorDegrees"] = f.Plan.Numerics.AngularToleranceDegrees * 2,
                n => n["measured"]!["visual"]!["materials"]![0]!["roughness"] = .987654321,
                n => n["measured"]!["visual"]!["rawCounts"]!["nodes"] = 999,
                n => n["measured"]!["visual"]!["nodes"]![0]!["worldMatrix"]![0] = -1,
                n => n["measured"]!["visual"]!["nodes"]![0]!["parentId"] = "unexpected",
                n => n["measured"]!["source"]!["visual"]!["meshes"]![0]!["observedVertexCount"] = 12
            }) f.RejectMutation(report, change);
        });
        Check.Case("export output protocol: separate simulated import requires truthful normals/clearance/source basis", () =>
        {
            var f = new Fixture(); var report = f.Verification("visual"); f.Verify(report);
            foreach (var change in new Action<JsonObject>[]
            {
                n => n["representation"] = "collision", n => n["measured"]!["cleanProcessImport"] = false,
                n => n["measured"]!["newNavigationMeasurement"] = true,
                n => n["measured"]!["nativeIndexedTopologyBasis"] = "reimported-index-layout",
                n => n["measured"]!["sourceClearanceEvidence"]!["minimumRouteClearance"] = 1,
                n => n["measured"]!["imported"]!["normalDiagnostics"]!["cornerNormals"] = 9,
                n => n["measured"]!["imported"]!["normalDiagnostics"]!["maxAngularErrorDegrees"] = 2,
                n => n["measured"]!["clearancePreservation"]!["newNavigationMeasurement"] = true,
                n => n["measured"]!["clearancePreservation"]!["sourceApertureIds"] = new JsonArray(),
                n => n["measured"]!["clearancePreservation"]!["maxSurfacePointErrorBound"] = .1,
                n => n["outputs"] = new JsonArray(new JsonObject { ["path"] = "verification.json", ["sha256"] = H('a'), ["bytes"] = 1 })
            }) f.RejectMutation(report, change);
        });
        Check.Case("export output protocol: complete simulated views bind source cameras/sections/fixed CPU and actual PNG", () =>
        {
            var f = new Fixture(); var report = f.Views(); f.Verify(report);
            foreach (var change in new Action<JsonObject>[]
            {
                n => n["measured"]!["requiredViewCount"] = 1,
                n => n["measured"]!["views"]![0]!["kind"] = "pretty-oblique",
                n => n["measured"]!["views"]![0]!["camera"]!["direction"] = new JsonArray(1, 0, 0),
                n => n["measured"]!["views"]![0]!["camera"]!["position"]![0] = 100,
                n => n["measured"]!["views"]![5]!["sourceId"] = "wrong-floor",
                n => n["measured"]!["views"]![5]!["clipPlanes"]![0]!["point"]![2] = 999,
                n => n["measured"]!["views"]![0]!["meshIds"] = new JsonArray(),
                n => n["measured"]!["renderSettings"]!["device"] = "GPU",
                n => n["measured"]!["renderSettings"]!["samples"] = 1,
                n => n["measured"]!["collisionCheck"]!["sha256"] = H('f')
            }) f.RejectMutation(report, change);
            var bad = f.Output(report, "view-000.png"); File.WriteAllText(bad, "not a PNG"); f.RefreshOutput(report, "view-000.png"); Check.Throws<InvalidOperationException>(() => f.Verify(report));
        });
        Check.Case("export output protocol: malformed/stale prior collision receipt blocks views despite passed flag", () =>
        {
            var f = new Fixture(); var report = f.Views(); var prior = report.Job.Inputs.CollisionVerification!;
            var node = JsonNode.Parse(File.ReadAllBytes(prior.Path))!.AsObject(); node["measured"]!["cleanProcessImport"] = false; Write(prior.Path, node);
            // Refresh hashes in both the job and view envelope: semantic admission must still fail.
            var updated = f.Reference(prior.Path); var job = report.Job with { Inputs = report.Job.Inputs with { CollisionVerification = updated } };
            var result = report with { Job = job }; f.SaveJob(result); var view = JsonNode.Parse(File.ReadAllBytes(f.ReportPath(result)))!.AsObject(); view["inputs"] = JsonSerializer.SerializeToNode(job.Inputs, StrictPlanJson.Options); view["jobSha256"] = result.JobSha; view["measured"]!["collisionCheck"]!["sha256"] = updated.Sha256; Write(f.ReportPath(result), view);
            Check.Throws<InvalidOperationException>(() => f.Verify(result));
        });
        Check.Case("export output protocol NV-01: self-consistent prior verification cannot widen approved source work", () =>
        {
            var f = new Fixture(); var views = f.Views(); f.Verify(views);
            var prior = views.Job.Inputs.CollisionVerification!;
            var priorJobPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(prior.Path))!, "job.json");
            var priorJob = ExportJson.Read<ExportNativeJob>(priorJobPath, 1_048_576, default);
            var raised = priorJob.Limits with { MaxGeometryChecks = checked(f.Plan.Budgets.MaxGeometryChecks + 1) };
            // Both immutable-identity simulations are refreshed deliberately:
            // only source-policy admission may reject this internally consistent receipt.
            Write(priorJobPath, priorJob with { Limits = raised });
            var priorReport = ExportJson.Read<ExportNativeReport>(prior.Path, 16_777_216, default);
            Write(prior.Path, priorReport with { Limits = raised, JobSha256 = f.Reference(priorJobPath).Sha256 });
            var updated = f.Reference(prior.Path);
            var currentJob = views.Job with { Inputs = views.Job.Inputs with { CollisionVerification = updated } };
            var current = views with { Job = currentJob }; f.SaveJob(current);
            var viewEnvelope = JsonNode.Parse(File.ReadAllBytes(f.ReportPath(current)))!.AsObject();
            viewEnvelope["inputs"] = JsonSerializer.SerializeToNode(currentJob.Inputs, StrictPlanJson.Options);
            viewEnvelope["jobSha256"] = current.JobSha;
            viewEnvelope["measured"]!["collisionCheck"]!["sha256"] = updated.Sha256;
            Write(f.ReportPath(current), viewEnvelope);
            var failure = Check.Throws<InvalidOperationException>(() => f.Verify(current));
            Check.True(failure.Message.Contains("widens approved source", StringComparison.Ordinal), "NV-01 must fail the approved source allowance gate");
        });
    }

    static string H(char c) => new(c, 64);
    static void Write<T>(string path, T value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, StrictPlanJson.Options)); }
    sealed record Result(string Directory, ExportNativeJob Job, string ReportName)
    {
        internal string JobSha { get; set; } = "";
    }
    sealed class Fixture
    {
        internal readonly BuildingPlan Plan = ConstructionPlanExamples.SingleStorey();
        internal readonly ConstructionScene Scene;
        internal readonly ConstructionMaterial Material;
        readonly Workspace workspace;
        readonly string root;
        readonly ExportNativeFile blend, construction, constructionReport, plan, approval;
        readonly SortedDictionary<string, string> bundle = new(StringComparer.Ordinal);
        readonly GltfToolchain toolchain;
        readonly ExportNativeLimits limits = new(16_777_216, 1_048_576, 16_777_216, 1000, 1000, 100, 100, 100, 100, 100, 100_000, 10, 768);
        ExportNativeFile? package;
        int sequence;
        internal string PlanFile => plan.Path;
        internal Fixture()
        {
            var repository = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(repository, "Bubo.slnx"))) repository = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException("Fixture root missing");
            root = Path.Combine(repository, ".bubo", "export-output-protocol", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); workspace = new(root);
            Material = new() { Id = Plan.Materials[0].Id, Name = Plan.Materials[0].Name, Color = Plan.Materials[0].Color, Roughness = Plan.Materials[0].Roughness, Metallic = Plan.Materials[0].Metallic, Transmission = 0, Ior = 1 };
            var mesh = new ConstructionMesh { Id = "visual-synthetic", SemanticId = Plan.Storeys[0].Id, StoreyId = Plan.Storeys[0].Id, Kind = "floor-slab", Representation = "visual", CollisionRole = "none", MaterialId = Material.Id, ParentAnchorId = null, TriangleMaterialIds = null, Vertices = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], Triangles = [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3] };
            Scene = new() { SchemaVersion = "bubo-floor-plan-construction-scene/v1", PlanId = Plan.Id, LinearTolerance = Plan.Numerics.LinearTolerance, Materials = [Material], Meshes = [mesh, mesh with { Id = "collision-synthetic", Representation = "collision", CollisionRole = "static" }], Anchors = [new() { Id = "floor-anchor-synthetic", SemanticId = mesh.StoreyId, StoreyId = mesh.StoreyId, Kind = "floor-root", Position = new(), Axis = new(0, 0, 1), RotationRadians = 0 }], Metrics = new() { MeshCount = 2, VertexCount = 8, TriangleCount = 8, VisualTriangleCount = 4, CollisionTriangleCount = 4, GeometryChecks = 1, Minimum = new(), Maximum = new(1, 1, 1) }, Clearance = new() { Routes = [], DoorSweeps = [], MinimumRouteClearance = 0 } };
            foreach (var name in new[] { "admission.py", "review.py", "export.py", "export_admission.py", "export_geometry.py", "export_views.py" }) bundle.Add(name, H('a'));
            toolchain = new(Path.Combine(root, "synthetic-unexecuted-blender"), H('b'), Path.Combine(root, "synthetic-unloaded-module"), [new("__init__.py", H('c'), 1)], H('d'));
            plan = Save("source/plan.json", Plan); blend = TextFile("source/building.blend", "SIMULATED source bytes: never loaded by Blender"); constructionReport = TextFile("source/construction-report.json", "{\"synthetic\":true}"); approval = TextFile("source/design-approval.json", "{\"synthetic\":true}");
            construction = Save("source/job.json", new ConstructionBlenderJob("bubo-floor-plan-blender-job/v1", "construct", root, Path.Combine(root, "source", "historical-output"), Scene, new(plan.Sha256, H('a'), H('f')), new() { ["author.py"] = H('a'), ["admission.py"] = H('a') }, new(1000, 1000, 100, 16_777_216, 1_048_576)));
        }
        internal ExportNativeFile Reference(string path) { path = Path.GetFullPath(path); return new(path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length); }
        ExportNativeFile Save<T>(string relative, T value) { var path = Path.Combine(root, relative); Write(path, value); return Reference(path); }
        ExportNativeFile TextFile(string relative, string value) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); return Reference(path); }
        ExportNativeIdentities Identities() => new(plan.Sha256, H('a'), approval.Sha256, H('f'), construction.Sha256, constructionReport.Sha256, blend.Sha256, package?.Sha256);
        internal Result Produce()
        {
            var directory = "produce-" + sequence++; var output = Path.Combine(root, directory, "output"); Directory.CreateDirectory(output);
            TextFile(directory + "/output/visual.glb", "SIMULATED visual bytes; no geometry quality claim"); TextFile(directory + "/output/collision.glb", "SIMULATED collision bytes; no geometry quality claim");
            var job = new ExportNativeJob("bubo-floor-plan-export-job/v1", "produce", root, output, bundle, toolchain, Identities() with { PackageSha256 = null }, limits, new(blend, construction, constructionReport, plan, null, null, null, null));
            var result = new Result(directory, job, "producer-report.json"); SaveJob(result);
            Report(result, new JsonObject { ["passed"] = true, ["sourceUnchanged"] = true, ["source"] = new JsonObject { ["visual"] = Compare("visual", "source"), ["collision"] = Compare("collision", "source") }, ["visual"] = Compare("visual", "raw"), ["collision"] = Compare("collision", "raw"), ["geometryChecks"] = 10000 }, [OutputEntry(result, "collision.glb"), OutputEntry(result, "visual.glb")]); return result;
        }
        void Package()
        {
            if (package is not null) return;
            var files = new List<ExportFileEntry>();
            void Copy(ExportNativeFile original, string target) { var dest = Path.Combine(root, "package", target); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(original.Path, dest); files.Add(new(target, original.Sha256, original.Bytes)); }
            Copy(plan, "building-plan.json"); Copy(blend, "building.blend"); Copy(constructionReport, "provenance/construction-report.json"); Copy(approval, "provenance/design-approval.json");
            foreach (var target in new[] { "visual.glb", "collision.glb", "provenance/design-preview.json", "semantics.json" }.Concat(new[] { "roof-plan.svg", "elevation-north.svg", "elevation-south.svg", "elevation-east.svg", "elevation-west.svg", "section-principal.svg", "construction-details.svg", "floor-01.svg" }.Select(n => "drawings/" + n))) { var file = TextFile("package/" + target, "SIMULATED immutable payload"); files.Add(new(target, file.Sha256, file.Bytes)); }
            var payload = new FloorPlanExportPackage { SchemaVersion = ExportPackageValidator.PackageSchema, Task = "synthetic", Asset = "synthetic", Version = "v1", Coordinates = ExportPackageValidator.Coordinates(), Numerics = Plan.Numerics, Files = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), Ancestry = new() { ModelAuthor = "synthetic-author", PlanSha256 = plan.Sha256, DesignFingerprint = H('a'), ApprovalSha256 = approval.Sha256, RuntimeSha256 = H('f'), CatalogSha256 = H('a'), ConstructionAttempt = "source", ConstructionJobSha256 = construction.Sha256, ConstructionReportSha256 = constructionReport.Sha256, SourceSha256 = blend.Sha256, Bundle = bundle, Toolchain = new() { BlenderSha256 = toolchain.BlenderSha256, BlenderVersion = "4.5.3", GltfFiles = toolchain.Files.Select(f => new ExportFileEntry(f.Path, f.Sha256, f.Bytes)).ToList() } } };
            package = Save("package/package.json", payload);
        }
        internal Result Verification(string representation)
        {
            Package(); var directory = "verify-" + representation + "-" + sequence++; var output = Path.Combine(root, directory, "output"); Directory.CreateDirectory(output);
            var job = new ExportNativeJob("bubo-floor-plan-export-job/v1", "verify", root, output, bundle, toolchain, Identities(), limits, new(null, construction, null, plan, package, Reference(Path.Combine(root, "package", representation + ".glb")), null, null));
            var result = new Result(directory, job, "verification.json"); SaveJob(result);
            Report(result, VerificationMeasured(representation), [], representation); return result;
        }
        internal Result Views()
        {
            var visual = Verification("visual"); var collision = Verification("collision"); var directory = "views-" + sequence++; var output = Path.Combine(root, directory, "output"); Directory.CreateDirectory(output);
            var job = new ExportNativeJob("bubo-floor-plan-export-job/v1", "views", root, output, bundle, toolchain, Identities(), limits, new(null, construction, null, plan, package, null, Reference(ReportPath(visual)), Reference(ReportPath(collision)))); var result = new Result(directory, job, "views.json"); SaveJob(result);
            var directions = new[] { new Point3(0, 1, 0), new Point3(1, 0, 0), new Point3(0, -1, 0), new Point3(-1, 0, 0), new Point3(0, 0, 1), new Point3(0, 0, 1), new Point3(0, -1, 0) }; var kinds = new[] { "exterior-north", "exterior-east", "exterior-south", "exterior-west", "roof-plan", "floor-plan", "principal-section" }; var views = new JsonArray(); var outputs = new List<ExportNativeOutput>();
            for (var i = 0; i < kinds.Length; i++)
            {
                var name = $"view-{i:D3}.png"; WritePng(Path.Combine(output, name)); var file = OutputEntry(result, name); outputs.Add(file); var d = directions[i]; var distance = 2 * Math.Sqrt(3); var camera = new JsonObject { ["projection"] = "ORTHO", ["position"] = Vector(.5 + d.X * distance, .5 + d.Y * distance, .5 + d.Z * distance), ["target"] = Vector(.5, .5, .5), ["direction"] = Vector(d.X, d.Y, d.Z), ["orthoScale"] = Math.Sqrt(3) / 2 * 2.3 };
                var planes = new JsonArray(); if (i == 5) { var floor = Plan.Storeys[0]; planes.Add(Plane(new(0, 0, floor.Elevation + Math.Min(1.2, floor.Height / 2)), new(0, 0, 1))); planes.Add(Plane(new(0, 0, floor.Elevation - floor.SlabThickness), new(0, 0, -1))); } if (i == 6) planes.Add(Plane(new(.5, .5, .5), new(0, -1, 0)));
                views.Add(new JsonObject { ["index"] = i, ["kind"] = kinds[i], ["sourceId"] = i == 5 ? Plan.Storeys[0].Id : null, ["path"] = name, ["sha256"] = file.Sha256, ["bytes"] = file.Bytes, ["camera"] = camera, ["clipPlanes"] = planes, ["meshIds"] = new JsonArray("visual-synthetic"), ["diagnostics"] = new JsonObject { ["method"] = "world-triangle-half-space-clipping", ["capSectionFaces"] = false, ["vertices"] = 12, ["triangles"] = 4, ["planeIntersections"] = 0 } });
            }
            var measured = new JsonObject { ["passed"] = true, ["requiredViewCount"] = views.Count, ["views"] = views, ["geometryChecks"] = 10000, ["rawVisual"] = Compare("visual", "raw"), ["importedVisual"] = Compare("visual", "imported"), ["collisionCheck"] = new JsonObject { ["method"] = "separate-complete-triangle-import-verification", ["sha256"] = job.Inputs.CollisionVerification!.Sha256 }, ["renderSettings"] = new JsonObject { ["engine"] = "CYCLES", ["device"] = "CPU", ["samples"] = 8, ["threads"] = 2, ["imageSize"] = 768, ["viewTransform"] = "Standard", ["denoising"] = false, ["adaptiveSampling"] = false } }; Report(result, measured, outputs); return result;
        }
        JsonObject VerificationMeasured(string representation) => new() { ["passed"] = true, ["raw"] = Compare(representation, "raw"), ["imported"] = Compare(representation, "imported"), ["geometryChecks"] = 10000, ["cleanProcessImport"] = true, ["newNavigationMeasurement"] = false, ["nativeIndexedTopologyBasis"] = "source-construction-only", ["clearanceBasis"] = "complete-oriented-triangle-correspondence-to-native-measured-construction", ["sourceClearanceEvidence"] = JsonSerializer.SerializeToNode(Scene.Clearance, StrictPlanJson.Options), ["clearancePreservation"] = new JsonObject { ["method"] = "native-clearance-minus-measured-surface-and-hinge-perturbation", ["routes"] = new JsonArray(), ["doorSweeps"] = new JsonArray(), ["maxSurfacePointErrorBound"] = 0, ["newNavigationMeasurement"] = false, ["sourceApertureIds"] = new JsonArray(Plan.Storeys.SelectMany(f => f.Walls).SelectMany(w => w.Openings).Select(o => o.Id).Order(StringComparer.Ordinal).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["sourceSlabOpeningIds"] = new JsonArray(), ["sourceStairIds"] = new JsonArray(), ["featureBasis"] = "complete-triangle-preservation-within-approved-tolerance" } };
        JsonObject Compare(string representation, string kind)
        {
            var value = new JsonObject { ["meshes"] = new JsonArray(new JsonObject { ["id"] = representation + "-synthetic", ["sourceTriangleCount"] = 4, ["observedTriangleCount"] = 4, ["observedVertexCount"] = 4, ["materialTriangleCounts"] = new JsonObject { [Material.Id] = 4 }, ["minimum"] = JsonSerializer.SerializeToNode(new Point3(), StrictPlanJson.Options), ["maximum"] = JsonSerializer.SerializeToNode(new Point3(1, 1, 1), StrictPlanJson.Options), ["maxWorldCoordinateError"] = 0, ["maxSourceFaceAngularErrorDegrees"] = 0 }), ["sourceTriangleCount"] = 4, ["observedTriangleCount"] = 4, ["observedVertexCount"] = 4, ["maxWorldCoordinateError"] = 0, ["maxSourceFaceAngularErrorDegrees"] = 0, ["geometryChecks"] = 124 };
            if (kind != "source")
            {
                var color = new JsonArray(); for (var i = 0; i < 3; i++) { var c = Convert.ToInt32(Material.Color.Substring(1 + i * 2, 2), 16) / 255.0; color.Add(c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4)); } color.Add(1);
                value["materials"] = new JsonArray(new JsonObject { ["id"] = Material.Id, ["color"] = color, ["roughness"] = Material.Roughness, ["metallic"] = Material.Metallic, ["transmission"] = 0, ["ior"] = 1 });
                value["nodes"] = kind == "raw" ? Nodes(representation) : new JsonObject { ["nodes"] = Nodes(representation), ["maxNodePositionError"] = 0 };
                if (kind == "raw") { value["maxNodePositionError"] = 0; value["rawCounts"] = new JsonObject { ["nodes"] = 5, ["meshes"] = 1, ["materials"] = 1, ["accessors"] = 3, ["primitives"] = 1, ["decodedVertices"] = 8 }; }
                else value["normalDiagnostics"] = new JsonObject { ["cornerNormals"] = 12, ["maxAngularErrorDegrees"] = 0, ["angularToleranceDegrees"] = Plan.Numerics.AngularToleranceDegrees };
            }
            return value;
        }
        JsonArray Nodes(string representation)
        {
            var floor = Plan.Storeys[0].Id; var building = "building:" + Plan.Id; var storey = "storey:" + floor; var role = representation == "visual" ? "visual" : "collision-static"; var group = "representation:" + role + ":" + floor;
            JsonObject Node(string id, string kind, string? parent, string floorId = "", string semantic = "", string rep = "", string collisionRole = "", string material = "") => new() { ["id"] = id, ["semanticId"] = semantic, ["storeyId"] = floorId, ["kind"] = kind, ["representation"] = rep, ["collisionRole"] = collisionRole, ["materialId"] = material, ["parentAnchorId"] = "", ["parentId"] = parent, ["position"] = Vector(0, 0, 0), ["axis"] = Vector(0, 0, 1), ["worldMatrix"] = new JsonArray(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1) };
            var rows = new[] { Node(building, "building", null), Node(storey, "storey", building, floor), Node(group, "representation", storey, floor, rep: role), Node("floor-anchor-synthetic", "floor-root", storey, floor, floor), Node(representation + "-synthetic", "floor-slab", group, floor, floor, representation, representation == "visual" ? "none" : "static", Material.Id) }; return new JsonArray(rows.OrderBy(r => r["id"]!.GetValue<string>(), StringComparer.Ordinal).Select(r => (JsonNode?)r).ToArray());
        }
        static JsonArray Vector(double x, double y, double z) => new(x, y, z);
        static JsonObject Plane(Point3 point, Point3 normal) => new() { ["point"] = Vector(point.X, point.Y, point.Z), ["normal"] = Vector(normal.X, normal.Y, normal.Z) };
        void Report(Result result, JsonObject measured, List<ExportNativeOutput> outputs, string? representation = null) => Write(ReportPath(result), new ExportNativeReport { SchemaVersion = result.Job.Operation switch { "produce" => "bubo-floor-plan-export-producer/v1", "verify" => "bubo-floor-plan-export-verification/v1", _ => "bubo-floor-plan-export-views/v1" }, Operation = result.Job.Operation, JobSha256 = result.JobSha, Identities = result.Job.Identities, Bundle = bundle, Toolchain = toolchain, Inputs = result.Job.Inputs, Limits = limits, Outputs = outputs, Measured = measured, HumanModelAccepted = false, RuntimeAccepted = false, BlenderVersion = "4.5.3", Representation = representation });
        internal string Output(Result result, string name) => Path.Combine(root, result.Directory, "output", name);
        internal string JobPath(Result result) => Path.Combine(root, result.Directory, "job.json");
        internal string ReportPath(Result result) => Output(result, result.ReportName);
        internal void SaveJob(Result result) { Write(JobPath(result), result.Job); result.JobSha = Reference(JobPath(result)).Sha256; }
        ExportNativeOutput OutputEntry(Result result, string name) { var f = Reference(Output(result, name)); return new(name, f.Sha256, f.Bytes); }
        internal void Verify(Result result, CancellationToken cancellation = default) => ExportOutputVerifier.Verify(workspace, result.Directory + "/output", result.Job, result.JobSha, cancellation);
        internal void Append(Result result, string name, string text) => File.AppendAllText(Output(result, name), text);
        internal void RejectMutation(Result result, Action<JsonObject> mutate)
        {
            var original = File.ReadAllBytes(ReportPath(result)); var node = JsonNode.Parse(original)!.AsObject(); mutate(node); Write(ReportPath(result), node);
            try { try { Verify(result); } catch (Exception error) when (error is InvalidOperationException or JsonException or ArgumentException) { return; } throw new InvalidOperationException("Protocol mutation was accepted"); }
            finally { File.WriteAllBytes(ReportPath(result), original); }
        }
        internal void RefreshOutput(Result result, string name)
        {
            var replacement = OutputEntry(result, name); var node = JsonNode.Parse(File.ReadAllBytes(ReportPath(result)))!.AsObject();
            foreach (var item in node["outputs"]!.AsArray().OfType<JsonObject>()) if (item["path"]!.GetValue<string>() == name) { item["sha256"] = replacement.Sha256; item["bytes"] = replacement.Bytes; }
            foreach (var item in node["measured"]!["views"]!.AsArray().OfType<JsonObject>()) if (item["path"]!.GetValue<string>() == name) { item["sha256"] = replacement.Sha256; item["bytes"] = replacement.Bytes; }
            Write(ReportPath(result), node);
        }
    }
    static void WritePng(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string name, byte[] bytes) { Span<byte> n = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(n, (uint)bytes.Length); file.Write(n); var type = System.Text.Encoding.ASCII.GetBytes(name); file.Write(type); file.Write(bytes); var crc = 0xffffffffu; foreach (var b in type.Concat(bytes)) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1; } BinaryPrimitives.WriteUInt32BigEndian(n, crc ^ 0xffffffffu); file.Write(n); }
        var header = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(header, 768); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 768); header[8] = 8; header[9] = 2; Chunk("IHDR", header);
        using var compressed = new MemoryStream(); using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(new byte[768 * (768 * 3 + 1)]); Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
    }
}
