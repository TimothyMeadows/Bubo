using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bubo.Schematic.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Infrastructure;

/// <summary>Native checks of the independently reopened editable prototypes and binary measured geometry.</summary>
public static class PartOutputVerifier
{
    static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException("Part output: " + message); }
    static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    static bool Near(double a, double b, double tolerance) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= tolerance;
    static bool Near(Vec3 a, Vec3 b, double tolerance) => Near(a.X, b.X, tolerance) && Near(a.Y, b.Y, tolerance) && Near(a.Z, b.Z, tolerance);
    static string JsonHash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, StrictDesignJson.Options)));
    public static Dictionary<string, string> PartIdentities(AssemblyDesign design)
    {
        var materials = design.Materials.ToDictionary(m => m.Id, StringComparer.Ordinal);
        return design.Parts.ToDictionary(p => p.Id, p => JsonHash(new { definition = p, material = materials[p.MaterialId], numerics = design.Numerics }), StringComparer.Ordinal);
    }
    public static string FileHash(IWorkspace workspace, string path, CancellationToken cancellation = default, long maximumBytes = long.MaxValue)
    {
        cancellation.ThrowIfCancellationRequested();
        using var file = new FileStream(workspace.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        Need(file.Length > 0 && file.Length <= maximumBytes, "file exceeds its finite byte limit");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long total = 0; int read;
        while ((read = file.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested(); Need(read <= maximumBytes - total, "file grew beyond its byte limit");
            total = checked(total + read); hash.AppendData(buffer.AsSpan(0, read));
        }
        cancellation.ThrowIfCancellationRequested(); return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static PartsReport Verify(IWorkspace workspace, string output, PartBuildJob job, string jobSha256, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        new PartConstructionReadiness().RequireReady(job.Design, cancellation);
        Need(job.SchemaVersion == "bubo-schematic-part-job/v1" && job.Operation == "build-parts" && job.ProjectRoot == workspace.Root
            && job.Output == workspace.Resolve(output) && Hash(jobSha256), "job identity/layout differs");
        Need(job.Limits.MaxVertices is > 0 and <= PartConstructionReadiness.MaximumVertices && job.Limits.MaxTriangles is > 0 and <= PartConstructionReadiness.MaximumTriangles
            && job.Limits.MaxTriangles <= job.Design.Budgets.MaxGeneratedTriangles && job.Limits.MaxOutputBytes == job.Design.Budgets.MaxExportBytes
            && job.Limits.MaxJobBytes == PartConstructionReadiness.MaximumJobBytes && job.Limits.MaxFiles == checked(job.Design.Parts.Count * 2 + 2), "job limits differ from approved/hard bounds");
        var expectedIdentities = PartIdentities(job.Design);
        Need(job.PartIdentities.Count == expectedIdentities.Count && expectedIdentities.All(p => job.PartIdentities.GetValueOrDefault(p.Key) == p.Value), "definition/material/numerics identities differ");
        VerifyInventory(workspace, output, job, cancellation);
        var author = StrictDesignJson.Read<PartsReport>(workspace.Resolve(output + "/parts-report.json"));
        var verified = StrictDesignJson.Read<PartsReport>(workspace.Resolve(output + "/parts-verification.json"));
        void Header(PartsReport report, string schema)
        {
            Need(report.SchemaVersion == schema && report.JobSha256 == jobSha256 && report.Identity == job.Identity, "report ancestry differs");
            Need(Regex.IsMatch(report.BlenderVersion, "^4\\.5\\.[0-9]+(?: LTS)?$", RegexOptions.CultureInvariant), "Blender 4.5 LTS is required");
            Need(report.Parts.Count == job.Design.Parts.Count && report.Parts.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == report.Parts.Count, "missing or duplicate definition");
            Need(JsonHash(report.Instances) == JsonHash(job.Design.Instances), "physical occurrence mapping or approved transforms differ");
            Need(report.TotalBytes > 0 && report.TotalBytes <= job.Limits.MaxOutputBytes, "reported payload exceeds approved bytes");
        }
        Header(author, "bubo-schematic-parts/v1"); Header(verified, "bubo-schematic-parts/v1");
        Need(author.BlenderVersion == verified.BlenderVersion && author.WeightedTriangles == verified.WeightedTriangles && author.TotalBytes == verified.TotalBytes,
            "author and independent importer disagree");
        var parts = author.Parts.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var independent = verified.Parts.ToDictionary(p => p.Id, StringComparer.Ordinal);
        long bytes = 0, vertices = 0, triangles = 0, weighted = 0;
        var occurrences = job.Design.Instances.GroupBy(i => i.PartId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.LongCount(), StringComparer.Ordinal);
        foreach (var definition in job.Design.Parts)
        {
            cancellation.ThrowIfCancellationRequested();
            Need(parts.TryGetValue(definition.Id, out var actual) && independent.TryGetValue(definition.Id, out _), "missing definition: " + definition.Id);
            var part = actual!; var imported = independent[definition.Id];
            Need(JsonHash(part) == JsonHash(imported), "author and independent reopened geometry/material identities differ: " + part.Id);
            Need(part.DefinitionSha256 == job.PartIdentities[definition.Id] && part.MaterialId == definition.MaterialId, "part source ownership differs");
            Need(part.BlendFile == definition.Id + ".blend" && part.GeometryFile == definition.Id + ".geometry.bin" && Hash(part.BlendSha256) && Hash(part.GeometrySha256), "part filenames/hash identities differ");
            Need(part.VertexCount is >= 4 && part.VertexCount <= job.Limits.MaxVertices - vertices && part.TriangleCount is >= 4 && part.TriangleCount <= job.Limits.MaxTriangles - triangles,
                "definition geometry exceeds aggregate limits");
            vertices = checked(vertices + part.VertexCount); triangles = checked(triangles + part.TriangleCount);
            weighted = checked(weighted + part.TriangleCount * occurrences.GetValueOrDefault(definition.Id));
            Need(weighted <= Math.Min(job.Limits.MaxTriangles, job.Design.Budgets.MaxGeneratedTriangles), "all-instance weighted geometry exceeds approved/hard triangles");
            foreach (var (name, hash) in new[] { (part.BlendFile, part.BlendSha256), (part.GeometryFile, part.GeometrySha256) })
            {
                var length = new FileInfo(workspace.Resolve(output + "/" + name)).Length;
                Need(length > 0 && length <= job.Limits.MaxOutputBytes - bytes, "part payload exceeds approved bytes");
                Need(FileHash(workspace, output + "/" + name, cancellation, job.Limits.MaxOutputBytes) == hash, "editable/binary source hash changed");
                bytes = checked(bytes + length);
            }
            var material = job.Design.Materials.Single(m => m.Id == definition.MaterialId);
            Need(Near(part.MaterialColorLinear, LinearColor(material.Color), 1e-6) && Near(part.MaterialRoughness, material.Roughness, 1e-6)
                && Near(part.MaterialMetallic, material.Metallic, 1e-6), "actual material parameters differ from source");
            VerifyGeometry(workspace, output + "/" + part.GeometryFile, part, definition, job.Design.Numerics, job.Limits, cancellation);
        }
        Need(bytes == author.TotalBytes && weighted == author.WeightedTriangles, "measured payload bytes or occurrence-weighted triangles differ");
        VerifyInventory(workspace, output, job, cancellation);
        cancellation.ThrowIfCancellationRequested(); return verified;
    }

    public static void VerifyInventory(IWorkspace workspace, string output, PartBuildJob job, CancellationToken cancellation = default)
    {
        var expected = job.Design.Parts.SelectMany(p => new[] { p.Id + ".blend", p.Id + ".geometry.bin" }).Concat(["parts-report.json", "parts-verification.json"]).ToHashSet(StringComparer.Ordinal);
        Need(expected.Count == job.Limits.MaxFiles, "file inventory limit differs");
        var entries = Directory.EnumerateFileSystemEntries(workspace.Resolve(output)).Take(checked(job.Limits.MaxFiles + 1)).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Need(entries.SetEquals(expected), "unexpected, linked, missing or extra output files");
        long total = 0;
        foreach (var name in expected)
        {
            cancellation.ThrowIfCancellationRequested(); var file = workspace.Resolve(output + "/" + name);
            Need(File.Exists(file) && (File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "nonregular output file");
            var length = new FileInfo(file).Length;
            Need(length > 0 && length <= job.Limits.MaxOutputBytes - total, "complete output inventory exceeds approved bytes"); total = checked(total + length);
            if (name is "parts-report.json" or "parts-verification.json") Need(length is > 0 and <= StrictDesignJson.MaximumInputBytes, "report exceeds bounded JSON size");
        }
    }

    static Vec3 LinearColor(string color)
    {
        double Channel(int start)
        {
            var value = byte.Parse(color.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return new() { X = Channel(1), Y = Channel(3), Z = Channel(5) };
    }
    readonly record struct Point(double X, double Y, double Z)
    {
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public static Point Cross(Point a, Point b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }
    readonly record struct Edge(int Count, int Direction, int FirstLowCorner, int FirstHighCorner);
    static void VerifyGeometry(IWorkspace workspace, string path, PartArtifact report, PartDefinition definition, NumericalPolicy numerics, PartBuildLimits limits, CancellationToken cancellation)
    {
        using var stream = new FileStream(workspace.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        Need(stream.Length <= limits.MaxOutputBytes && stream.Length >= 16 && reader.ReadBytes(8).AsSpan().SequenceEqual("BUBOPRT1"u8), "invalid measured geometry header");
        var vertexCount = reader.ReadUInt32(); var triangleCount = reader.ReadUInt32();
        Need(vertexCount == report.VertexCount && triangleCount == report.TriangleCount && vertexCount <= limits.MaxVertices && triangleCount <= limits.MaxTriangles,
            "measured geometry count differs before allocation");
        Need(stream.Length == checked(16L + 24L * vertexCount + 12L * triangleCount), "binary geometry length/count differs");
        var vertices = new Point[checked((int)vertexCount)];
        var parent = new int[vertices.Length]; var used = new bool[vertices.Length];
        var minimum = new Point(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = new Point(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        var tolerance = numerics.LinearTolerance;
        for (var i = 0; i < vertices.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var p = new Point(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
            Need(double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z) && p.X >= -tolerance && p.Y >= -tolerance && p.Z >= -tolerance
                && p.X <= definition.Size.X + tolerance && p.Y <= definition.Size.Y + tolerance && p.Z <= definition.Size.Z + tolerance
                && Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) <= numerics.MaxCoordinateMagnitude, "measured position escapes approved local envelope");
            vertices[i] = p; parent[i] = i;
            minimum = new(Math.Min(minimum.X, p.X), Math.Min(minimum.Y, p.Y), Math.Min(minimum.Z, p.Z));
            maximum = new(Math.Max(maximum.X, p.X), Math.Max(maximum.Y, p.Y), Math.Max(maximum.Z, p.Z));
        }
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        var edges = new Dictionary<ulong, Edge>(); var triangles = new (int A, double Volume)[checked((int)triangleCount)];
        // Each triangle corner is one node in its vertex's face link. Joining
        // across shared edges must produce exactly one cycle at every vertex;
        // paired edges alone would admit solids pinched at a shared vertex.
        var links = Enumerable.Range(0, checked((int)triangleCount * 3)).ToArray();
        int Link(int i) { while (links[i] != i) { links[i] = links[links[i]]; i = links[i]; } return i; }
        double area = 0;
        void AddEdge(int a, int b, int cornerA, int cornerB)
        {
            var low = Math.Min(a, b); var high = Math.Max(a, b); var key = ((ulong)(uint)low << 32) | (uint)high;
            var edge = edges.GetValueOrDefault(key); var count = edge.Count + 1;
            Need(count <= 2, "edge has more than two incident triangles");
            var lowCorner = a == low ? cornerA : cornerB; var highCorner = a == low ? cornerB : cornerA;
            if (count == 1) edges[key] = new(count, a == low ? 1 : -1, lowCorner, highCorner);
            else
            {
                links[Link(lowCorner)] = Link(edge.FirstLowCorner); links[Link(highCorner)] = Link(edge.FirstHighCorner);
                edges[key] = edge with { Count = count, Direction = edge.Direction + (a == low ? 1 : -1) };
            }
        }
        for (var i = 0; i < triangleCount; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var ai = reader.ReadUInt32(); var bi = reader.ReadUInt32(); var ci = reader.ReadUInt32();
            Need(ai < vertexCount && bi < vertexCount && ci < vertexCount && ai != bi && ai != ci && bi != ci, "invalid measured triangle indices");
            var a = checked((int)ai); var b = checked((int)bi); var c = checked((int)ci);
            var cross = Point.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]); var triangleArea = cross.Length / 2;
            Need(double.IsFinite(triangleArea) && triangleArea > 0, "zero-area/nonfinite measured triangle");
            area += triangleArea; used[a] = used[b] = used[c] = true; parent[Find(b)] = Find(a); parent[Find(c)] = Find(a);
            AddEdge(a, b, i * 3, i * 3 + 1); AddEdge(b, c, i * 3 + 1, i * 3 + 2); AddEdge(c, a, i * 3 + 2, i * 3);
            triangles[i] = (a, Point.Dot(vertices[a], Point.Cross(vertices[b], vertices[c])) / 6);
        }
        Need(used.All(value => value) && edges.Values.All(e => e.Count == 2 && e.Direction == 0), "unused vertices, open edges or inconsistent orientation");
        var vertexLinks = new int[vertices.Length]; Array.Fill(vertexLinks, -1);
        void VertexLink(int vertex, int corner)
        {
            var root = Link(corner);
            Need(vertexLinks[vertex] == -1 || vertexLinks[vertex] == root, "nonmanifold disconnected vertex link"); vertexLinks[vertex] = root;
        }
        foreach (var (key, edge) in edges)
        {
            cancellation.ThrowIfCancellationRequested(); VertexLink(checked((int)(key >> 32)), edge.FirstLowCorner); VertexLink(checked((int)(key & uint.MaxValue)), edge.FirstHighCorner);
        }
        var volumes = new Dictionary<int, double>();
        foreach (var triangle in triangles) { cancellation.ThrowIfCancellationRequested(); var root = Find(triangle.A); volumes[root] = volumes.GetValueOrDefault(root) + triangle.Volume; }
        var volume = volumes.Values.Sum();
        Need(volumes.Count == 1 && report.Components == 1 && report.Manifold && volumes.Values.All(v => double.IsFinite(v) && v > 0), "a physical part requires one connected positive-volume boundary; disconnected solids and closed inner cavities require a separately reviewed construction profile");
        Need(Near(report.Minimum, new() { X = minimum.X, Y = minimum.Y, Z = minimum.Z }, tolerance)
            && Near(report.Maximum, new() { X = maximum.X, Y = maximum.Y, Z = maximum.Z }, tolerance), "reported bounds differ from measured binary positions");
        Need(Near(report.Volume, volume, Math.Max(1e-12, tolerance * area * 2)) && Near(report.SurfaceArea, area, Math.Max(1e-12, tolerance * Math.Sqrt(area) * 8))
            && report.Volume > 0 && report.SurfaceArea > 0, "reported positive volume/area differs from measured triangles");
        cancellation.ThrowIfCancellationRequested();
    }
}
