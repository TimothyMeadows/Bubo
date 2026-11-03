using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bubo.FloorPlan.Domain;
using Hephaera.AssetProduction;

namespace Bubo.FloorPlan.Infrastructure;

/// <summary>Admits fixed-worker receipts against exact original source and observed files. This is not human/model/engine acceptance.</summary>
internal static class ExportOutputVerifier
{
    const long ReportCap = 67_108_864, PngCap = 16_777_216;
    const double MaterialEpsilon = 1.0 / 1_048_576;
    // Diagnostic camera math/RNA is float32. This serialization bound never
    // applies to geometry, nodes, dimensions, clip planes or angular policy.
    const double CameraSerializationEpsilon = 1.0 / 1_048_576;
    static readonly string[] BundleNames = ["admission.py", "review.py", "export.py", "export_admission.py", "export_geometry.py", "export_views.py"];
    static readonly string[] CompareKeys = ["meshes", "sourceTriangleCount", "observedTriangleCount", "observedVertexCount", "maxWorldCoordinateError", "maxSourceFaceAngularErrorDegrees", "geometryChecks"];
    static readonly string[] Metadata = ["id", "semanticId", "storeyId", "kind", "representation", "collisionRole", "materialId", "parentAnchorId"];

    internal static ExportNativeReport Verify(IWorkspace workspace, string outputRelative, ExportNativeJob job, string expectedJobSha, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); Hash(expectedJobSha);
        Need(job.SchemaVersion == "bubo-floor-plan-export-job/v1" && job.Operation is "produce" or "verify" or "views", "unsupported job");
        var output = workspace.Resolve(outputRelative);
        Need(Path.GetFileName(output) == "output" && job.OutputDirectory == output && job.WorkspaceRoot == workspace.Root, "job workspace/output differs from owned leaf");
        var jobPath = workspace.Resolve(Path.Combine(Path.GetDirectoryName(output)!, "job.json"));
        var reportName = job.Operation switch { "produce" => "producer-report.json", "verify" => "verification.json", _ => "views.json" };
        using var leases = new ReadLeases(workspace);
        leases.Add(jobPath);
        Need(FileHash(workspace, jobPath, Math.Min(job.Limits.MaxJobBytes, 1_048_576), cancellation) == expectedJobSha, "native job bytes changed");
        var savedJob = ExportJson.Read<ExportNativeJob>(jobPath, Math.Min(job.Limits.MaxJobBytes, 1_048_576), cancellation);
        Same(savedJob, job, "saved job differs from admitted job");
        ValidateLimits(job.Limits);
        Need(job.Bundle.Count == BundleNames.Length && job.Bundle.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(BundleNames), "wrong fixed bundle");
        foreach (var hash in job.Bundle.Values) Hash(hash);
        foreach (var hash in new[] { job.Identities.PlanSha256, job.Identities.DesignFingerprint, job.Identities.ApprovalSha256, job.Identities.RuntimeSha256, job.Identities.ConstructionJobSha256, job.Identities.ConstructionReportSha256, job.Identities.SourceSha256 }) Hash(hash);
        ValidateInputSet(job);
        var constructionInput = job.Inputs.ConstructionJob ?? throw new InvalidOperationException("Missing source construction input");
        var planInput = job.Inputs.Plan ?? throw new InvalidOperationException("Missing source plan input");
        var inputs = InputFiles(job.Inputs).ToArray();
        foreach (var input in inputs) { Need(!IsWithin(input.Path, output), "input overlaps owned output subtree"); leases.Add(input.Path); VerifyFile(workspace, input, input == job.Inputs.ConstructionJob ? job.Limits.MaxJobBytes : job.Limits.MaxSourceBytes, cancellation); }
        Need(job.Inputs.ConstructionJob!.Sha256 == job.Identities.ConstructionJobSha256 && job.Inputs.Plan!.Sha256 == job.Identities.PlanSha256, "source reference identities differ");
        if (job.Operation == "produce") Need(job.Inputs.SourceBlend!.Sha256 == job.Identities.SourceSha256 && job.Inputs.ConstructionReport!.Sha256 == job.Identities.ConstructionReportSha256, "producer source ancestry differs");
        var source = ExportJson.Read<ConstructionBlenderJob>(workspace.Resolve(constructionInput.Path), job.Limits.MaxJobBytes, cancellation);
        var plan = ExportJson.Read<BuildingPlan>(workspace.Resolve(planInput.Path), Math.Min(job.Limits.MaxSourceBytes, StrictPlanJson.MaximumInputBytes), cancellation);
        Need(source.SchemaVersion == "bubo-floor-plan-blender-job/v1" && source.Operation == "construct" && source.WorkspaceRoot == workspace.Root && source.Scene.PlanId == plan.Id && source.Scene.LinearTolerance == plan.Numerics.LinearTolerance, "original construction/plan contract differs");
        Need(source.Identities.PlanSha256 == job.Identities.PlanSha256 && source.Identities.DesignFingerprint == job.Identities.DesignFingerprint && source.Identities.RuntimeSha256 == job.Identities.RuntimeSha256, "original construction ancestry differs");
        new ConstructionReadiness().RequireReady(plan, cancellation);
        ValidateSourceLimits(job.Limits, plan);
        var scene = source.Scene;
        Need(scene.Meshes.Count <= job.Limits.MaxMeshes && scene.Materials.Count <= job.Limits.MaxMaterials && scene.Metrics.VertexCount <= job.Limits.MaxVertices && scene.Metrics.TriangleCount <= job.Limits.MaxTriangles, "source exceeds export limits");
        Need(scene.SchemaVersion == "bubo-floor-plan-construction-scene/v1" && scene.Meshes.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() == scene.Meshes.Count && scene.Meshes.Count == scene.Metrics.MeshCount && scene.Meshes.Sum(m => (long)m.Vertices.Count) == scene.Metrics.VertexCount && scene.Meshes.Sum(m => (long)m.Triangles.Count / 3) == scene.Metrics.TriangleCount, "source geometry inventory differs");
        FloorPlanExportPackage? package = null; var payload = new List<ExportNativeFile>();
        string? representation = null;
        if (job.Operation != "produce")
        {
            Hash(job.Identities.PackageSha256!);
            var packageInput = job.Inputs.Package ?? throw new InvalidOperationException("Missing sealed package input");
            Need(job.Inputs.Package!.Sha256 == job.Identities.PackageSha256, "package reference differs");
            package = ExportJson.Read<FloorPlanExportPackage>(workspace.Resolve(packageInput.Path), Math.Min(job.Limits.MaxSourceBytes, StrictPlanJson.MaximumInputBytes), cancellation);
            ExportPackageValidator.RequireValidPackage(package, DrawingPaths(plan), Math.Min(plan.Budgets.MaxExportBytes, ExportPackageValidator.MaxPayloadBytes));
            Need(package.Ancestry.PlanSha256 == job.Identities.PlanSha256 && package.Ancestry.DesignFingerprint == job.Identities.DesignFingerprint && package.Ancestry.ApprovalSha256 == job.Identities.ApprovalSha256 && package.Ancestry.RuntimeSha256 == job.Identities.RuntimeSha256 && package.Ancestry.ConstructionJobSha256 == job.Identities.ConstructionJobSha256 && package.Ancestry.ConstructionReportSha256 == job.Identities.ConstructionReportSha256 && package.Ancestry.SourceSha256 == job.Identities.SourceSha256, "sealed package ancestry differs");
            Same(package.Numerics, plan.Numerics, "sealed numerical policy differs"); Same(package.Ancestry.Bundle, job.Bundle, "sealed bundle differs");
            Need(package.Ancestry.Toolchain.BlenderSha256 == job.Toolchain.BlenderSha256, "sealed executable differs");
            Need(package.Ancestry.Toolchain.GltfFiles.Count == job.Toolchain.Files.Count, "sealed module inventory differs");
            for (var i = 0; i < job.Toolchain.Files.Count; i++) { var a = package.Ancestry.Toolchain.GltfFiles[i]; var b = job.Toolchain.Files[i]; Need(a.Path == b.Path && a.Sha256 == b.Sha256 && a.Bytes == b.Bytes, "sealed module entry differs"); }
            var parent = Path.GetDirectoryName(packageInput.Path)!;
            foreach (var entry in package.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                var file = new ExportNativeFile(workspace.Resolve(Path.Combine(parent, entry.Path)), entry.Sha256, entry.Bytes);
                leases.Add(file.Path); VerifyFile(workspace, file, job.Limits.MaxSourceBytes, cancellation); payload.Add(file);
            }
            if (job.Operation == "verify")
            {
                var glb = job.Inputs.Glb ?? throw new InvalidOperationException("Missing selected GLB input");
                representation = new[] { "visual", "collision" }.SingleOrDefault(kind => glb.Path == workspace.Resolve(Path.Combine(parent, kind + ".glb")));
                Need(representation is not null, "selected GLB is not an exact sealed representation");
                var file = payload.Single(f => Path.GetFileName(f.Path) == representation + ".glb");
                Need(glb.Sha256 == file.Sha256 && glb.Bytes == file.Bytes, "selected GLB differs from sealed inventory");
            }
        }
        var inventory = Inventory(workspace, outputRelative, job.Limits.MaxOutputBytes, job.Limits.MaxViews + 3, cancellation);
        Need(inventory.ContainsKey(reportName), "missing fixed report");
        foreach (var file in inventory.Keys) leases.Add(Path.Combine(output, file));
        var reportPath = Path.Combine(output, reportName);
        var reportHash = FileHash(workspace, reportPath, Math.Min(ReportCap, job.Limits.MaxOutputBytes), cancellation);
        var report = ExportJson.Read<ExportNativeReport>(reportPath, Math.Min(ReportCap, job.Limits.MaxOutputBytes), cancellation);
        var schema = job.Operation switch { "produce" => "bubo-floor-plan-export-producer/v1", "verify" => "bubo-floor-plan-export-verification/v1", _ => "bubo-floor-plan-export-views/v1" };
        Need(report.SchemaVersion == schema && report.Operation == job.Operation && report.JobSha256 == expectedJobSha && report.Representation == representation && !report.HumanModelAccepted && !report.RuntimeAccepted, "report envelope or acceptance claims differ");
        Same(report.Identities, job.Identities, "report identities differ"); Same(report.Bundle, job.Bundle, "report bundle differs"); Same(report.Toolchain, job.Toolchain, "report toolchain differs"); Same(report.Inputs, job.Inputs, "report inputs differ"); Same(report.Limits, job.Limits, "report limits differ");
        Need(Regex.IsMatch(report.BlenderVersion, "^4\\.5\\.[0-9]+\\z", RegexOptions.CultureInvariant) && (package is null || report.BlenderVersion == package.Ancestry.Toolchain.BlenderVersion), "measured Blender version differs");
        var expectedOutputs = job.Operation switch { "produce" => new[] { "collision.glb", "visual.glb" }, "verify" => [], _ => Enumerable.Range(0, plan.Storeys.Count + plan.Stairs.Count + 6).Select(i => $"view-{i:D3}.png").Order(StringComparer.Ordinal).ToArray() };
        Need(report.Outputs.Count == expectedOutputs.Length && inventory.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedOutputs.Append(reportName)), "wrong exact output inventory");
        for (var i = 0; i < expectedOutputs.Length; i++)
        {
            var file = report.Outputs[i]; Need(file.Path == expectedOutputs[i], "outputs must be exact ordinal-sorted fixed names");
            Need(file.Bytes == inventory[file.Path], "output byte count differs");
            VerifyFile(workspace, new(Path.Combine(output, file.Path), file.Sha256, file.Bytes), job.Limits.MaxOutputBytes, cancellation);
        }
        var measured = report.Measured;
        if (job.Operation == "produce")
        {
            Closed(measured, ["passed", "source", "visual", "collision", "sourceUnchanged", "geometryChecks"]); True(measured, "passed"); True(measured, "sourceUnchanged");
            var original = Object(measured, "source"); Closed(original, ["visual", "collision"]);
            long work = 0;
            foreach (var kind in new[] { "visual", "collision" }) { work += Comparison(Object(original, kind), scene, plan, kind, job.Limits, "source", cancellation); work += Comparison(Object(measured, kind), scene, plan, kind, job.Limits, "raw", cancellation); }
            Need(Integer(measured, "geometryChecks", 1, job.Limits.MaxGeometryChecks) >= work, "producer cumulative work is underreported");
        }
        else if (job.Operation == "verify") ValidateVerification(measured, scene, plan, representation!, job.Limits, cancellation);
        else
        {
            foreach (var kind in new[] { "visual", "collision" }) PriorVerification(workspace, kind == "visual" ? job.Inputs.VisualVerification! : job.Inputs.CollisionVerification!, kind, job, report.BlenderVersion, scene, plan, leases, cancellation);
            ValidateViews(measured, report.Outputs, output, scene, plan, job, workspace, cancellation);
        }
        foreach (var file in inputs.Concat(payload)) VerifyFile(workspace, file, file == job.Inputs.ConstructionJob ? job.Limits.MaxJobBytes : job.Limits.MaxSourceBytes, cancellation);
        Need(FileHash(workspace, reportPath, Math.Min(ReportCap, job.Limits.MaxOutputBytes), cancellation) == reportHash && FileHash(workspace, jobPath, Math.Min(job.Limits.MaxJobBytes, 1_048_576), cancellation) == expectedJobSha, "report/job changed during verification");
        var final = Inventory(workspace, outputRelative, job.Limits.MaxOutputBytes, job.Limits.MaxViews + 3, cancellation);
        Need(inventory.Count == final.Count && inventory.All(p => final.GetValueOrDefault(p.Key) == p.Value), "output inventory changed during verification");
        foreach (var file in report.Outputs) VerifyFile(workspace, new(Path.Combine(output, file.Path), file.Sha256, file.Bytes), job.Limits.MaxOutputBytes, cancellation);
        cancellation.ThrowIfCancellationRequested(); return report;
    }

    static long Comparison(JsonObject value, ConstructionScene scene, BuildingPlan plan, string representation, ExportNativeLimits limits, string kind, CancellationToken cancellation)
    {
        Closed(value, CompareKeys.Concat(kind == "raw" ? ["nodes", "maxNodePositionError", "materials", "rawCounts"] : kind == "imported" ? ["nodes", "materials", "normalDiagnostics"] : []).ToArray());
        var expected = scene.Meshes.Where(m => m.Representation == representation).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var rows = Array(value, "meshes", limits.MaxMeshes); Need(rows.Count == expected.Count && rows.Count > 0, "measured mesh set differs");
        long triangles = 0, vertices = 0; double maximum = 0, angle = 0; string? previous = null;
        foreach (var entry in rows)
        {
            cancellation.ThrowIfCancellationRequested(); var row = AsObject(entry);
            Closed(row, ["id", "sourceTriangleCount", "observedTriangleCount", "observedVertexCount", "materialTriangleCounts", "minimum", "maximum", "maxWorldCoordinateError", "maxSourceFaceAngularErrorDegrees"]);
            var id = Text(row, "id"); Need(previous is null || StringComparer.Ordinal.Compare(previous, id) < 0, "duplicate/unsorted measured mesh"); previous = id;
            Need(expected.TryGetValue(id, out var source), "unknown measured mesh"); var mesh = source!;
            var count = mesh.Triangles.Count / 3;
            Need(Integer(row, "sourceTriangleCount", 1, limits.MaxTriangles) == count && Integer(row, "observedTriangleCount", 1, limits.MaxTriangles) == count, "triangle multiplicity differs"); triangles += count;
            var observed = Integer(row, "observedVertexCount", 3, limits.MaxVertices); vertices += observed;
            Need(kind != "source" || observed == mesh.Vertices.Count, "editable source indexing count changed");
            var palette = Object(row, "materialTriangleCounts"); var materialCounts = Enumerable.Range(0, count).Select(i => mesh.TriangleMaterialIds?[i] ?? mesh.MaterialId).GroupBy(m => m, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            Need(palette.Count == materialCounts.Count && materialCounts.All(p => Integer(palette, p.Key, 1, limits.MaxTriangles) == p.Value), "triangle material assignments differ");
            var low = new Point3(mesh.Vertices.Min(p => p.X), mesh.Vertices.Min(p => p.Y), mesh.Vertices.Min(p => p.Z)); var high = new Point3(mesh.Vertices.Max(p => p.X), mesh.Vertices.Max(p => p.Y), mesh.Vertices.Max(p => p.Z));
            Need(Near(Point(row["minimum"]), low, scene.LinearTolerance) && Near(Point(row["maximum"]), high, scene.LinearTolerance), "mesh world bounds differ");
            maximum = Math.Max(maximum, Number(row, "maxWorldCoordinateError", 0, scene.LinearTolerance)); angle = Math.Max(angle, Number(row, "maxSourceFaceAngularErrorDegrees", 0, plan.Numerics.AngularToleranceDegrees));
        }
        Need(vertices <= limits.MaxVertices && triangles <= limits.MaxTriangles && Integer(value, "sourceTriangleCount", 1, limits.MaxTriangles) == triangles && Integer(value, "observedTriangleCount", 1, limits.MaxTriangles) == triangles && Integer(value, "observedVertexCount", 3, limits.MaxVertices) == vertices, "measured cumulative geometry counts differ");
        EqualNumber(Number(value, "maxWorldCoordinateError", 0, scene.LinearTolerance), maximum, "world error aggregation differs"); EqualNumber(Number(value, "maxSourceFaceAngularErrorDegrees", 0, plan.Numerics.AngularToleranceDegrees), angle, "source-face angular aggregation differs");
        if (kind == "raw")
        {
            Nodes(Array(value, "nodes", limits.MaxNodes), Number(value, "maxNodePositionError", 0, scene.LinearTolerance), scene, plan, representation, cancellation);
            Materials(Array(value, "materials", limits.MaxMaterials), scene, representation);
            var counts = Object(value, "rawCounts"); Closed(counts, ["nodes", "meshes", "materials", "accessors", "primitives", "decodedVertices"]);
            Need(Integer(counts, "nodes", 1, limits.MaxNodes) == ExpectedNodes(scene, representation).Count && Integer(counts, "meshes", 1, limits.MaxMeshes) == expected.Count && Integer(counts, "materials", 1, limits.MaxMaterials) == Array(value, "materials", limits.MaxMaterials).Count, "raw decoded inventory differs");
            Integer(counts, "accessors", 1, limits.MaxAccessors); Integer(counts, "primitives", expected.Count, limits.MaxPrimitives); Need(Integer(counts, "decodedVertices", 1, 2L * limits.MaxVertices) >= vertices, "raw decoded vertex count is inconsistent");
        }
        if (kind == "imported")
        {
            var nodes = Object(value, "nodes"); Closed(nodes, ["nodes", "maxNodePositionError"]); Nodes(Array(nodes, "nodes", limits.MaxNodes), Number(nodes, "maxNodePositionError", 0, scene.LinearTolerance), scene, plan, representation, cancellation);
            Materials(Array(value, "materials", limits.MaxMaterials), scene, representation);
            var normals = Object(value, "normalDiagnostics"); Closed(normals, ["cornerNormals", "maxAngularErrorDegrees", "angularToleranceDegrees"]);
            Need(Integer(normals, "cornerNormals", 3, 3L * limits.MaxTriangles) == 3 * triangles && Number(normals, "angularToleranceDegrees", 0, 1) == plan.Numerics.AngularToleranceDegrees, "imported normal inventory/policy differs"); Number(normals, "maxAngularErrorDegrees", 0, plan.Numerics.AngularToleranceDegrees);
        }
        // Two triangle admissions, source grouping, all 27 neighbor cells and
        // at least one successful candidate are unavoidable for every match.
        return Integer(value, "geometryChecks", 31 * triangles, limits.MaxGeometryChecks);
    }

    static void ValidateVerification(JsonObject measured, ConstructionScene scene, BuildingPlan plan, string representation, ExportNativeLimits limits, CancellationToken cancellation)
    {
        Closed(measured, ["passed", "raw", "imported", "geometryChecks", "clearancePreservation", "clearanceBasis", "sourceClearanceEvidence", "newNavigationMeasurement", "nativeIndexedTopologyBasis", "cleanProcessImport"]);
        True(measured, "passed"); True(measured, "cleanProcessImport"); False(measured, "newNavigationMeasurement");
        Need(Text(measured, "clearanceBasis") == "complete-oriented-triangle-correspondence-to-native-measured-construction" && Text(measured, "nativeIndexedTopologyBasis") == "source-construction-only", "false topology/navigation evidence basis");
        SameNode(measured["sourceClearanceEvidence"], scene.Clearance, "copied source clearance evidence differs");
        var raw = Object(measured, "raw"); var imported = Object(measured, "imported");
        var work = Comparison(raw, scene, plan, representation, limits, "raw", cancellation) + Comparison(imported, scene, plan, representation, limits, "imported", cancellation);
        Need(Integer(measured, "geometryChecks", 1, limits.MaxGeometryChecks) >= work, "verification cumulative work is underreported");
        Clearance(Object(measured, "clearancePreservation"), raw, imported, scene, plan, representation, limits, cancellation);
    }

    static void PriorVerification(IWorkspace workspace, ExportNativeFile reference, string representation, ExportNativeJob current, string expectedBlenderVersion, ConstructionScene scene, BuildingPlan plan, ReadLeases leases, CancellationToken cancellation)
    {
        var output = Path.GetDirectoryName(reference.Path)!; Need(Path.GetFileName(reference.Path) == "verification.json" && Path.GetFileName(output) == "output", "prior verification has no fixed output identity");
        var jobPath = workspace.Resolve(Path.Combine(Path.GetDirectoryName(output)!, "job.json")); leases.Add(jobPath);
        var report = ExportJson.Read<ExportNativeReport>(workspace.Resolve(reference.Path), Math.Min(ReportCap, current.Limits.MaxSourceBytes), cancellation);
        var original = ExportJson.Read<ExportNativeJob>(jobPath, Math.Min(1_048_576, current.Limits.MaxJobBytes), cancellation); ValidateLimits(original.Limits); ValidateSourceLimits(original.Limits, plan); ValidateInputSet(original);
        var glb = original.Inputs.Glb ?? throw new InvalidOperationException("Missing prior verification GLB input");
        Need(original.SchemaVersion == "bubo-floor-plan-export-job/v1" && original.Operation == "verify" && original.WorkspaceRoot == workspace.Root && original.OutputDirectory == output && report.SchemaVersion == "bubo-floor-plan-export-verification/v1" && report.Operation == "verify" && report.Representation == representation && report.Outputs.Count == 0 && !report.HumanModelAccepted && !report.RuntimeAccepted, "prior verification is not a clean matching decision-free receipt");
        Hash(report.JobSha256); Need(FileHash(workspace, jobPath, Math.Min(1_048_576, current.Limits.MaxJobBytes), cancellation) == report.JobSha256, "prior verification job bytes changed");
        Same(report.Identities, current.Identities, "prior verification source/package identities differ"); Same(original.Identities, current.Identities, "prior verification job identities differ"); Same(report.Bundle, current.Bundle, "prior verification bundle differs"); Same(original.Bundle, current.Bundle, "prior verification job bundle differs"); Same(report.Toolchain, current.Toolchain, "prior verification toolchain differs"); Same(original.Toolchain, current.Toolchain, "prior verification job toolchain differs"); Same(report.Inputs, original.Inputs, "prior verification input envelope differs"); Same(report.Limits, original.Limits, "prior verification limit envelope differs");
        Need(original.Inputs.Package == current.Inputs.Package && original.Inputs.Plan == current.Inputs.Plan && original.Inputs.ConstructionJob == current.Inputs.ConstructionJob && glb.Path == workspace.Resolve(Path.Combine(Path.GetDirectoryName(current.Inputs.Package!.Path)!, representation + ".glb")), "prior verification does not bind exact current source/representation");
        leases.Add(glb.Path); VerifyFile(workspace, glb, current.Limits.MaxSourceBytes, cancellation);
        Need(report.BlenderVersion == expectedBlenderVersion, "prior verification Blender version differs");
        ValidateVerification(report.Measured, scene, plan, representation, original.Limits, cancellation);
        var inventory = Inventory(workspace, workspace.Relative(output), original.Limits.MaxOutputBytes, 2, cancellation); Need(inventory.Count == 1 && inventory.ContainsKey("verification.json"), "prior verification contains extra outputs");
        VerifyFile(workspace, reference, current.Limits.MaxSourceBytes, cancellation); Need(FileHash(workspace, jobPath, Math.Min(1_048_576, current.Limits.MaxJobBytes), cancellation) == report.JobSha256, "prior verification job changed during checking");
    }

    static void Clearance(JsonObject value, JsonObject raw, JsonObject imported, ConstructionScene scene, BuildingPlan plan, string representation, ExportNativeLimits limits, CancellationToken cancellation)
    {
        Closed(value, ["method", "routes", "doorSweeps", "maxSurfacePointErrorBound", "newNavigationMeasurement", "sourceApertureIds", "sourceSlabOpeningIds", "sourceStairIds", "featureBasis"]);
        Need(Text(value, "method") == "native-clearance-minus-measured-surface-and-hinge-perturbation" && Text(value, "featureBasis") == "complete-triangle-preservation-within-approved-tolerance", "false clearance method"); False(value, "newNavigationMeasurement");
        var error = Math.Max(Number(raw, "maxWorldCoordinateError", 0, scene.LinearTolerance), Number(imported, "maxWorldCoordinateError", 0, scene.LinearTolerance)); EqualNumber(Number(value, "maxSurfacePointErrorBound", 0, scene.LinearTolerance), error, "clearance surface error differs");
        Strings(value, "sourceApertureIds", plan.Storeys.SelectMany(f => f.Walls).SelectMany(w => w.Openings).Select(o => o.Id)); Strings(value, "sourceSlabOpeningIds", plan.Storeys.SelectMany(f => f.SlabOpenings).Select(o => o.Id)); Strings(value, "sourceStairIds", plan.Stairs.Select(s => s.Id));
        var routes = Array(value, "routes", 100_000); Need(routes.Count == scene.Clearance.Routes.Count, "route evidence inventory differs");
        for (var i = 0; i < routes.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var row = AsObject(routes[i]); var source = scene.Clearance.Routes[i]; Closed(row, ["roomId", "fromId", "toId", "sourceMinimumClearance", "triangleErrorBound", "conservativeClearanceLowerBound"]);
            Need(Text(row, "roomId") == source.RoomId && Text(row, "fromId") == source.FromId && Text(row, "toId") == source.ToId, "route source identities differ");
            EqualNumber(Number(row, "sourceMinimumClearance"), source.MinimumClearance, "route source margin differs"); EqualNumber(Number(row, "triangleErrorBound"), error, "route perturbation differs"); EqualNumber(Number(row, "conservativeClearanceLowerBound", -scene.LinearTolerance), source.MinimumClearance - error, "route lower bound differs");
        }
        var sweeps = Array(value, "doorSweeps", 16_384); Need(sweeps.Count == scene.Clearance.DoorSweeps.Count, "door sweep inventory differs");
        var observed = new[] { Array(raw, "nodes", limits.MaxNodes), Array(Object(imported, "nodes"), "nodes", limits.MaxNodes) };
        for (var i = 0; i < sweeps.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var row = AsObject(sweeps[i]); var sweep = scene.Clearance.DoorSweeps[i];
            Closed(row, ["openingId", "anchorId", "angleDegrees", "sourceMinimumClearance", "leafLeverArm", "hingePositionErrorBound", "hingeFrameFrobeniusErrorBound", "frameConditionAmplificationBound", "movingLeafErrorBound", "staticSurfaceErrorBound", "conservativeClearanceLowerBound", "completeSweepAndFinalOpenCovered"]);
            Need(Text(row, "openingId") == sweep.OpeningId && Text(row, "anchorId") == sweep.AnchorId, "door sweep identity differs"); True(row, "completeSweepAndFinalOpenCovered"); EqualNumber(Number(row, "angleDegrees", -180, 180), sweep.AngleDegrees, "signed door angle differs"); EqualNumber(Number(row, "sourceMinimumClearance"), sweep.MinimumClearance, "source sweep margin differs");
            var hinge = scene.Anchors.Single(a => a.Id == sweep.AnchorId).Position;
            var leaves = scene.Meshes.Where(m => m.Representation == representation && m.ParentAnchorId == sweep.AnchorId && m.Kind == "door-leaf").ToArray(); Need(leaves.Length > 0, "sweep has no owned dynamic leaf");
            double radius = 0; foreach (var leaf in leaves) foreach (var point in leaf.Vertices) { cancellation.ThrowIfCancellationRequested(); radius = Math.Max(radius, Distance(point, hinge)); }
            double hingeError = 0, frameError = 0;
            foreach (var set in observed)
            {
                var node = set.Select(AsObject).Single(n => Text(n, "id") == sweep.AnchorId); hingeError = Math.Max(hingeError, Distance(Point(node["position"]), hinge)); var matrix = Numbers(node, "worldMatrix", 16); double sum = 0;
                for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) sum += Math.Pow(matrix[4 * r + c] - (r == c ? 1 : 0), 2);
                frameError = Math.Max(frameError, Math.Sqrt(sum));
            }
            Need(frameError < 1, "hinge frame has no bounded inverse"); var amplification = (1 + frameError) / (1 - frameError); var moving = amplification * error + (amplification + 1) * hingeError + 2 * frameError / (1 - frameError) * radius;
            foreach (var (key, target) in new[] { ("leafLeverArm", radius), ("hingePositionErrorBound", hingeError), ("hingeFrameFrobeniusErrorBound", frameError), ("frameConditionAmplificationBound", amplification), ("movingLeafErrorBound", moving), ("staticSurfaceErrorBound", error) }) EqualNumber(Number(row, key, 0), target, "door perturbation differs: " + key);
            EqualNumber(Number(row, "conservativeClearanceLowerBound", -scene.LinearTolerance), sweep.MinimumClearance - moving - error, "complete door sweep lower bound differs");
        }
    }

    sealed record NodeExpected(string Id, string Semantic, string Storey, string Kind, string Representation, string CollisionRole, string Material, string ParentAnchor, string? Parent, Point3 Position);
    static List<NodeExpected> ExpectedNodes(ConstructionScene scene, string representation)
    {
        var root = "building:" + scene.PlanId; var rows = new List<NodeExpected> { new(root, "", "", "building", "", "", "", "", null, new()) };
        foreach (var storey in scene.Meshes.Select(m => m.StoreyId).Concat(scene.Anchors.Select(a => a.StoreyId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            rows.Add(new("storey:" + storey, "", storey, "storey", "", "", "", "", root, new()));
            foreach (var role in scene.Meshes.Where(m => m.Representation == representation && m.StoreyId == storey).Select(m => representation == "visual" ? "visual" : "collision-" + m.CollisionRole).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) rows.Add(new("representation:" + role + ":" + storey, "", storey, "representation", role, "", "", "", "storey:" + storey, new()));
        }
        foreach (var anchor in scene.Anchors) rows.Add(new(anchor.Id, anchor.SemanticId, anchor.StoreyId, anchor.Kind, "", "", "", "", "storey:" + anchor.StoreyId, anchor.Position));
        foreach (var mesh in scene.Meshes.Where(m => m.Representation == representation)) rows.Add(new(mesh.Id, mesh.SemanticId, mesh.StoreyId, mesh.Kind, mesh.Representation, mesh.CollisionRole, mesh.MaterialId, mesh.ParentAnchorId ?? "", mesh.ParentAnchorId ?? "representation:" + (representation == "visual" ? "visual" : "collision-" + mesh.CollisionRole) + ":" + mesh.StoreyId, mesh.ParentAnchorId is null ? new() : scene.Anchors.Single(a => a.Id == mesh.ParentAnchorId).Position));
        Need(rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() == rows.Count, "source/structural node IDs collide"); return rows;
    }

    static void Nodes(JsonArray rows, double reportedError, ConstructionScene scene, BuildingPlan plan, string representation, CancellationToken cancellation)
    {
        var expected = ExpectedNodes(scene, representation).ToDictionary(n => n.Id, StringComparer.Ordinal); Need(rows.Count == expected.Count, "node inventory differs"); double maximum = 0; string? previous = null;
        foreach (var entry in rows)
        {
            cancellation.ThrowIfCancellationRequested(); var row = AsObject(entry); Closed(row, Metadata.Concat(["parentId", "position", "axis", "worldMatrix"]).ToArray());
            var id = Text(row, "id"); Need(previous is null || StringComparer.Ordinal.Compare(previous, id) < 0, "duplicate/unsorted node identity"); previous = id; Need(expected.TryGetValue(id, out var wanted), "unknown semantic node"); var n = wanted!;
            Need(Text(row, "semanticId", true) == n.Semantic && Text(row, "storeyId", true) == n.Storey && Text(row, "kind") == n.Kind && Text(row, "representation", true) == n.Representation && Text(row, "collisionRole", true) == n.CollisionRole && Text(row, "materialId", true) == n.Material && Text(row, "parentAnchorId", true) == n.ParentAnchor && NullableText(row, "parentId") == n.Parent, "node metadata/parent differs");
            var matrix = Numbers(row, "worldMatrix", 16); Need(matrix[12] == 0 && matrix[13] == 0 && matrix[14] == 0 && matrix[15] == 1, "non-affine node matrix");
            var columns = Enumerable.Range(0, 3).Select(c => new Point3(matrix[c], matrix[4 + c], matrix[8 + c])).ToArray();
            for (var c = 0; c < 3; c++)
            {
                var length = Distance(columns[c], new()); Need(Math.Abs(length - 1) <= 1e-6, "node scale changed"); var component = c == 0 ? columns[c].X : c == 1 ? columns[c].Y : columns[c].Z;
                Need(Math.Acos(Math.Clamp(component / length, -1, 1)) * 180 / Math.PI <= plan.Numerics.AngularToleranceDegrees, "node angular orientation/reflection changed");
                for (var other = 0; other < c; other++) Need(Math.Abs(Dot(columns[c], columns[other])) <= 1e-6, "node shear changed");
            }
            var position = new Point3(matrix[3], matrix[7], matrix[11]); var error = Distance(position, n.Position); maximum = Math.Max(maximum, error);
            Need(error <= scene.LinearTolerance && Distance(Point(row["position"]), position) <= scene.LinearTolerance && Distance(Point(row["axis"]), columns[2]) <= 1e-6, "node pivot/axis disagrees with measured matrix");
        }
        EqualNumber(reportedError, maximum, "node error aggregation differs");
    }

    static void Materials(JsonArray rows, ConstructionScene scene, string representation)
    {
        var used = scene.Meshes.Where(m => m.Representation == representation).SelectMany(m => m.TriangleMaterialIds ?? [m.MaterialId]).ToHashSet(StringComparer.Ordinal); var source = scene.Materials.Where(m => used.Contains(m.Id)).ToDictionary(m => m.Id, StringComparer.Ordinal); Need(rows.Count == source.Count, "material set differs"); string? previous = null;
        foreach (var entry in rows)
        {
            var row = AsObject(entry); Closed(row, ["id", "color", "roughness", "metallic", "transmission", "ior"]); var id = Text(row, "id"); Need(previous is null || StringComparer.Ordinal.Compare(previous, id) < 0, "duplicate/unsorted material"); previous = id; Need(source.TryGetValue(id, out var value), "unknown material"); var material = value!;
            var color = Numbers(row, "color", 4); for (var i = 0; i < 4; i++) { var srgb = i == 3 ? 1 : Convert.ToInt32(material.Color.Substring(1 + 2 * i, 2), 16) / 255.0; var linear = i == 3 ? 1 : srgb <= .04045 ? srgb / 12.92 : Math.Pow((srgb + .055) / 1.055, 2.4); Need(color[i] is >= 0 and <= 1 && Math.Abs(color[i] - linear) <= MaterialEpsilon, "linear material color differs"); }
            foreach (var (key, target) in new[] { ("roughness", material.Roughness), ("metallic", material.Metallic), ("transmission", material.Transmission), ("ior", material.Ior) }) Need(Math.Abs(Number(row, key, 0, 3) - target) <= MaterialEpsilon, "material scalar differs: " + key);
        }
    }

    sealed record ViewSpec(string Kind, string? SourceId, Point3 Direction, (Point3 Point, Point3 Normal)[] Planes);
    static List<ViewSpec> Specs(BuildingPlan plan, ConstructionScene scene)
    {
        var center = Mid(scene.Metrics.Minimum, scene.Metrics.Maximum);
        var rows = new List<ViewSpec> { new("exterior-north", null, new(0, 1, 0), []), new("exterior-east", null, new(1, 0, 0), []), new("exterior-south", null, new(0, -1, 0), []), new("exterior-west", null, new(-1, 0, 0), []), new("roof-plan", null, new(0, 0, 1), []) };
        foreach (var floor in plan.Storeys.OrderBy(f => f.Elevation).ThenBy(f => f.Id, StringComparer.Ordinal)) rows.Add(new("floor-plan", floor.Id, new(0, 0, 1), [(new(0, 0, floor.Elevation + Math.Min(1.2, floor.Height / 2)), new(0, 0, 1)), (new(0, 0, floor.Elevation - floor.SlabThickness), new(0, 0, -1))]));
        rows.Add(new("principal-section", null, new(0, -1, 0), [(center, new(0, -1, 0))]));
        foreach (var stair in plan.Stairs.OrderBy(s => s.Id, StringComparer.Ordinal)) { var dx = stair.End.X - stair.Start.X; var dy = stair.End.Y - stair.Start.Y; var length = Math.Sqrt(dx * dx + dy * dy); Need(double.IsFinite(length) && length > 0, "invalid source connector direction"); var normal = new Point3(-dy / length, dx / length, 0); rows.Add(new("stair-section", stair.Id, normal, [(new((stair.Start.X + stair.End.X) / 2, (stair.Start.Y + stair.End.Y) / 2, 0), normal)])); }
        return rows;
    }

    static void ValidateViews(JsonObject measured, List<ExportNativeOutput> outputs, string output, ConstructionScene scene, BuildingPlan plan, ExportNativeJob job, IWorkspace workspace, CancellationToken cancellation)
    {
        Closed(measured, ["passed", "requiredViewCount", "views", "geometryChecks", "importedVisual", "rawVisual", "collisionCheck", "renderSettings"]); True(measured, "passed");
        var raw = Object(measured, "rawVisual"); var imported = Object(measured, "importedVisual"); var work = Comparison(raw, scene, plan, "visual", job.Limits, "raw", cancellation) + Comparison(imported, scene, plan, "visual", job.Limits, "imported", cancellation);
        Need(Integer(measured, "geometryChecks", 1, job.Limits.MaxGeometryChecks) >= work + plan.Storeys.Count + plan.Stairs.Count + 6, "view cumulative work is underreported");
        var collision = Object(measured, "collisionCheck"); Closed(collision, ["method", "sha256"]); Need(Text(collision, "method") == "separate-complete-triangle-import-verification" && Text(collision, "sha256") == job.Inputs.CollisionVerification!.Sha256, "collision review basis differs");
        var settings = Object(measured, "renderSettings"); Closed(settings, ["engine", "device", "samples", "threads", "imageSize", "viewTransform", "denoising", "adaptiveSampling"]);
        Need(Text(settings, "engine") == "CYCLES" && Text(settings, "device") == "CPU" && Integer(settings, "samples", 1, 8) == 8 && Integer(settings, "threads", 1, 2) == 2 && Integer(settings, "imageSize", 768, 768) == job.Limits.ImageSize && Text(settings, "viewTransform") == "Standard", "fixed CPU render settings differ"); False(settings, "denoising"); False(settings, "adaptiveSampling");
        var specs = Specs(plan, scene); var views = Array(measured, "views", job.Limits.MaxViews); Need(views.Count == specs.Count && Integer(measured, "requiredViewCount", 1, job.Limits.MaxViews) == specs.Count && outputs.Count == specs.Count, "incomplete floor/exterior/roof/connector views");
        var outputFiles = outputs.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var meshes = scene.Meshes.Where(m => m.Representation == "visual").ToDictionary(m => m.Id, StringComparer.Ordinal);
        var observedBounds = Array(imported, "meshes", job.Limits.MaxMeshes).Select(AsObject).ToDictionary(m => Text(m, "id"), StringComparer.Ordinal);
        long visibilityWork = 0;
        for (var i = 0; i < views.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var row = AsObject(views[i]); var spec = specs[i]; Closed(row, ["index", "kind", "sourceId", "path", "sha256", "bytes", "camera", "clipPlanes", "meshIds", "diagnostics"]);
            var filename = $"view-{i:D3}.png"; Need(outputFiles.TryGetValue(filename, out var artifact), "missing required indexed view artifact");
            Need(Integer(row, "index", 0, job.Limits.MaxViews - 1) == i && Text(row, "kind") == spec.Kind && NullableText(row, "sourceId") == spec.SourceId && Text(row, "path") == filename && Text(row, "sha256") == artifact.Sha256 && Integer(row, "bytes", 1, PngCap) == artifact.Bytes, "view identity/artifact differs");
            var planes = Array(row, "clipPlanes", 2); Need(planes.Count == spec.Planes.Length, "view clipping plane set differs");
            for (var p = 0; p < planes.Count; p++) { var plane = AsObject(planes[p]); Closed(plane, ["point", "normal"]); Need(Near(Point(plane["point"]), spec.Planes[p].Point, 1e-12) && Near(Point(plane["normal"]), spec.Planes[p].Normal, 1e-12), "view section plane differs from approved source"); }
            var camera = Object(row, "camera"); Closed(camera, ["projection", "position", "target", "orthoScale", "direction"]); Need(Text(camera, "projection") == "ORTHO" && Near(Point(camera["direction"]), spec.Direction, 1e-12), "view projection/direction differs");
            var bounds = observedBounds.Where(p => spec.Kind != "floor-plan" || meshes[p.Key].StoreyId == spec.SourceId).Select(p => p.Value).ToArray(); Need(bounds.Length > 0, "empty expected view floor");
            var low = new Point3(bounds.Min(b => Point(b["minimum"]).X), bounds.Min(b => Point(b["minimum"]).Y), bounds.Min(b => Point(b["minimum"]).Z)); var high = new Point3(bounds.Max(b => Point(b["maximum"]).X), bounds.Max(b => Point(b["maximum"]).Y), bounds.Max(b => Point(b["maximum"]).Z));
            var target = Mid(low, high); var radius = Math.Max(Distance(low, high) / 2, .1); var distance = Math.Max(4 * radius, 2); var length = Distance(spec.Direction, new()); var position = new Point3(target.X + spec.Direction.X / length * distance, target.Y + spec.Direction.Y / length * distance, target.Z + spec.Direction.Z / length * distance);
            Need(Distance(Point(camera["target"]), target) <= CameraSerializationEpsilon * Math.Max(1, Distance(target, new())) && Distance(Point(camera["position"]), position) <= CameraSerializationEpsilon * Math.Max(1, Distance(position, new())) && Math.Abs(Number(camera, "orthoScale", .01) - radius * 2.3) <= CameraSerializationEpsilon * Math.Max(1, radius * 2.3), "view camera does not frame admitted delivered geometry");
            var visible = Array(row, "meshIds", job.Limits.MaxMeshes); Need(visible.Count > 0, "empty rendered geometry inventory"); string? previous = null;
            foreach (var idNode in visible) { var id = ScalarText(idNode); Need((previous is null || StringComparer.Ordinal.Compare(previous, id) < 0) && meshes.ContainsKey(id), "unknown/duplicate view mesh ID"); previous = id; }
            Need(spec.Planes.Length != 0 || visible.Count == meshes.Count, "unclipped view omits delivered meshes");
            var reportedVisible = visible.Select(n => ScalarText(n)).ToHashSet(StringComparer.Ordinal);
            foreach (var mesh in meshes.Values)
            {
                var required = HasRetainedTriangle(mesh, spec.Planes, -scene.LinearTolerance, ref visibilityWork, job.Limits.MaxGeometryChecks, cancellation);
                var permitted = required || HasRetainedTriangle(mesh, spec.Planes, scene.LinearTolerance, ref visibilityWork, job.Limits.MaxGeometryChecks, cancellation);
                Need((!required || reportedVisible.Contains(mesh.Id)) && (permitted || !reportedVisible.Contains(mesh.Id)), "clipped view omits robustly retained geometry or includes definitely clipped geometry");
            }
            var diagnostic = Object(row, "diagnostics"); Closed(diagnostic, ["method", "capSectionFaces", "vertices", "triangles", "planeIntersections"]); Need(Text(diagnostic, "method") == "world-triangle-half-space-clipping", "view uses unsupported section method"); False(diagnostic, "capSectionFaces"); var diagnosticVertices = Integer(diagnostic, "vertices", 3, job.Limits.MaxVertices); var diagnosticTriangles = Integer(diagnostic, "triangles", 1, job.Limits.MaxTriangles); var intersections = Integer(diagnostic, "planeIntersections", 0, job.Limits.MaxGeometryChecks);
            if (spec.Planes.Length == 0) Need(diagnosticTriangles == scene.Meshes.Where(m => m.Representation == "visual").Sum(m => (long)m.Triangles.Count / 3) && diagnosticVertices == 3 * diagnosticTriangles && intersections == 0, "unclipped view geometry diagnostic differs");
            Png(workspace.Resolve(Path.Combine(output, filename)), job.Limits.ImageSize, cancellation);
        }
    }

    // Matched exported surfaces lie within the approved positional bound.
    // Contracted planes identify definitely visible source surfaces; expanded
    // planes identify possible boundary surfaces without inventing visibility.
    static bool HasRetainedTriangle(ConstructionMesh mesh, (Point3 Point, Point3 Normal)[] planes, double bound, ref long work, long maximum, CancellationToken cancellation)
    {
        if (planes.Length == 0) return mesh.Triangles.Count > 0;
        for (var i = 0; i < mesh.Triangles.Count; i += 3)
        {
            cancellation.ThrowIfCancellationRequested(); Need(++work <= maximum, "native view visibility work allowance exhausted");
            var polygon = new List<Point3> { mesh.Vertices[mesh.Triangles[i]], mesh.Vertices[mesh.Triangles[i + 1]], mesh.Vertices[mesh.Triangles[i + 2]] };
            foreach (var plane in planes)
            {
                Need(polygon.Count <= maximum - work, "native view clipping work allowance exhausted"); work += polygon.Count;
                if (polygon.Count == 0) break;
                var clipped = new List<Point3>(); var previous = polygon[^1]; var previousDistance = Dot(new(previous.X - plane.Point.X, previous.Y - plane.Point.Y, previous.Z - plane.Point.Z), plane.Normal) - bound;
                foreach (var point in polygon)
                {
                    var distance = Dot(new(point.X - plane.Point.X, point.Y - plane.Point.Y, point.Z - plane.Point.Z), plane.Normal) - bound;
                    if ((distance <= 0) != (previousDistance <= 0)) { var t = previousDistance / (previousDistance - distance); clipped.Add(new(previous.X + (point.X - previous.X) * t, previous.Y + (point.Y - previous.Y) * t, previous.Z + (point.Z - previous.Z) * t)); }
                    if (distance <= 0) clipped.Add(point);
                    previous = point; previousDistance = distance;
                }
                polygon = clipped;
            }
            for (var p = 1; p + 1 < polygon.Count; p++)
            {
                var a = polygon[0]; var b = polygon[p]; var c = polygon[p + 1]; var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z; var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
                if (uy * vz - uz * vy != 0 || uz * vx - ux * vz != 0 || ux * vy - uy * vx != 0) return true;
            }
        }
        return false;
    }

    static void ValidateLimits(ExportNativeLimits l)
    {
        Need(l.MaxSourceBytes is > 0 and <= 1_073_741_824 && l.MaxJobBytes is >= 2 and <= ExportJson.HardMaximumBytes && l.MaxOutputBytes is > 0 and <= 17_179_869_184 && l.MaxVertices is > 0 and <= 2_000_000 && l.MaxTriangles is > 0 and <= 4_000_000 && l.MaxNodes is > 0 and <= 100_000 && l.MaxMeshes is > 0 and <= 20_000 && l.MaxMaterials is > 0 and <= 4096 && l.MaxAccessors is > 0 and <= 100_000 && l.MaxPrimitives is > 0 and <= 100_000 && l.MaxGeometryChecks is > 0 and <= 2_000_000_000 && l.MaxViews is > 0 and <= 2048 && l.ImageSize == 768, "invalid finite native limits");
    }
    static void ValidateSourceLimits(ExportNativeLimits limits, BuildingPlan plan)
    {
        var profile = plan.Construction;
        Need(profile is not null && limits.MaxVertices <= profile.MaxConstructionVertices && limits.MaxTriangles <= plan.Budgets.MaxGeneratedTriangles && limits.MaxMeshes <= profile.MaxMeshes && limits.MaxGeometryChecks <= plan.Budgets.MaxGeometryChecks && limits.MaxJobBytes <= profile.MaxJobBytes, "export job widens approved source geometry/work/job allowances");
    }
    static void ValidateInputSet(ExportNativeJob job)
    {
        var i = job.Inputs; Need(i.ConstructionJob is not null && i.Plan is not null, "missing original source references");
        if (job.Operation == "produce") Need(i.SourceBlend is not null && i.ConstructionReport is not null && i.Package is null && i.Glb is null && i.VisualVerification is null && i.CollisionVerification is null && job.Identities.PackageSha256 is null, "wrong producer input set");
        if (job.Operation == "verify") Need(i.SourceBlend is null && i.ConstructionReport is null && i.Package is not null && i.Glb is not null && i.VisualVerification is null && i.CollisionVerification is null, "wrong verifier input set");
        if (job.Operation == "views") Need(i.SourceBlend is null && i.ConstructionReport is null && i.Package is not null && i.Glb is null && i.VisualVerification is not null && i.CollisionVerification is not null, "wrong views input set");
    }
    static IEnumerable<ExportNativeFile> InputFiles(ExportNativeInputs i) => new[] { i.SourceBlend, i.ConstructionJob, i.ConstructionReport, i.Plan, i.Package, i.Glb, i.VisualVerification, i.CollisionVerification }.OfType<ExportNativeFile>();
    static string[] DrawingPaths(BuildingPlan plan) => new[] { "roof-plan.svg", "elevation-north.svg", "elevation-south.svg", "elevation-east.svg", "elevation-west.svg", "section-principal.svg", "construction-details.svg" }.Concat(Enumerable.Range(1, plan.Storeys.Count).Select(i => $"floor-{i:D2}.svg")).Concat(Enumerable.Range(1, plan.Stairs.Count).Select(i => $"section-stair-{i:D2}.svg")).Select(p => "drawings/" + p).ToArray();
    static SortedDictionary<string, long> Inventory(IWorkspace workspace, string directory, long maximum, int count, CancellationToken cancellation)
    {
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal); var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(workspace.Resolve(directory)))
        {
            cancellation.ThrowIfCancellationRequested(); Need(result.Count < count, "output file-count cap exceeded"); var full = workspace.Resolve(path); Need(File.Exists(full) && !Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, "nonregular output entry"); var name = Path.GetFileName(full); Need(aliases.Add(name), "case-aliased output entry"); var size = new FileInfo(full).Length; Need(size > 0 && size <= maximum - total, "cumulative output bytes exceeded"); total += size; result.Add(name, size);
        }
        return result;
    }
    static void VerifyFile(IWorkspace workspace, ExportNativeFile file, long maximum, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); Need(Path.IsPathFullyQualified(file.Path) && !file.Path.Any(char.IsControl), "input/output reference must be confined absolute path"); var path = workspace.Resolve(file.Path);
        Need(path == file.Path, "input/output reference must be canonical");
        foreach (var part in Path.GetRelativePath(workspace.Root, path).Replace('\\', '/').Split('/')) Need(part is not ("" or "." or ".." or ".git") && !part.EndsWith('.') && !part.EndsWith(' ') && !part.Any(c => ":<>\"|?*".Contains(c)), "ambiguous input/output reference component");
        Hash(file.Sha256); Need(file.Bytes > 0 && file.Bytes <= maximum && File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && new FileInfo(path).Length == file.Bytes && FileHash(workspace, path, maximum, cancellation) == file.Sha256, "actual reference bytes/hash differ: " + Path.GetFileName(path));
    }
    static bool IsWithin(string path, string directory) { var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; var full = Path.GetFullPath(path); return full.Equals(directory, comparison) || full.StartsWith(directory + Path.DirectorySeparatorChar, comparison); }
    static string FileHash(IWorkspace workspace, string path, long maximum, CancellationToken cancellation) => ConstructionOutputVerifier.FileHash(workspace, path, cancellation, maximum);
    static JsonObject Object(JsonObject value, string key) { Need(value.TryGetPropertyValue(key, out var node), "missing field: " + key); return AsObject(node); }
    static JsonObject AsObject(JsonNode? node) { Need(node is JsonObject, "expected measured object"); return (JsonObject)node; }
    static JsonArray Array(JsonObject value, string key, int maximum) { Need(value[key] is JsonArray array && array.Count <= maximum, "expected bounded array: " + key); return (JsonArray)value[key]!; }
    static void Closed(JsonObject value, string[] keys) => Need(value.Count == keys.Length && keys.All(value.ContainsKey), "unknown/missing measured fields");
    static string Text(JsonObject value, string key, bool empty = false) { Need(value.TryGetPropertyValue(key, out var node), "missing text: " + key); return ScalarText(node, empty); }
    static string ScalarText(JsonNode? node, bool empty = false) { Need(node is JsonValue value && value.TryGetValue<string>(out var text) && text is not null && text.Length <= 512 && (empty || text.Length > 0) && !text.Any(char.IsControl), "expected bounded text"); return node!.GetValue<string>(); }
    static string? NullableText(JsonObject value, string key) { Need(value.ContainsKey(key), "missing nullable identity"); return value[key] is null ? null : ScalarText(value[key]); }
    static double Number(JsonObject value, string key, double minimum = -1_000_000_000, double maximum = 1_000_000_000) => Number(value[key], minimum, maximum);
    static double Number(JsonNode? node, double minimum = -1_000_000_000, double maximum = 1_000_000_000) { Need(node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) && number >= minimum && number <= maximum, "expected finite bounded number"); return node!.GetValue<double>(); }
    static long Integer(JsonObject value, string key, long minimum, long maximum) { var number = Number(value[key], minimum, maximum); Need(number == Math.Truncate(number), "expected integral measured value"); return checked((long)number); }
    static void True(JsonObject value, string key) => Boolean(value, key, true);
    static void False(JsonObject value, string key) => Boolean(value, key, false);
    static void Boolean(JsonObject value, string key, bool wanted) => Need(value[key] is JsonValue node && node.TryGetValue<bool>(out var actual) && actual == wanted, "false/missing evidence flag: " + key);
    static double[] Numbers(JsonObject value, string key, int length) { var array = Array(value, key, length); Need(array.Count == length, "wrong vector/matrix length"); return array.Select(n => Number(n)).ToArray(); }
    static Point3 Point(JsonNode? value)
    {
        if (value is JsonObject point) { Closed(point, ["x", "y", "z"]); return new(Number(point, "x"), Number(point, "y"), Number(point, "z")); }
        Need(value is JsonArray array && array.Count == 3, "expected finite XYZ vector"); var p = (JsonArray)value; return new(Number(p[0]), Number(p[1]), Number(p[2]));
    }
    static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static double Distance(Point3 a, Point3 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
    static Point3 Mid(Point3 a, Point3 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
    static bool Near(Point3 a, Point3 b, double tolerance) => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance && Math.Abs(a.Z - b.Z) <= tolerance;
    static void EqualNumber(double actual, double expected, string message) => Need(Math.Abs(actual - expected) <= Math.Max(1e-12, Math.Abs(expected) * 1e-12), message);
    static void Strings(JsonObject value, string key, IEnumerable<string> expected) { var wanted = expected.Order(StringComparer.Ordinal).ToArray(); var array = Array(value, key, 100_000); Need(array.Count == wanted.Length && array.Select(n => ScalarText(n)).SequenceEqual(wanted, StringComparer.Ordinal), "source feature identities differ: " + key); }
    static void Same<T>(T actual, T expected, string message) => Need(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(actual, StrictPlanJson.Options), JsonSerializer.SerializeToNode(expected, StrictPlanJson.Options)), message);
    static void SameNode<T>(JsonNode? actual, T expected, string message) => Need(JsonNode.DeepEquals(actual, JsonSerializer.SerializeToNode(expected, StrictPlanJson.Options)), message);
    static void Hash(string value) => Need(value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "invalid lowercase SHA-256");
    static void Need([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new InvalidOperationException("Export output: " + message); }
    sealed class ReadLeases(IWorkspace workspace) : IDisposable
    {
        readonly Dictionary<string, FileStream> files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        internal void Add(string path) { var full = workspace.Resolve(path); if (!files.ContainsKey(full)) files.Add(full, new(full, FileMode.Open, FileAccess.Read, FileShare.Read)); }
        public void Dispose() { foreach (var file in files.Values) file.Dispose(); }
    }

    static void Png(string path, int imageSize, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); Need(stream.Length is >= 57 and <= PngCap, "PNG exceeds fixed image cap");
        Span<byte> signature = stackalloc byte[8]; stream.ReadExactly(signature); Need(signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "invalid PNG signature");
        using var compressed = new MemoryStream(); var first = true; var ended = false; var dataEnded = false; var seenData = false; var chunks = 0;
        Span<byte> header = stackalloc byte[8]; Span<byte> checksum = stackalloc byte[4];
        while (stream.Position < stream.Length)
        {
            cancellation.ThrowIfCancellationRequested(); Need(++chunks <= 1024 && !ended, "invalid PNG chunk inventory"); stream.ReadExactly(header); var length = BinaryPrimitives.ReadUInt32BigEndian(header); Need(length <= PngCap && length <= stream.Length - stream.Position - 4, "invalid PNG chunk length"); var name = System.Text.Encoding.ASCII.GetString(header[4..]); var bytes = new byte[(int)length]; stream.ReadExactly(bytes); stream.ReadExactly(checksum);
            var crc = 0xffffffffu; foreach (var b in header[4..]) crc = Crc(crc, b); for (var i = 0; i < bytes.Length; i++) { if ((i & 65535) == 0) cancellation.ThrowIfCancellationRequested(); crc = Crc(crc, bytes[i]); } Need((crc ^ 0xffffffffu) == BinaryPrimitives.ReadUInt32BigEndian(checksum), "PNG CRC differs");
            if (first) { Need(name == "IHDR" && length == 13 && BinaryPrimitives.ReadUInt32BigEndian(bytes) == imageSize && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)) == imageSize && bytes[8] == 8 && bytes[9] == 2 && bytes[10] == 0 && bytes[11] == 0 && bytes[12] == 0, "PNG fixed RGB dimensions/format differ"); first = false; continue; }
            Need(name != "IHDR", "duplicate PNG header");
            if (name == "IDAT") { Need(!dataEnded && compressed.Length + length <= PngCap, "noncontiguous/oversized PNG data"); compressed.Write(bytes); seenData = true; }
            else { if (seenData) dataEnded = true; if (name == "IEND") { Need(length == 0 && seenData && stream.Position == stream.Length, "invalid PNG end"); ended = true; } else Need(name.Length == 4 && char.IsLower(name[0]), "unsupported critical PNG chunk"); }
        }
        Need(ended, "missing PNG end"); compressed.Position = 0; using var inflate = new ZLibStream(compressed, CompressionMode.Decompress); var rowSize = checked(imageSize * 3 + 1); var expected = checked((long)imageSize * rowSize); var buffer = new byte[65_536]; long total = 0;
        while (true) { cancellation.ThrowIfCancellationRequested(); var read = inflate.Read(buffer); if (read == 0) break; Need(read <= expected - total, "PNG decompressed image exceeds dimensions"); for (var i = 0; i < read; i++) if ((total + i) % rowSize == 0) Need(buffer[i] <= 4, "invalid PNG row filter"); total += read; }
        Need(total == expected, "PNG decompressed image is incomplete"); cancellation.ThrowIfCancellationRequested();
    }
    static uint Crc(uint value, byte next) { value ^= next; for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xedb88320u : value >> 1; return value; }
}
