using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

namespace Bubo.FloorPlan.Domain;

/// <summary>Pure bounded relationship validation. Filesystem hashing, tool freshness and measured GLB verification remain external.</summary>
public static partial class ExportPackageValidator
{
    public const string PackageSchema = "bubo-floor-plan-export-package/v1";
    public const string SemanticsSchema = "bubo-floor-plan-semantics/v1";
    public const string ModelReviewSchema = "bubo-floor-plan-model-review/v1";
    public const int MaxInventoryFiles = 2048;
    public const int MaxToolFiles = 8192;
    public const int MaxReviewArtifacts = 4096;
    public const long MaxPayloadBytes = 1_073_741_824;
    public const long MaxToolBytes = 268_435_456;
    public const int MaxSemanticBytes = 67_108_864;
    public const int MaxMeshes = 10_000;
    public const int MaxAnchors = 32_768;
    public const int MaxSourceVertices = 2_000_000;
    public const int MaxSourceTriangles = 10_000_000;
    public const string DoorPoseConvention = "closed-at-zero-final-open-clearance";
    public const string ClearanceBasis = "construction-source";
    private static readonly string[] FixedPayload = ["building.blend", "building-plan.json", "collision.glb", "provenance/construction-report.json", "provenance/design-approval.json", "provenance/design-preview.json", "semantics.json", "visual.glb"];
    private static readonly double[] Transform = [1, 0, 0, 0, 0, 1, 0, -1, 0];
    private static readonly JsonSerializerOptions SnapshotJson = CreateSnapshotOptions();

    private static JsonSerializerOptions CreateSnapshotOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 64, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    public static ExportCoordinateSystem Coordinates() => new("xy-metres-z-up", "gltf-metres-y-up", [.. Transform]);

