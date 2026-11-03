using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bubo.FloorPlan.Domain;
using Hephaera.AssetProduction;
namespace Bubo.FloorPlan.Infrastructure;

public static class ConstructionOutputVerifier
{
    static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException("Construction output: " + message); }
    static void Hash(string value) => Need(value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$"), "invalid content identity");
    static bool Near(Point3 a, Point3 b, double tolerance) => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance && Math.Abs(a.Z - b.Z) <= tolerance;
    public static string IndexHash(ConstructionMesh mesh, CancellationToken cancellation = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4];
        foreach (var index in mesh.Triangles) { cancellation.ThrowIfCancellationRequested(); BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)index)); hash.AppendData(buffer); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static string MaterialHash(ConstructionMesh mesh, CancellationToken cancellation = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4];
        for (var i = 0; i < mesh.Triangles.Count / 3; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetBytes(mesh.TriangleMaterialIds?[i] ?? mesh.MaterialId);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)bytes.Length)); hash.AppendData(buffer); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static string SourceGeometryHash(ConstructionMesh mesh, CancellationToken cancellation = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[24];
        foreach (var point in mesh.Vertices)
        {
            cancellation.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt64LittleEndian(buffer, BitConverter.DoubleToInt64Bits(point.X == 0 ? 0 : point.X));
            BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], BitConverter.DoubleToInt64Bits(point.Y == 0 ? 0 : point.Y));
            BinaryPrimitives.WriteInt64LittleEndian(buffer[16..], BitConverter.DoubleToInt64Bits(point.Z == 0 ? 0 : point.Z));
            hash.AppendData(buffer);
        }
        foreach (var index in mesh.Triangles) { cancellation.ThrowIfCancellationRequested(); BinaryPrimitives.WriteUInt32LittleEndian(buffer[..4], checked((uint)index)); hash.AppendData(buffer[..4]); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    static int Components(ConstructionMesh mesh, CancellationToken cancellation)
    {
        var parent = Enumerable.Range(0, mesh.Vertices.Count).ToArray(); var used = new bool[parent.Length];
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        for (var i = 0; i < mesh.Triangles.Count; i += 3)
        {
            cancellation.ThrowIfCancellationRequested();
            var a = mesh.Triangles[i]; var b = mesh.Triangles[i + 1]; var c = mesh.Triangles[i + 2];
            used[a] = used[b] = used[c] = true; parent[Find(b)] = Find(a); parent[Find(c)] = Find(a);
        }
        Need(used.All(value => value), "source mesh contains unused vertices");
        return Enumerable.Range(0, parent.Length).Select(Find).Distinct().Count();
    }
    public static string FileHash(IWorkspace workspace, string path, CancellationToken cancellation = default, long maximumBytes = long.MaxValue)
    {
        cancellation.ThrowIfCancellationRequested();
        using var stream = new FileStream(workspace.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        Need(stream.Length <= maximumBytes, "file exceeds its declared byte bound before hashing");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; int read; long total = 0;
        while ((read = stream.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); Need(read <= maximumBytes - total, "file grew beyond its declared byte bound"); total = checked(total + read); hash.AppendData(buffer.AsSpan(0, read)); }
        cancellation.ThrowIfCancellationRequested(); return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static ConstructionReport Verify(IWorkspace workspace, string outputDirectory, ConstructionBlenderJob job, string jobSha256, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        VerifyInventory(workspace, outputDirectory, job.Limits.MaxOutputBytes);
        var report = StrictPlanJson.Read<ConstructionReport>(workspace.Resolve(outputDirectory + "/construction-report.json"));
        Need(report.SchemaVersion == "bubo-floor-plan-construction-report/v1" && report.JobSha256 == jobSha256 && report.Identities == job.Identities, "job/design/runtime identity differs");
        Need(report.Bundle.Count == job.Bundle.Count && job.Bundle.All(p => report.Bundle.GetValueOrDefault(p.Key) == p.Value), "authoring bundle identity differs");
        Need(Regex.IsMatch(report.BlenderVersion, "^4\\.5\\.[0-9]+(?: LTS)?$"), "Blender 4.5 LTS is required");
        Need(!report.IndependentReimport && !report.HumanModelAccepted && !report.RuntimeAccepted, "construction cannot assert later reviews/acceptance");
        var scene = job.Scene; var tolerance = scene.LinearTolerance;
        Need(double.IsFinite(report.MaxWorldCoordinateError) && report.MaxWorldCoordinateError >= 0 && report.MaxWorldCoordinateError <= tolerance, "coordinate error exceeds approved tolerance");
        Need(Near(report.Minimum, scene.Metrics.Minimum, tolerance) && Near(report.Maximum, scene.Metrics.Maximum, tolerance), "scene bounds differ");
        Need(report.Counts == new ConstructionCounts(scene.Meshes.Count, scene.Anchors.Count, scene.Materials.Count, scene.Metrics.VertexCount, scene.Metrics.TriangleCount, scene.Metrics.VisualTriangleCount, scene.Metrics.CollisionTriangleCount), "actual inventory/counts differ");
        Need(report.Meshes.Count == scene.Meshes.Count && report.Meshes.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() == report.Meshes.Count, "duplicate or missing mesh identity");
        var observed = report.Meshes.ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var mesh in scene.Meshes)
        {
            cancellation.ThrowIfCancellationRequested();
            Need(observed.TryGetValue(mesh.Id, out var actual), "missing mesh " + mesh.Id); var a = actual!;
            Need(a.SemanticId == mesh.SemanticId && a.StoreyId == mesh.StoreyId && a.Kind == mesh.Kind && a.Representation == mesh.Representation && a.CollisionRole == mesh.CollisionRole && a.MaterialId == mesh.MaterialId && a.ParentAnchorId == mesh.ParentAnchorId, "semantic ownership differs: " + mesh.Id);
            Need(a.VertexCount == mesh.Vertices.Count && a.TriangleCount == mesh.Triangles.Count / 3 && a.Manifold && a.ComponentCount == Components(mesh, cancellation) && double.IsFinite(a.MinimumComponentVolume) && a.MinimumComponentVolume > 0, "mesh topology differs: " + mesh.Id);
            var minimum = new Point3(mesh.Vertices.Min(v => v.X), mesh.Vertices.Min(v => v.Y), mesh.Vertices.Min(v => v.Z));
            var maximum = new Point3(mesh.Vertices.Max(v => v.X), mesh.Vertices.Max(v => v.Y), mesh.Vertices.Max(v => v.Z));
            Need(Near(a.Minimum, minimum, tolerance) && Near(a.Maximum, maximum, tolerance) && double.IsFinite(a.MaxWorldCoordinateError) && a.MaxWorldCoordinateError >= 0 && a.MaxWorldCoordinateError <= tolerance, "mesh coordinates differ: " + mesh.Id);
            Hash(a.GeometrySha256); Need(a.SourceGeometrySha256 == SourceGeometryHash(mesh, cancellation) && a.IndexSha256 == IndexHash(mesh, cancellation) && a.MaterialAssignmentsSha256 == MaterialHash(mesh, cancellation), "source geometry/indices/material assignments differ: " + mesh.Id);
            var materials = Enumerable.Range(0, a.TriangleCount).Select(i => mesh.TriangleMaterialIds?[i] ?? mesh.MaterialId).GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            Need(materials.Count == a.MaterialTriangleCounts.Count && materials.All(p => a.MaterialTriangleCounts.GetValueOrDefault(p.Key) == p.Value), "material face inventory differs: " + mesh.Id);
        }
        Need(report.Anchors.Count == scene.Anchors.Count && report.Anchors.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() == report.Anchors.Count, "anchor inventory differs");
        foreach (var anchor in scene.Anchors)
        {
            cancellation.ThrowIfCancellationRequested();
            var actual = report.Anchors.SingleOrDefault(a => a.Id == anchor.Id);
            Need(actual is not null && actual.SemanticId == anchor.SemanticId && actual.StoreyId == anchor.StoreyId && actual.Kind == anchor.Kind && Near(actual.Position, anchor.Position, tolerance) && Near(actual.Axis, anchor.Axis, tolerance) && Math.Abs(actual.RotationRadians - anchor.RotationRadians) <= tolerance, "pivot transform differs: " + anchor.Id);
        }
        Need(JsonSerializer.Serialize(report.Materials.OrderBy(m => m.Id), StrictPlanJson.Options) == JsonSerializer.Serialize(scene.Materials.OrderBy(m => m.Id), StrictPlanJson.Options), "material identity differs");
        Need(report.Blend.Path == "building.blend" && report.Blend.Bytes > 0, "invalid editable source identity"); Hash(report.Blend.Sha256);
        var blend = workspace.Resolve(outputDirectory + "/building.blend");
        Need(new FileInfo(blend).Length == report.Blend.Bytes && FileHash(workspace, outputDirectory + "/building.blend", cancellation, job.Limits.MaxOutputBytes) == report.Blend.Sha256, "editable source bytes changed");
        cancellation.ThrowIfCancellationRequested();
        return report;
    }
    public static void VerifyInventory(IWorkspace workspace, string outputDirectory, long maximumBytes)
    {
        var files = Directory.EnumerateFileSystemEntries(workspace.Resolve(outputDirectory)).Take(3).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Need(files.SetEquals(new[] { "building.blend", "construction-report.json" }), "unexpected or missing output file");
        long total = 0;
        foreach (var name in files)
        {
            var full = workspace.Resolve(outputDirectory + "/" + name); Need(File.Exists(full), "output entry is not a regular file");
            var size = new FileInfo(full).Length;
            Need(size > 0 && size <= maximumBytes - total, "output exceeds approved byte budget"); total = checked(total + size);
        }
    }
}
