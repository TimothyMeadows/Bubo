using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Reflection;
using Bubo.FloorPlan.Domain;
using Bubo.FloorPlan.Infrastructure;

namespace Bubo.FloorPlan.Tests;

/// <summary>Synthetic pure-contract fixtures; none constitute actual Blender/model/human evidence.</summary>
public static class ExportContractTests
{
    private static string H(char c) => new(c, 64);
    private static readonly string[] Drawings = ["drawings/000-floor.svg"];

    public static void Run()
    {
        Check.Case("export contract: closed exact portable payload has no self hash/reviews", () =>
        {
            var package = Package(); ExportPackageValidator.RequireValidPackage(package, Drawings);
            var roundtrip = StrictPlanJson.Parse<FloorPlanExportPackage>(JsonSerializer.SerializeToUtf8Bytes(package, StrictPlanJson.Options));
            ExportPackageValidator.RequireValidPackage(roundtrip, Drawings);
            var node = JsonSerializer.SerializeToNode(package, StrictPlanJson.Options)!.AsObject();
            node["packageSha256"] = H('a');
            Check.Throws<JsonException>(() => StrictPlanJson.Parse<FloorPlanExportPackage>(JsonSerializer.SerializeToUtf8Bytes(node)));
        });
        Check.Case("export contract: missing/unknown/duplicate/null/case fields are rejected", () =>
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Package(), StrictPlanJson.Options);
            var node = JsonNode.Parse(bytes)!.AsObject(); node.Remove("ancestry");
            Check.Throws<JsonException>(() => StrictPlanJson.Parse<FloorPlanExportPackage>(JsonSerializer.SerializeToUtf8Bytes(node)));
            node = JsonNode.Parse(bytes)!.AsObject(); node["coordinates"] = null;
            Check.Throws<JsonException>(() => StrictPlanJson.Parse<FloorPlanExportPackage>(JsonSerializer.SerializeToUtf8Bytes(node)));
            var duplicate = System.Text.Encoding.UTF8.GetString(bytes).Replace("\"schemaVersion\":", "\"schemaVersion\":\"duplicate\",\"schemaVersion\":", StringComparison.Ordinal);
            Check.Throws<InvalidOperationException>(() => StrictPlanJson.Parse<FloorPlanExportPackage>(System.Text.Encoding.UTF8.GetBytes(duplicate)));
            node = JsonNode.Parse(bytes)!.AsObject(); node["SchemaVersion"] = node["schemaVersion"]!.DeepClone(); node.Remove("schemaVersion");
            Check.Throws<JsonException>(() => StrictPlanJson.Parse<FloorPlanExportPackage>(JsonSerializer.SerializeToUtf8Bytes(node)));
        });
        Check.Case("export contract: inventory rejects missing/extra/self/review/alias/traversal/device/stream entries", () =>
        {
            foreach (var path in new[] { "package.json", "verification.json", "reviews/human.json", "../escape", "/absolute", "C:/outside", "drawings\\alias.svg", "drawings/NUL.svg", "drawings/a.svg:stream", "drawings/a.svg.", "drawings/ a.svg/../escape" })
            {
                var p = Package(); p.Files.Add(new(path, H('b'), 1)); p.Files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
                Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(p, Drawings));
            }
            var missing = Package(); missing.Files.RemoveAt(0); Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(missing, Drawings));
            var alias = Package(); alias.Files.Add(alias.Files[0] with { Path = alias.Files[0].Path.ToUpperInvariant() }); alias.Files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(alias, Drawings));
            var unsorted = Package(); unsorted.Files.Reverse(); Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(unsorted, Drawings));
        });
        Check.Case("export contract: cumulative bytes/ancestry/coordinates/numerics cannot be changed", () =>
        {
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(Package(), Drawings, 8));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(Package() with { Ancestry = Package().Ancestry with { SourceSha256 = H('f') } }, Drawings));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(Package() with { Ancestry = Package().Ancestry with { SourceSha256 = H('c') + "\n" } }, Drawings));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(Package() with { Ancestry = Package().Ancestry with { ModelAuthor = " " } }, Drawings));
            var p = Package(); p.Coordinates.SourceToGltf[0] = -1; Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(p, Drawings));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(Package() with { Numerics = Package().Numerics with { LinearTolerance = double.NaN } }, Drawings));
            var overflow = Package(); overflow.Files[0] = overflow.Files[0] with { Bytes = long.MaxValue }; Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(overflow, Drawings));
            var tools = Package(); tools.Ancestry.Toolchain.GltfFiles.Add(new("io_scene_gltf2", H('a'), 1)); tools.Ancestry.Toolchain.GltfFiles.Sort((a,b) => StringComparer.Ordinal.Compare(a.Path,b.Path));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidPackage(tools, Drawings));
        });
        Check.Case("export contract: exact dimensional semantics and node/pivot/collision/material ancestry", () =>
        {
            var (plan, scene) = Source(); var semantics = ExportPackageValidator.CreateSemantics(plan, scene, H('a'), H('b'), H('c'));
            ExportPackageValidator.RequireValidSemantics(semantics, plan, scene, H('a'), H('b'), H('c'));
            ExportPackageValidator.RequireMatchingSemantics(Package() with { Numerics = plan.Numerics }, semantics);
            Check.True(semantics.Meshes.Single(m => m.Representation == "collision").ParentNodeId == "representation:collision-static:" + plan.Storeys[0].Id);
            foreach (var changed in new[]
            {
                semantics with { SourceSha256 = H('d') }, semantics with { Plan = semantics.Plan with { Storeys = [] } },
                semantics with { ClearanceBasis = "independent-glb" }, semantics with { DoorPoseConvention = "open-at-zero" },
                semantics with { Meshes = [semantics.Meshes[0] with { ParentNodeId = "other" }, semantics.Meshes[1]] },
                semantics with { Meshes = [semantics.Meshes[0] with { MaterialId = "different" }, semantics.Meshes[1]] },
                semantics with { Meshes = [semantics.Meshes[0] with { Minimum = new(.1, 0, 0) }, semantics.Meshes[1]] },
                semantics with { Meshes = [semantics.Meshes[0], semantics.Meshes[1] with { CollisionRole = "dynamic" }] },
                semantics with { Anchors = [semantics.Anchors[0] with { Axis = new(0, 1, 0) }] }
            }) Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidSemantics(changed, plan, scene, H('a'), H('b'), H('c')));
            semantics.Plan.Storeys[0].Walls.Clear();
            Check.True(plan.Storeys[0].Walls.Count > 0, "Factory must produce a deep source snapshot");
        });
        Check.Case("export contract: v1, invalid source references/indices and nonfinite values fail", () =>
        {
            var (plan, scene) = Source();
            Check.Throws<ArgumentException>(() => ExportPackageValidator.CreateSemantics(PlanExamples.SingleStorey(), scene, H('a'), H('b'), H('c')));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.CreateSemantics(plan, scene with { Meshes = [scene.Meshes[0] with { ParentAnchorId = "missing" }] }, H('a'), H('b'), H('c')));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.CreateSemantics(plan, scene with { Meshes = [scene.Meshes[0] with { Triangles = [0, 1, 99] }] }, H('a'), H('b'), H('c')));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.CreateSemantics(plan, scene with { Meshes = [scene.Meshes[0] with { Vertices = [new(double.NaN, 0, 0)] }] }, H('a'), H('b'), H('c')));
        });
        Check.Case("export contract: cancelled semantic creation and comparison cannot succeed", () =>
        {
            var (plan, scene) = Source();
            var semantics = ExportPackageValidator.CreateSemantics(plan, scene, H('a'), H('b'), H('c'));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check.Throws<OperationCanceledException>(() => ExportPackageValidator.CreateSemantics(plan, scene, H('a'), H('b'), H('c'), cancelled.Token));
            Check.Throws<OperationCanceledException>(() => ExportPackageValidator.RequireValidSemantics(semantics, plan, scene, H('a'), H('b'), H('c'), cancelled.Token));
        });
        Check.Case("export contract: cancellation inside large snapshot serialization is observed by bounded writes", () =>
        {
            using var cancellation = new CancellationTokenSource();
            // The callback fires inside serialization after its entry check, without scheduler/timer races.
            var value = new CancelsDuringWriteSnapshot(cancellation) { Payload = new string('x', 512 * 1024) };
            RequireSnapshotCancellation("SnapshotBytes", value, cancellation.Token);
            Check.True(cancellation.IsCancellationRequested);
        });
        Check.Case("export contract: cancellation at completion of large snapshot deserialization prevents clone success", () =>
        {
            using var cancellation = new CancellationTokenSource();
            CancelsAfterReadSnapshot.AfterRead = cancellation.Cancel;
            try
            {
                var value = new CancelsAfterReadSnapshot { Payload = new string('x', 512 * 1024) };
                RequireSnapshotCancellation("SnapshotClone", value, cancellation.Token);
                Check.True(cancellation.IsCancellationRequested);
            }
            finally { CancelsAfterReadSnapshot.AfterRead = null; }
        });
        Check.Case("export review: independent evidence is exact, separate and may reject", () =>
        {
            var (review, binding) = Review(); ExportPackageValidator.RequireValidModelReview(review, binding);
            ExportPackageValidator.RequireValidModelReview(review with { Decision = "rejected", Reason = "Measured issue remains" }, binding);
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidModelReview(review with { Reviewer = "AUTHOR" }, binding));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidModelReview(review with { Human = true }, binding));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidModelReview(review with { PackageSha256 = H('f') }, binding));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidModelReview(review with { Artifacts = [] }, binding));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidModelReview(review with { Artifacts = [review.Artifacts[0] with { Sha256 = H('f') }] }, binding));
        });
        Check.Case("export review: human model decision requires exact current independent identity and complete artifacts", () =>
        {
            var (review, binding) = Review();
            review = review with { Kind = "human-model", Human = true, IndependentReviewSha256 = H('e') };
            binding = binding with { Kind = "human-model", IndependentReviewSha256 = H('e') };
            ExportPackageValidator.RequireValidModelReview(review, binding);
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidModelReview(review with { IndependentReviewSha256 = null }, binding));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidModelReview(review with { IndependentReviewSha256 = H('d') }, binding));
            Check.Throws<ArgumentException>(() => ExportPackageValidator.RequireValidModelReview(review with { ViewsSha256 = H('f') }, binding));
        });
    }

    private static void RequireSnapshotCancellation<T>(string helper, T value, CancellationToken token)
    {
        // Exercise the private common snapshot seam to trigger cancellation at a deterministic phase.
        var method = typeof(ExportPackageValidator).GetMethod(helper, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(T));
        var error = Check.Throws<TargetInvocationException>(() => method.Invoke(null, [value, token]));
        Check.True(error.InnerException is OperationCanceledException cancelled && cancelled.CancellationToken == token, "Snapshot work must propagate the supplied cancellation token");
    }

    private sealed class CancelsDuringWriteSnapshot(CancellationTokenSource cancellation) : IJsonOnSerializing
    {
        public required string Payload { get; init; }
        public void OnSerializing() => cancellation.Cancel();
    }

    private sealed class CancelsAfterReadSnapshot : IJsonOnDeserialized
    {
        public CancelsAfterReadSnapshot() { }
        public static Action? AfterRead { get; set; }
        public required string Payload { get; init; }
        public void OnDeserialized() => AfterRead?.Invoke();
    }

    private static FloorPlanExportPackage Package()
    {
        var files = new[] { "building.blend", "building-plan.json", "collision.glb", Drawings[0], "provenance/construction-report.json", "provenance/design-approval.json", "provenance/design-preview.json", "semantics.json", "visual.glb" }
            .Select(path => new ExportFileEntry(path, path switch { "building-plan.json" => H('a'), "building.blend" => H('c'), "provenance/construction-report.json" => H('d'), "provenance/design-approval.json" => H('e'), _ => H('b') }, 1)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return new()
        {
            SchemaVersion = ExportPackageValidator.PackageSchema, Task = "synthetic-task", Asset = "synthetic-building", Version = "v1",
            Ancestry = new() { ModelAuthor = "author", PlanSha256 = H('a'), DesignFingerprint = H('b'), ApprovalSha256 = H('e'), RuntimeSha256 = H('f'), CatalogSha256 = H('a'), ConstructionAttempt = "builds/synthetic", ConstructionJobSha256 = H('b'), ConstructionReportSha256 = H('d'), SourceSha256 = H('c'), Bundle = new() { ["export.py"] = H('a') }, Toolchain = new() { BlenderSha256 = H('a'), BlenderVersion = "4.5.3 synthetic", GltfFiles = [new("io_scene_gltf2/__init__.py", H('b'), 1)] } },
            Coordinates = ExportPackageValidator.Coordinates(), Numerics = ConstructionPlanExamples.SingleStorey().Numerics, Files = files
        };
    }

    private static (BuildingPlan, ConstructionScene) Source()
    {
        var plan = ConstructionPlanExamples.SingleStorey(); var floor = plan.Storeys[0].Id; var material = plan.Materials[0];
        var visual = new ConstructionMesh { Id = "visual-synthetic", SemanticId = floor, StoreyId = floor, Kind = "floor-slab", Representation = "visual", CollisionRole = "none", MaterialId = material.Id, ParentAnchorId = null, TriangleMaterialIds = [material.Id], Vertices = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], Triangles = [0, 1, 2] };
        return (plan, new() { SchemaVersion = "bubo-floor-plan-construction-scene/v1", PlanId = plan.Id, LinearTolerance = plan.Numerics.LinearTolerance,
            Materials = [new() { Id = material.Id, Name = material.Name, Color = material.Color, Roughness = material.Roughness, Metallic = material.Metallic, Transmission = 0, Ior = 1 }],
            Meshes = [visual, visual with { Id = "collision-synthetic", Representation = "collision", CollisionRole = "static", TriangleMaterialIds = null }],
            Anchors = [new() { Id = "floor-anchor-" + floor, SemanticId = floor, StoreyId = floor, Kind = "floor-root", Position = new(0, 0, 0), Axis = new(0, 0, 1), RotationRadians = 0 }],
            Metrics = new() { MeshCount = 2, VertexCount = 6, TriangleCount = 2, VisualTriangleCount = 1, CollisionTriangleCount = 1, GeometryChecks = 1, Minimum = new(0, 0, 0), Maximum = new(1, 1, 0) },
            Clearance = new() { Routes = [], DoorSweeps = [], MinimumRouteClearance = 0 } });
    }

    private static (FloorPlanModelReview, ModelReviewBinding) Review()
    {
        var artifacts = new List<ExportFileEntry> { new("package.json", H('a'), 1) };
        return (new() { SchemaVersion = ExportPackageValidator.ModelReviewSchema, Kind = "independent-model", Decision = "approved", Reviewer = "independent-synthetic", Role = "qa-engineer", Human = false, Reason = "Synthetic contract fixture only", PackageSha256 = H('a'), VisualVerificationSha256 = H('b'), CollisionVerificationSha256 = H('c'), ViewsSha256 = H('d'), IndependentReviewSha256 = null, Artifacts = artifacts },
            new() { Kind = "independent-model", Author = "author", PackageSha256 = H('a'), VisualVerificationSha256 = H('b'), CollisionVerificationSha256 = H('c'), ViewsSha256 = H('d'), IndependentReviewSha256 = null, Artifacts = [.. artifacts] });
    }
}