    public static void RequireValidPackage(FloorPlanExportPackage package, IReadOnlyList<string> approvedDrawingPaths, long maxPayloadBytes = MaxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(package); ArgumentNullException.ThrowIfNull(approvedDrawingPaths);
        Require(package.SchemaVersion == PackageSchema, "Unknown export package schema");
        Identity(package.Task); Identity(package.Asset); Identity(package.Version);
        Require(package.Ancestry is not null && package.Numerics is not null, "Missing package ancestry/numerics");
        RequireCoordinates(package.Coordinates); RequireNumerics(package.Numerics);
        Require(maxPayloadBytes is > 0 and <= MaxPayloadBytes, "Invalid payload allowance");
        var ancestry = package.Ancestry;
        Text(ancestry.ModelAuthor, 128);
        foreach (var hash in new[] { ancestry.PlanSha256, ancestry.DesignFingerprint, ancestry.ApprovalSha256, ancestry.RuntimeSha256, ancestry.CatalogSha256, ancestry.ConstructionJobSha256, ancestry.ConstructionReportSha256, ancestry.SourceSha256 }) Hash(hash);
        RelativePath(ancestry.ConstructionAttempt);
        Require(ancestry.Bundle is { Count: > 0 and <= 32 }, "Missing/oversized fixed export bundle");
        var bundleAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in ancestry.Bundle)
        {
            RelativePath(name); Require(!name.Contains('/') && name.EndsWith(".py", StringComparison.Ordinal) && bundleAliases.Add(name), "Bundle names must be distinct portable fixed Python basenames"); Hash(hash);
        }
        Require(ancestry.Toolchain is not null, "Missing portable toolchain identity");
        Hash(ancestry.Toolchain.BlenderSha256); Text(ancestry.Toolchain.BlenderVersion, 128);
        Inventory(ancestry.Toolchain.GltfFiles, MaxToolFiles, MaxToolBytes);
        var files = Inventory(package.Files, MaxInventoryFiles, maxPayloadBytes);
        Require(approvedDrawingPaths.Count is > 0 and <= MaxInventoryFiles - 8, "Missing/oversized approved drawing set");
        var expected = new HashSet<string>(FixedPayload, StringComparer.Ordinal);
        foreach (var path in approvedDrawingPaths)
        {
            RelativePath(path);
            Require(path.StartsWith("drawings/", StringComparison.Ordinal) && path.Count(c => c == '/') == 1 && path.EndsWith(".svg", StringComparison.Ordinal), "Approved drawing must be an exact drawings/*.svg entry");
            Require(expected.Add(path), "Duplicate approved drawing");
        }
        Require(expected.SetEquals(files.Keys), "Payload inventory must contain exactly the fixed payload and complete approved drawings, excluding package.json and external reviews");
        Require(files["building-plan.json"].Sha256 == ancestry.PlanSha256 && files["building.blend"].Sha256 == ancestry.SourceSha256 && files["provenance/construction-report.json"].Sha256 == ancestry.ConstructionReportSha256 && files["provenance/design-approval.json"].Sha256 == ancestry.ApprovalSha256, "Payload files do not bind exact immutable source ancestry");
    }

    public static FloorPlanSemantics CreateSemantics(BuildingPlan plan, ConstructionScene scene, string planSha256, string constructionJobSha256, string sourceSha256, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(scene);
        Hash(planSha256); Hash(constructionJobSha256); Hash(sourceSha256);
        RequireNumerics(plan.Numerics);
        Require(plan.SchemaVersion == "bubo-building-plan/v2" && plan.Construction is not null, "Export requires an explicit construction-ready v2 design");
        Require(scene.SchemaVersion == "bubo-floor-plan-construction-scene/v1" && scene.PlanId == plan.Id && scene.LinearTolerance == plan.Numerics.LinearTolerance, "Construction scene belongs to another plan/numerical policy");
        Require(scene.Meshes is { Count: > 0 and <= MaxMeshes } && scene.Meshes.Count <= plan.Construction.MaxMeshes, "Invalid construction mesh inventory");
        Require(scene.Anchors is { Count: > 0 and <= MaxAnchors } && scene.Materials is { Count: > 0 and <= 4096 } && scene.Clearance is not null, "Missing/oversized source semantics");
        var floors = plan.Storeys.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var materials = scene.Materials.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var anchors = scene.Anchors.ToDictionary(a => a.Id, StringComparer.Ordinal);
        Require(anchors.Count == scene.Anchors.Count && materials.Count == scene.Materials.Count, "Duplicate semantic anchor/material");
        var meshes = new List<ExportMeshIdentity>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        long vertices = 0, triangles = 0;
        foreach (var mesh in scene.Meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(mesh is not null && ids.Add(mesh.Id), "Missing/duplicate source mesh");
            Require(floors.Contains(mesh.StoreyId) && materials.Contains(mesh.MaterialId), "Unresolved source floor/material identity");
            Require(mesh.Representation is "visual" or "collision" && (mesh.Representation == "visual" ? mesh.CollisionRole == "none" : mesh.CollisionRole is "static" or "dynamic"), "Invalid source representation/collision role");
            Require(mesh.ParentAnchorId is null || anchors.TryGetValue(mesh.ParentAnchorId, out var anchor) && anchor.StoreyId == mesh.StoreyId, "Unresolved/cross-storey mesh pivot");
            Require(mesh.CollisionRole != "dynamic" || mesh.ParentAnchorId is not null, "Dynamic collision requires an explicit pivot");
            Require(mesh.Vertices is { Count: > 0 } && mesh.Triangles is { Count: > 0 } && mesh.Triangles.Count % 3 == 0, "Invalid source triangle mesh");
            vertices += mesh.Vertices.Count; triangles += mesh.Triangles.Count / 3;
            Require(vertices <= Math.Min(MaxSourceVertices, plan.Construction.MaxConstructionVertices) && triangles <= Math.Min(MaxSourceTriangles, plan.Budgets.MaxGeneratedTriangles), "Source geometry exceeds approved budgets/hard caps");
            foreach (var point in mesh.Vertices) { cancellationToken.ThrowIfCancellationRequested(); RequirePoint(point, plan.Numerics.MaxCoordinateMagnitude + plan.Numerics.LinearTolerance); }
            foreach (var index in mesh.Triangles) { cancellationToken.ThrowIfCancellationRequested(); Require(index >= 0 && index < mesh.Vertices.Count, "Source index out of bounds"); }
            Require(mesh.TriangleMaterialIds is null || mesh.TriangleMaterialIds.Count == mesh.Triangles.Count / 3 && mesh.TriangleMaterialIds.All(materials.Contains), "Source triangle material mapping is incomplete/unresolved");
            meshes.Add(new()
            {
                Id = mesh.Id, NodeId = mesh.Id, ParentNodeId = mesh.ParentAnchorId ?? "representation:" + (mesh.Representation == "visual" ? "visual" : "collision-" + mesh.CollisionRole) + ":" + mesh.StoreyId,
                SemanticId = mesh.SemanticId, StoreyId = mesh.StoreyId, Kind = mesh.Kind, Representation = mesh.Representation, CollisionRole = mesh.CollisionRole,
                MaterialId = mesh.MaterialId, ParentAnchorId = mesh.ParentAnchorId, TriangleMaterialIds = mesh.TriangleMaterialIds is null ? null : [.. mesh.TriangleMaterialIds],
                SourceVertexCount = mesh.Vertices.Count, SourceTriangleCount = mesh.Triangles.Count / 3,
                Minimum = new(mesh.Vertices.Min(p => p.X), mesh.Vertices.Min(p => p.Y), mesh.Vertices.Min(p => p.Z)),
                Maximum = new(mesh.Vertices.Max(p => p.X), mesh.Vertices.Max(p => p.Y), mesh.Vertices.Max(p => p.Z))
            });
        }
        foreach (var anchor in scene.Anchors)
        {
            Require(floors.Contains(anchor.StoreyId), "Unresolved source anchor floor");
            RequirePoint(anchor.Position, plan.Numerics.MaxCoordinateMagnitude + plan.Numerics.LinearTolerance);
            Require(anchor.Axis == new Point3(0, 0, 1) && anchor.RotationRadians == 0, "Source anchors must preserve approved Z-up closed-at-zero convention");
        }
        var semantics = new FloorPlanSemantics
        {
            SchemaVersion = SemanticsSchema, PlanSha256 = planSha256, ConstructionJobSha256 = constructionJobSha256, SourceSha256 = sourceSha256,
            Coordinates = Coordinates(), Plan = plan, Materials = scene.Materials, Meshes = meshes, Anchors = scene.Anchors,
            DoorPoseConvention = DoorPoseConvention, ClearanceBasis = ClearanceBasis, Clearance = scene.Clearance
        };
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SnapshotClone(semantics, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    public static void RequireValidSemantics(FloorPlanSemantics semantics, BuildingPlan plan, ConstructionScene scene, string planSha256, string constructionJobSha256, string sourceSha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        // Exact approved source snapshots avoid accepting omitted rooms/openings or changed nominal dimensions from mesh bounds.
        var expected = CreateSemantics(plan, scene, planSha256, constructionJobSha256, sourceSha256, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var actualBytes = SnapshotBytes(semantics, cancellationToken);
        var expectedBytes = SnapshotBytes(expected, cancellationToken);
        var equal = actualBytes.Length == expectedBytes.Length;
        for (var offset = 0; equal && offset < actualBytes.Length; offset += 65_536)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(65_536, actualBytes.Length - offset);
            equal = actualBytes.AsSpan(offset, count).SequenceEqual(expectedBytes.AsSpan(offset, count));
        }
        cancellationToken.ThrowIfCancellationRequested();
        Require(equal, "Semantics differ from the exact approved plan/construction identities, dimensions, materials, pivots, node mapping or source clearance evidence");
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static void RequireMatchingSemantics(FloorPlanExportPackage package, FloorPlanSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(package); ArgumentNullException.ThrowIfNull(semantics);
        Require(package.Ancestry is not null && semantics.Plan is not null, "Missing package/semantic source identities");
        Require(package.Ancestry.PlanSha256 == semantics.PlanSha256 && package.Ancestry.ConstructionJobSha256 == semantics.ConstructionJobSha256 && package.Ancestry.SourceSha256 == semantics.SourceSha256, "Package and semantics disagree on exact source ancestry");
        RequireCoordinates(package.Coordinates); RequireCoordinates(semantics.Coordinates);
        Require(JsonSerializer.SerializeToUtf8Bytes(package.Numerics, SnapshotJson).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(semantics.Plan.Numerics, SnapshotJson)), "Package and semantic plan disagree on approved numerical policy");
    }

    public static void RequireValidModelReview(FloorPlanModelReview review, ModelReviewBinding expected)
    {
        ArgumentNullException.ThrowIfNull(review); ArgumentNullException.ThrowIfNull(expected);
        Require(review.SchemaVersion == ModelReviewSchema && review.Kind == expected.Kind && review.Kind is "independent-model" or "human-model", "Unknown/mismatched model review kind/schema");
        Require(review.Decision is "approved" or "rejected", "A model review must explicitly approve or reject");
        Text(review.Reviewer, 128); Text(review.Role, 128); Text(review.Reason, 4096); Text(expected.Author, 128);
        foreach (var hash in new[] { review.PackageSha256, review.VisualVerificationSha256, review.CollisionVerificationSha256, review.ViewsSha256 }) Hash(hash);
        Require(review.PackageSha256 == expected.PackageSha256 && review.VisualVerificationSha256 == expected.VisualVerificationSha256 && review.CollisionVerificationSha256 == expected.CollisionVerificationSha256 && review.ViewsSha256 == expected.ViewsSha256, "Model review binds stale package/verification/views");
        if (review.Kind == "independent-model")
            Require(!review.Human && review.IndependentReviewSha256 is null && expected.IndependentReviewSha256 is null && !string.Equals(review.Reviewer.Trim(), expected.Author.Trim(), StringComparison.OrdinalIgnoreCase), "Independent review must be separate from author/human approval and cannot inherit another review");
        else
        {
            Hash(review.IndependentReviewSha256); Hash(expected.IndependentReviewSha256);
            Require(review.Human && review.IndependentReviewSha256 == expected.IndependentReviewSha256, "Human model decision requires the current independently approved review identity");
        }
        var artifacts = Inventory(review.Artifacts, MaxReviewArtifacts, 4L * MaxPayloadBytes);
        var current = Inventory(expected.Artifacts, MaxReviewArtifacts, 4L * MaxPayloadBytes);
        Require(artifacts.Count == current.Count && artifacts.All(pair => current.TryGetValue(pair.Key, out var file) && file == pair.Value), "Review must bind every exact current reviewed artifact with no omissions/extras");
    }

    public static void RequireCoordinates(ExportCoordinateSystem coordinates)
    {
        Require(coordinates is not null && coordinates.Source == "xy-metres-z-up" && coordinates.Target == "gltf-metres-y-up" && coordinates.SourceToGltf is not null && coordinates.SourceToGltf.SequenceEqual(Transform), "Coordinate mapping must be exactly p_gltf=(x,z,-y), without scale or datum changes");
    }

    private static void RequireNumerics(NumericalPolicy n)
    {
        Require(double.IsFinite(n.LinearTolerance) && n.LinearTolerance is >= 1e-9 and <= .01 && double.IsFinite(n.AngularToleranceDegrees) && n.AngularToleranceDegrees is >= .001 and <= 1 && double.IsFinite(n.MaxCoordinateMagnitude) && n.MaxCoordinateMagnitude is > 0 and <= 1_000_000 && double.IsFinite(n.MinimumFeatureSize) && n.MinimumFeatureSize >= 10 * n.LinearTolerance && n.MinimumFeatureSize <= 10, "Invalid approved numerical policy");
    }

    private static byte[] SnapshotBytes<T>(T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new BoundedSnapshotStream(cancellationToken);
        JsonSerializer.Serialize(stream, value, SnapshotJson);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = stream.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    private static T SnapshotClone<T>(T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new BoundedSnapshotStream(cancellationToken);
        JsonSerializer.Serialize(stream, value, SnapshotJson);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        var snapshot = JsonSerializer.Deserialize<T>(stream, SnapshotJson)!;
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    private sealed class BoundedSnapshotStream(CancellationToken cancellationToken) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { CheckWrite(count); base.Write(buffer, offset, count); cancellationToken.ThrowIfCancellationRequested(); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckWrite(buffer.Length); base.Write(buffer); cancellationToken.ThrowIfCancellationRequested(); }
        public override void WriteByte(byte value) { CheckWrite(1); base.WriteByte(value); cancellationToken.ThrowIfCancellationRequested(); }
        public override int Read(byte[] buffer, int offset, int count) { cancellationToken.ThrowIfCancellationRequested(); var read = base.Read(buffer, offset, count); cancellationToken.ThrowIfCancellationRequested(); return read; }
        public override int Read(Span<byte> buffer) { cancellationToken.ThrowIfCancellationRequested(); var read = base.Read(buffer); cancellationToken.ThrowIfCancellationRequested(); return read; }
        public override int ReadByte() { cancellationToken.ThrowIfCancellationRequested(); var value = base.ReadByte(); cancellationToken.ThrowIfCancellationRequested(); return value; }
        private void CheckWrite(int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(count <= MaxSemanticBytes - Length, "Semantic metadata exceeds its finite byte cap");
        }
    }

    private static Dictionary<string, ExportFileEntry> Inventory(IReadOnlyList<ExportFileEntry> files, int maxFiles, long maxBytes)
    {
        Require(files is { Count: > 0 } && files.Count <= maxFiles, "Missing/oversized file inventory");
        var result = new Dictionary<string, ExportFileEntry>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? previous = null; long total = 0;
        foreach (var file in files)
        {
            Require(file is not null, "Null inventory entry"); RelativePath(file.Path); Hash(file.Sha256);
            Require(file.Bytes > 0 && file.Bytes <= maxBytes - total, "File inventory exceeds its cumulative byte allowance"); total += file.Bytes;
            Require(previous is null || StringComparer.Ordinal.Compare(previous, file.Path) < 0, "Inventory paths must be strictly ordinal sorted");
            Require(aliases.Add(file.Path) && result.TryAdd(file.Path, file), "Duplicate/case-aliased inventory path"); previous = file.Path;
        }
        // A file cannot also be an ancestor directory, including a case alias on Windows.
        foreach (var path in result.Keys)
        {
            var slash = path.IndexOf('/');
            while (slash >= 0) { Require(!aliases.Contains(path[..slash]), "File/directory inventory path collision"); slash = path.IndexOf('/', slash + 1); }
        }
        return result;
    }

    private static void RelativePath(string path)
    {
        Require(path is { Length: > 0 and <= 512 } && path.All(c => c is >= ' ' and <= '~') && !path.Contains('\\') && !path.Contains(':') && !path.StartsWith('/') && !path.EndsWith('/'), "Expected bounded portable slash-separated relative path");
        foreach (var segment in path.Split('/'))
        {
            Require(segment is not ("" or "." or "..") && segment.Length <= 128 && !segment.EndsWith(' ') && !segment.EndsWith('.') && !segment.Any(c => "<>\"|?*".Contains(c)), "Unsafe portable path component");
            var stem = segment.Split('.')[0];
            Require(!ReservedDevice().IsMatch(stem), "Windows device path is not portable");
        }
    }

    private static void Identity(string value) => Require(value is not null && IdPattern().IsMatch(value), "Invalid bounded task/asset/version identity");
    private static void Hash(string? value) => Require(value is not null && HashPattern().IsMatch(value), "Expected lowercase SHA-256");
    private static void Text(string value, int max) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl), "Missing/oversized/control-bearing text");
    private static void RequirePoint(Point3 point, double max) => Require(double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z) && Math.Abs(point.X) <= max && Math.Abs(point.Y) <= max && Math.Abs(point.Z) <= max, "Nonfinite/out-of-policy source coordinate");
    private static void Require([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\\z", RegexOptions.CultureInvariant)] private static partial Regex IdPattern();
    [GeneratedRegex("^[a-f0-9]{64}\\z", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ReservedDevice();
}
