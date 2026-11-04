using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using Hephaera.AssetProduction;
using static Bubo.Schematic.Domain.RigidTransforms;

namespace Bubo.Schematic.Infrastructure;

/// <summary>Retains actual sampled clearance witnesses when an assembly cannot be certified.</summary>
public sealed class AssemblyOutputVerificationException(string message, ClearanceReport report) : InvalidOperationException(message)
{
    public ClearanceReport Report { get; } = report;
}

/// <summary>Read-only independent checks of reopened reports, prototype ancestry and actual evaluated binary skins.</summary>
public static class AssemblyOutputVerifier
{
    const int MaximumFiles = 16_384;
    const int MaximumPoseElements = 250_000;
    const long MaximumOutputBytes = 17_179_869_184;
    sealed record Mesh(Vec3[] Vertices, TriangleIndex[] Triangles);
    static void Need([DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new InvalidOperationException("Assembly output: " + message); }
    static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    static string JsonHash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, StrictDesignJson.Options)));
    static bool Near(double a, double b, double tolerance) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= tolerance;
    static bool Near(Vec3 a, Vec3 b, double tolerance) => Finite(a) && Finite(b) && Length(Subtract(a, b)) <= tolerance;
    static bool Finite(Vec3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    static string Name(string kind, int index, int? sub = null) => "bubo." + kind + "." + index.ToString("D6", CultureInfo.InvariantCulture)
        + (sub is { } value ? "." + value.ToString("D6", CultureInfo.InvariantCulture) : "");
    static string Ordinal(int index) => index.ToString("D6", CultureInfo.InvariantCulture);

    public static AssemblyNativeVerification Verify(IWorkspace workspace, string output, AssemblyBuildJob job, string jobSha256, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var design = job.Design; var profile = design.AssemblyProfile;
        Need(job.SchemaVersion == "bubo-schematic-assembly-job/v1" && job.Operation is "assemble" or "prove-motion"
            && job.ProjectRoot == workspace.Root && job.Output == workspace.Resolve(output) && Hash(jobSha256), "job identity/layout differs");
        Need(design.SchemaVersion == "bubo-assembly-design/v2" && profile is not null, "an approved v2 assembly profile is required");
        new AssemblyDesignValidator().RequireValid(design, cancellation);
        new AssemblyProfileValidator().RequireValid(design, cancellation);
        Need(job.Limits.MaxVertices is > 0 and <= PartConstructionReadiness.MaximumVertices
            && job.Limits.MaxTriangles is > 0 and <= PartConstructionReadiness.MaximumTriangles
            && job.Limits.MaxTriangles <= design.Budgets.MaxGeneratedTriangles && job.Limits.MaxOutputBytes == design.Budgets.MaxExportBytes
            && job.Limits.MaxOutputBytes is > 0 and <= MaximumOutputBytes && job.Limits.MaxJobBytes == StrictDesignJson.MaximumInputBytes
            && job.Limits.MaxFiles is > 0 and <= MaximumFiles && job.Limits.MaxMotionSamples == design.Budgets.MaxMotionSamples
            && job.Limits.MaxWorkChecks == profile!.MaxGeometryChecks && profile.MaxGeometryChecks <= GeometryWork.HardMaximum,
            "job limits differ from approved/hard bounds");
        var identity = job.Identity;
        Need(new[] { identity.DesignSha256, identity.DesignFingerprint, identity.RuntimeBuildSha256, identity.ToolSha256,
            identity.PartsJobSha256, identity.PartsReportSha256, identity.PartsVerificationSha256 }.All(Hash), "invalid seven-hash identity");
        Need(job.Plan.SchemaVersion == "bubo-schematic-diagnostic-plan/v1" && job.Plan.Clips.Count <= 2L * design.Joints.Count
            && job.Plan.Clips.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == job.Plan.Clips.Count,
            "diagnostic plan identity/inventory differs");
        long poseCount = 1;
        foreach (var clip in job.Plan.Clips)
        {
            cancellation.ThrowIfCancellationRequested();
            Need(clip.Samples.Count >= 2 && clip.Samples.Count <= job.Limits.MaxMotionSamples, "diagnostic sample count differs");
            double previous = -1;
            foreach (var sample in clip.Samples)
            {
                Need(double.IsFinite(sample.TimeSeconds) && sample.TimeSeconds >= 0 && sample.TimeSeconds > previous, "diagnostic sample times are not finite and increasing");
                previous = sample.TimeSeconds;
            }
            poseCount = checked(poseCount + clip.Samples.Count);
        }
        Need(poseCount == job.Plan.Sampling.TotalSamples && poseCount <= job.Limits.MaxMotionSamples, "complete diagnostic sampling differs");
        var reportName = job.Operation == "assemble" ? "assembly-report.json" : "motion-report.json";
        var verificationName = job.Operation == "assemble" ? "assembly-verification.json" : "motion-verification.json";
        var sourceName = job.Operation == "assemble" ? "assembly.blend" : "diagnostic.blend";
        var writtenPoses = job.Operation == "assemble" ? 1 : poseCount;
        Need(checked(3L + writtenPoses * design.Instances.Count) <= job.Limits.MaxFiles, "complete file inventory exceeds declared budget before allocation");
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal) { reportName, verificationName, sourceName };
        foreach (var (_, i) in design.Instances.Select((value, index) => (value, index))) expectedFiles.Add("rest." + Ordinal(i) + ".geometry.bin");
        for (var c = 0; job.Operation == "prove-motion" && c < job.Plan.Clips.Count; c++)
            for (var s = 0; s < job.Plan.Clips[c].Samples.Count; s++)
                for (var i = 0; i < design.Instances.Count; i++) expectedFiles.Add("clip." + Ordinal(c) + ".sample." + Ordinal(s) + "." + Ordinal(i) + ".geometry.bin");
        Need(expectedFiles.Count <= job.Limits.MaxFiles && expectedFiles.Count <= MaximumFiles, "exact file inventory exceeds declared budget");
        var inventory = Inventory(workspace, output, expectedFiles, job.Limits.MaxOutputBytes, cancellation);
        var author = StrictDesignJson.Read<AssemblyBuildReport>(workspace.Resolve(output + "/" + reportName));
        var reopened = StrictDesignJson.Read<AssemblyBuildReport>(workspace.Resolve(output + "/" + verificationName));
        void Header(AssemblyBuildReport report)
        {
            Need(report.SchemaVersion == "bubo-schematic-assembly/v1" && report.JobSha256 == jobSha256 && report.Identity == identity
                && report.Operation == job.Operation && report.SourceFile == sourceName && Hash(report.SourceSha256), "report identity/ancestry differs");
            Need(Regex.IsMatch(report.BlenderVersion, "^4\\.5\\.[0-9]+(?: LTS)?$", RegexOptions.CultureInvariant), "Blender 4.5 LTS is required");
            Need(report.Instances.Count == design.Instances.Count && report.Clips.Count == job.Plan.Clips.Count, "report instance/clip inventory differs");
            Need(report.TotalBytes > 0 && report.TotalBytes <= job.Limits.MaxOutputBytes && report.TotalVertices > 0 && report.TotalTriangles > 0, "invalid reported payload totals");
        }
        Header(author); Header(reopened);
        Need(JsonHash(author) == JsonHash(reopened), "author and fresh-process reopened reports disagree");
        Need(PartOutputVerifier.FileHash(workspace, output + "/" + sourceName, cancellation, job.Limits.MaxOutputBytes) == reopened.SourceSha256, "editable source hash differs");
        if (job.Operation == "assemble") Need(job.SourceAssembly is null && job.SourceAssemblySha256 is null, "assemble unexpectedly names a source assembly");
        else
        {
            Need(job.SourceAssembly is not null && Hash(job.SourceAssemblySha256), "motion proof requires immutable source ancestry");
            Regular(workspace, job.SourceAssembly!);
            Need(PartOutputVerifier.FileHash(workspace, job.SourceAssembly!, cancellation, job.Limits.MaxOutputBytes) == job.SourceAssemblySha256, "immutable source assembly changed");
        }
        long physicalVertices = 0, physicalTriangles = 0;
        foreach (var item in design.Instances)
        {
            var prototype = job.Parts.Parts.Single(p => p.Id == item.PartId);
            Need(prototype.VertexCount >= 4 && prototype.TriangleCount >= 4, "prototype counts are invalid before allocation");
            physicalVertices = checked(physicalVertices + prototype.VertexCount); physicalTriangles = checked(physicalTriangles + prototype.TriangleCount);
        }
        Need(physicalVertices <= Math.Min(job.Limits.MaxVertices, MaximumPoseElements) && physicalTriangles <= Math.Min(job.Limits.MaxTriangles, MaximumPoseElements), "physical per-pose geometry exceeds native bounded allocation");
        Need(reopened.TotalVertices == checked(physicalVertices * writtenPoses) && reopened.TotalTriangles == checked(physicalTriangles * writtenPoses), "total geometry does not sum every rest/sample binary");
        var rigs = job.Rigs.ToDictionary(r => r.InstanceId, StringComparer.Ordinal);
        Need(rigs.Count == design.Instances.Count(i => design.Parts.Single(p => p.Id == i.PartId).Kind != PartKind.Rigid)
            && rigs.Keys.All(id => design.Instances.Any(i => i.Id == id && design.Parts.Single(p => p.Id == i.PartId).Kind != PartKind.Rigid)), "owned flexible rig inventory differs");
        var work = new GeometryWork(profile!.MaxGeometryChecks, cancellation);
        var prototypes = Prototypes(workspace, job, work);
        var poses = new List<AssemblyPoseVerification>(checked((int)writtenPoses));
        long measuredBytes = inventory[sourceName];
        void Pose(IReadOnlyList<AssemblyInstanceMeasurement> actual, AssemblyPose expected, string prefix, string clipId, int sampleIndex)
        {
            cancellation.ThrowIfCancellationRequested();
            Need(actual.Count == design.Instances.Count && expected.Instances.Count == design.Instances.Count, "pose omits physical instances");
            var worlds = WorldFrames(design, expected, work);
            var meshes = new List<PosedMesh>(actual.Count); var flexible = new List<AssemblyFlexibleMeasurement>();
            for (var index = 0; index < design.Instances.Count; index++)
            {
                work.Charge(); var instance = design.Instances[index]; var part = design.Parts.Single(p => p.Id == instance.PartId);
                var measurement = actual[index]; var prototype = job.Parts.Parts.Single(p => p.Id == part.Id); var geometryName = prefix + "." + Ordinal(index) + ".geometry.bin";
                Need(measurement.InstanceId == instance.Id && measurement.PartId == part.Id && measurement.ObjectName == Name("instance", index)
                    && measurement.ParentId == instance.ParentId && measurement.MaterialId == part.MaterialId && measurement.GeometryFile == geometryName,
                    "physical instance/name/hierarchy/geometry ownership differs: " + instance.Id);
                Need(measurement.PrototypeBlendSha256 == prototype.BlendSha256 && measurement.PrototypeGeometrySha256 == prototype.GeometrySha256
                    && Hash(measurement.GeometrySha256) && measurement.VertexCount == prototype.VertexCount && measurement.TriangleCount == prototype.TriangleCount, "prototype ancestry/count differs");
                var world = worlds[instance.Id]; var local = Compose(Inverse(worlds[instance.ParentId]), world);
                PoseNear(measurement.WorldPose, world, design.Numerics); PoseNear(measurement.LocalPose, local, design.Numerics);
                var material = design.Materials.Single(m => m.Id == part.MaterialId);
                Need(Near(measurement.MaterialColorLinear, LinearColor(material.Color), 1e-6) && Near(measurement.MaterialRoughness, material.Roughness, 1e-6)
                    && Near(measurement.MaterialMetallic, material.Metallic, 1e-6), "actual material parameters differ");
                Ports(measurement, part, index, design.Numerics, work);
                var path = output + "/" + geometryName;
                var mesh = ReadMesh(workspace, path, measurement.VertexCount, measurement.TriangleCount, measurement.GeometrySha256, job.Limits, work);
                var source = prototypes[part.Id];
                Need(mesh.Triangles.AsSpan().SequenceEqual(source.Triangles), "actual binary topology differs from exact prototype triangle indices");
                // Float32 orientation storage is not an authorization for scale/shear. Its actual
                // dimensional effect on every delivered vertex must fit the approved metre tolerance.
                Undistorted(measurement.WorldPose, mesh.Vertices, design.Numerics.LinearTolerance, work);
                Undistorted(measurement.LocalPose, mesh.Vertices, design.Numerics.LinearTolerance, work);
                foreach (var vertex in mesh.Vertices)
                {
                    work.Charge(); var point = Point(measurement.WorldPose, vertex);
                    Need(Finite(point) && Math.Max(Math.Abs(point.X), Math.Max(Math.Abs(point.Y), Math.Abs(point.Z))) <= design.Numerics.MaxCoordinateMagnitude, "actual world vertex escapes coordinate bounds");
                }
                if (rigs.TryGetValue(instance.Id, out var program))
                    flexible.Add(Flexible(measurement, source, mesh, program, design, expected, clipId, sampleIndex, index, work));
                else
                {
                    Need(measurement.Rig is null, "rigid instance has an undeclared skin rig");
                    for (var v = 0; v < mesh.Vertices.Length; v++) { work.Charge(); Need(Near(mesh.Vertices[v], source.Vertices[v], design.Numerics.LinearTolerance), "rigid prototype-local position changed"); }
                }
                measuredBytes = checked(measuredBytes + inventory[geometryName]);
                meshes.Add(new(instance.Id, measurement.WorldPose, mesh.Vertices, mesh.Triangles));
            }
            var clearance = AssemblyClearanceVerifier.Verify(design, meshes, work, measureSurfaceMinimum: false);
            if (!clearance.IsValid) throw new AssemblyOutputVerificationException("Assembly output: actual clearance failed at " + clipId + "/" + sampleIndex + ": "
                + string.Join("; ", clearance.Issues.Select(i => i.Code + ": " + i.Message)), clearance);
            poses.Add(new(clipId, sampleIndex, clearance, flexible.AsReadOnly()));
        }
        Pose(reopened.Instances, job.Plan.Rest, "rest", "rest", -1);
        for (var c = 0; c < job.Plan.Clips.Count; c++)
        {
            work.Charge(); var expected = job.Plan.Clips[c]; var actual = reopened.Clips[c];
            Need(actual.Id == expected.Id && actual.Samples.Count == (job.Operation == "assemble" ? 0 : expected.Samples.Count), "clip identity/sample inventory differs");
            Actions(actual, expected, design, rigs, c);
            for (var s = 0; s < actual.Samples.Count; s++)
            {
                var sample = actual.Samples[s];
                Need(sample.Index == s && Near(sample.TimeSeconds, expected.Samples[s].TimeSeconds, 1e-12), "actual diagnostic sample index/time differs");
                Pose(sample.Instances, expected.Samples[s].Pose, "clip." + Ordinal(c) + ".sample." + Ordinal(s), expected.Id, s);
            }
        }
        Need(measuredBytes == reopened.TotalBytes, "reported payload bytes differ from blend and all geometry; receipts are separate");
        Inventory(workspace, output, expectedFiles, job.Limits.MaxOutputBytes, cancellation);
        cancellation.ThrowIfCancellationRequested(); return new(reopened, poses.AsReadOnly(), work.Checks);
    }

    static void Regular(IWorkspace workspace, string path)
    {
        var absolute = workspace.Resolve(path); var current = absolute;
        while (!string.Equals(current, workspace.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Need((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "linked output/source path is forbidden");
            current = Path.GetDirectoryName(current) ?? throw new InvalidOperationException("Assembly output: path escapes workspace");
        }
        Need(File.Exists(absolute) && (File.GetAttributes(absolute) & FileAttributes.Directory) == 0, "regular existing file is required");
    }
    static Dictionary<string, long> Inventory(IWorkspace workspace, string output, HashSet<string> expected, long maximumBytes, CancellationToken ct)
    {
        var absolute = workspace.Resolve(output);
        Need((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) == 0, "linked output directory is forbidden");
        var entries = Directory.EnumerateFileSystemEntries(absolute).Take(MaximumFiles + 1).ToArray();
        Need(entries.Length == expected.Count && entries.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal).SetEquals(expected), "unexpected/missing/duplicate output inventory");
        var lengths = new Dictionary<string, long>(StringComparer.Ordinal); long total = 0;
        foreach (var name in expected)
        {
            ct.ThrowIfCancellationRequested(); Need(Path.GetFileName(name) == name && !name.Contains('\\') && !name.Contains('/'), "relative confined filename is required");
            var path = output + "/" + name; Regular(workspace, path); var length = new FileInfo(workspace.Resolve(path)).Length;
            Need(length > 0 && length <= maximumBytes - total, "complete output including reports exceeds approved byte budget");
            if (name.EndsWith(".json", StringComparison.Ordinal)) Need(length <= StrictDesignJson.MaximumInputBytes, "report exceeds 8 MiB bounded JSON");
            lengths.Add(name, length); total = checked(total + length);
        }
        return lengths;
    }
    static Dictionary<string, Mesh> Prototypes(IWorkspace workspace, AssemblyBuildJob job, GeometryWork work)
    {
        var ct = work.CancellationToken;
        var identity = job.Identity;
        Need(Hash(job.PartsReportSha256) && Hash(job.PartsVerificationSha256) && identity.PartsReportSha256 == job.PartsReportSha256
            && identity.PartsVerificationSha256 == job.PartsVerificationSha256 && identity.PartsJobSha256 == job.Parts.JobSha256
            && job.Parts.Identity.DesignSha256 == identity.DesignSha256 && job.Parts.Identity.DesignFingerprint == identity.DesignFingerprint
            && job.Parts.Identity.RuntimeBuildSha256 == identity.RuntimeBuildSha256 && job.Parts.Identity.ToolSha256 == identity.ToolSha256, "parts report ancestry differs");
        foreach (var (name, hash) in new[] { ("parts-report.json", job.PartsReportSha256), ("parts-verification.json", job.PartsVerificationSha256) })
        {
            var path = job.PartsDirectory + "/" + name; Regular(workspace, path);
            Need(PartOutputVerifier.FileHash(workspace, path, ct, StrictDesignJson.MaximumInputBytes) == hash, "immutable parts report hash changed");
            Need(JsonHash(StrictDesignJson.Read<PartsReport>(workspace.Resolve(path))) == JsonHash(job.Parts), "parts report differs from admitted immutable data");
        }
        Need(job.Parts.SchemaVersion == "bubo-schematic-parts/v1" && job.Parts.Parts.Count == job.Design.Parts.Count
            && job.Parts.Parts.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == job.Parts.Parts.Count
            && job.Design.Parts.All(p => job.Design.Instances.Any(i => i.PartId == p.Id))
            && JsonHash(job.Parts.Instances) == JsonHash(job.Design.Instances), "parts definitions/physical occurrence inventory differs");
        var identities = PartOutputVerifier.PartIdentities(job.Design); var result = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        foreach (var part in job.Parts.Parts)
        {
            ct.ThrowIfCancellationRequested();
            Need(identities.TryGetValue(part.Id, out var definition) && part.DefinitionSha256 == definition && part.BlendFile == part.Id + ".blend"
                && part.GeometryFile == part.Id + ".geometry.bin" && Hash(part.BlendSha256) && Hash(part.GeometrySha256), "immutable prototype definition/path/identity differs");
            Need(part.MaterialId == job.Design.Parts.Single(p => p.Id == part.Id).MaterialId, "immutable prototype material ownership differs");
            foreach (var (file, hash) in new[] { (part.BlendFile, part.BlendSha256), (part.GeometryFile, part.GeometrySha256) })
            {
                var path = job.PartsDirectory + "/" + file; Regular(workspace, path);
                Need(PartOutputVerifier.FileHash(workspace, path, ct, job.Limits.MaxOutputBytes) == hash, "immutable prototype source/binary hash changed");
            }
            result.Add(part.Id, ReadMesh(workspace, job.PartsDirectory + "/" + part.GeometryFile, part.VertexCount, part.TriangleCount, part.GeometrySha256, job.Limits, work));
        }
        return result;
    }
    static Mesh ReadMesh(IWorkspace workspace, string path, int expectedVertices, int expectedTriangles, string expectedSha256, AssemblyBuildLimits limits, GeometryWork work)
    {
        work.Charge();
        Need(expectedVertices is >= 4 and <= MaximumPoseElements && expectedVertices <= limits.MaxVertices
            && expectedTriangles is >= 4 and <= MaximumPoseElements && expectedTriangles <= limits.MaxTriangles, "binary expected counts exceed bounded allocation");
        using var stream = new FileStream(workspace.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        Need(stream.Length >= 16 && stream.Length <= limits.MaxOutputBytes && reader.ReadBytes(8).AsSpan().SequenceEqual("BUBOPRT1"u8), "invalid measured geometry header");
        var vertices = reader.ReadUInt32(); var triangles = reader.ReadUInt32();
        Need(vertices == expectedVertices && triangles == expectedTriangles && stream.Length == checked(16L + 24L * vertices + 12L * triangles), "binary counts/length differ before allocation");
        // Hash and decode the same read-shared stream: a separate pathname hash cannot bind a later opened binary.
        stream.Position = 0;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[65536]; int read;
            while ((read = stream.Read(buffer)) > 0) { work.CancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, read)); }
            Need(Convert.ToHexStringLower(hash.GetHashAndReset()) == expectedSha256, "actual binary geometry hash differs");
        }
        stream.Position = 16;
        work.Charge(checked((long)vertices + triangles));
        var positions = new Vec3[expectedVertices]; var indices = new TriangleIndex[expectedTriangles];
        for (var i = 0; i < positions.Length; i++)
        {
            work.CancellationToken.ThrowIfCancellationRequested(); positions[i] = new() { X = reader.ReadDouble(), Y = reader.ReadDouble(), Z = reader.ReadDouble() };
            Need(Finite(positions[i]), "nonfinite actual binary position");
        }
        for (var i = 0; i < indices.Length; i++)
        {
            work.CancellationToken.ThrowIfCancellationRequested(); var a = reader.ReadUInt32(); var b = reader.ReadUInt32(); var c = reader.ReadUInt32();
            Need(a < vertices && b < vertices && c < vertices && a != b && a != c && b != c, "invalid actual triangle indices");
            indices[i] = new(checked((int)a), checked((int)b), checked((int)c));
        }
        Need(stream.ReadByte() == -1, "binary grew while reading"); return new(positions, indices);
    }
    static void PoseNear(RigidPose actual, RigidPose expected, NumericalPolicy numerics)
    {
        var orientation = RigidOrientation(actual); var angular = numerics.AngularToleranceDegrees;
        Need(Near(actual.Origin, expected.Origin, numerics.LinearTolerance)
            && Angle(actual.X, expected.X) <= angular && Angle(actual.Y, expected.Y) <= angular && Angle(actual.Z, expected.Z) <= angular
            && Math.Abs(Angle(actual.X, actual.Y) - 90) <= angular && Math.Abs(Angle(actual.X, actual.Z) - 90) <= angular
            && Math.Abs(Angle(actual.Y, actual.Z) - 90) <= angular,
            "actual local/world/bone/port origin, orientation or orthogonality differs from approved tolerances");
        // Frames without their own surface still resolve the declared minimum feature. Owned meshes
        // and skins below use all actual vertex offsets, not a fixed dimensionless basis allowance.
        foreach (var axis in new[] { RigidPose.Identity.X, RigidPose.Identity.Y, RigidPose.Identity.Z })
        {
            var probe = Scale(axis, numerics.MinimumFeatureSize);
            Need(Near(Direction(actual, probe), Direction(orientation, probe), numerics.LinearTolerance), "actual frame distortion exceeds the approved minimum-feature precision");
        }
    }
    static double Angle(Vec3 first, Vec3 second)
    {
        var a = Normalize(first); var b = Normalize(second);
        return Math.Atan2(Length(Cross(a, b)), Math.Clamp(Dot(a, b), -1, 1)) * 180 / Math.PI;
    }
    static RigidPose RigidOrientation(RigidPose actual)
    {
        Need(Finite(actual.Origin) && Finite(actual.X) && Finite(actual.Y) && Finite(actual.Z), "actual frame is nonfinite");
        var determinant = Dot(actual.X, Cross(actual.Y, actual.Z));
        Need(double.IsFinite(determinant) && determinant > 0, "actual frame must be invertible and right-handed");
        var x = Normalize(actual.X); var y = Normalize(Subtract(actual.Y, Scale(x, Dot(x, actual.Y))));
        return new(actual.Origin, x, y, Normalize(Cross(x, y)));
    }
    static void Undistorted(RigidPose actual, IReadOnlyList<Vec3> points, double tolerance, GeometryWork work)
    {
        var rigid = RigidOrientation(actual);
        foreach (var point in points)
        {
            work.Charge();
            Need(Near(Direction(actual, point), Direction(rigid, point), tolerance), "actual measured basis changes owned geometry beyond the approved linear tolerance");
        }
    }
    static RigidPose AffineInverse(RigidPose actual)
    {
        var determinant = Dot(actual.X, Cross(actual.Y, actual.Z));
        Need(double.IsFinite(determinant) && determinant > 0, "actual bone rest frame must be finite, invertible and right-handed");
        var first = Scale(Cross(actual.Y, actual.Z), 1 / determinant);
        var second = Scale(Cross(actual.Z, actual.X), 1 / determinant);
        var third = Scale(Cross(actual.X, actual.Y), 1 / determinant);
        var inverse = new RigidPose(Vec3.Zero, new() { X = first.X, Y = second.X, Z = third.X },
            new() { X = first.Y, Y = second.Y, Z = third.Y }, new() { X = first.Z, Y = second.Z, Z = third.Z });
        inverse = inverse with { Origin = Scale(Direction(inverse, actual.Origin), -1) };
        Need(Finite(inverse.Origin) && Finite(inverse.X) && Finite(inverse.Y) && Finite(inverse.Z), "actual bone rest inverse is nonfinite"); return inverse;
    }
    static Dictionary<string, RigidPose> WorldFrames(AssemblyDesign design, AssemblyPose pose, GeometryWork work)
    {
        Need(pose.Instances.Count == design.Instances.Count && design.Instances.All(i => pose.Instances.ContainsKey(i.Id)), "diagnostic pose identity inventory differs");
        var result = pose.Instances.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var groups = design.Subassemblies.ToDictionary(g => g.Id, StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
        RigidPose Get(string id)
        {
            work.Charge(); if (result.TryGetValue(id, out var value)) return value;
            Need(groups.ContainsKey(id) && visiting.Add(id), "unknown or cyclic parent frame"); var group = groups[id];
            var parent = group.ParentId is null ? RigidPose.Identity : Get(group.ParentId);
            result[id] = Compose(parent, FromFrame(group.Frame)); visiting.Remove(id); return result[id];
        }
        foreach (var group in groups.Values) Get(group.Id); return result;
    }
    static void Ports(AssemblyInstanceMeasurement instance, PartDefinition part, int index, NumericalPolicy numerics, GeometryWork work)
    {
        Need(instance.Ports.Count == part.Interfaces.Count + part.Terminals.Count, "owned port inventory differs"); int ordinal = 0;
        foreach (var (kind, ports) in new[] { ("interface", part.Interfaces.Select(p => (p.Id, p.Frame))), ("terminal", part.Terminals.Select(p => (p.Id, p.Frame))) })
        {
            int portIndex = 0;
            foreach (var port in ports)
            {
                work.Charge(); var actual = instance.Ports[ordinal++]; var local = FromFrame(port.Frame);
                Need(actual.PortId == port.Id && actual.Kind == kind && actual.ObjectName == Name(kind, index, portIndex++), "owned actual port identity differs");
                PoseNear(actual.LocalPose, local, numerics); PoseNear(actual.WorldPose, Compose(instance.WorldPose, local), numerics);
            }
        }
    }
    static void Actions(AssemblyClipMeasurement actual, DiagnosticClip expected, AssemblyDesign design, Dictionary<string, FlexibleRigProgram> rigs, int clipIndex)
    {
        Need(actual.Actions.Count == design.Instances.Count + rigs.Count, "actual object/rig action inventory differs"); int ownerIndex = 0;
        for (var i = 0; i < design.Instances.Count; i++)
            foreach (var kind in rigs.ContainsKey(design.Instances[i].Id) ? new[] { "instance", "rig" } : new[] { "instance" })
            {
                var action = actual.Actions[ownerIndex]; var channels = kind == "rig" ? checked(7 * (1 + rigs[design.Instances[i].Id].RestRingFrames.Count)) : 7;
                Need(action.OwnerId == design.Instances[i].Id && action.OwnerName == Name(kind, i) && action.ActionName == Name("action", clipIndex, ownerIndex)
                    && action.FcurveCount == channels && action.KeyframeCount == checked(channels * expected.Samples.Count), "actual retained action owner/channel/key inventory differs");
                ownerIndex++;
            }
    }
    static Vec3 LinearColor(string color)
    {
        double Channel(int offset)
        {
            var v = byte.Parse(color.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
        }
        return new() { X = Channel(1), Y = Channel(3), Z = Channel(5) };
    }

    static AssemblyFlexibleMeasurement Flexible(AssemblyInstanceMeasurement measurement, Mesh prototype, Mesh actual, FlexibleRigProgram program,
        AssemblyDesign design, AssemblyPose expected, string clipId, int sampleIndex, int instanceIndex, GeometryWork work)
    {
        var rig = measurement.Rig; Need(rig is not null, "flexible instance lacks actual owned rig");
        var part = design.Parts.Single(p => p.Id == measurement.PartId); var route = design.Routes.Single(r => r.InstanceId == measurement.InstanceId);
        var policy = design.AssemblyProfile!.FlexibleRigs.Single(p => p.RouteId == route.Id); var tolerance = design.Numerics.LinearTolerance;
        var rest = program.RestRingFrames;
        Need(program.ClosedPath == route.Closed && program.RadialSegments is >= 3 and <= 1024 && rest.Count == route.Points.Count
            && checked((long)rest.Count * program.RadialSegments) == prototype.Vertices.Length && rest.Count <= 16_384,
            "source ring inventory differs");
        var frames = sampleIndex < 0 ? rest : program.Clips.Single(c => c.ClipId == clipId).Samples.Single(s => s.SampleIndex == sampleIndex).RingFrames;
        Need(frames.Count == rest.Count && rig!.ArmatureName == Name("rig", instanceIndex) && rig.Bones.Count == rest.Count + 1
            && rig.RingVertexIndices.Count == rest.Count && rig.RingCenters.Count == rest.Count
            && rig.SegmentLengths.Count == (program.ClosedPath ? rest.Count : rest.Count - 1) && rig.BendAnglesDegrees.Count == rest.Count, "actual rig/ring metric inventory differs");
        PoseNear(rig.ArmatureLocalPose, measurement.LocalPose, design.Numerics); PoseNear(rig.ArmatureWorldPose, measurement.WorldPose, design.Numerics);
        Undistorted(rig.ArmatureLocalPose, actual.Vertices, tolerance, work); Undistorted(rig.ArmatureWorldPose, actual.Vertices, tolerance, work);
        for (var b = 0; b < rig.Bones.Count; b++)
        {
            work.Charge(); var bone = rig.Bones[b];
            Need(bone.Name == (b == 0 ? "root" : Name("ring", b - 1)) && bone.ParentName == (b == 0 ? null : "root") && Near(bone.Length, program.BoneLength, tolerance), "actual stable bone hierarchy/length differs");
            PoseNear(bone.RestPose, b == 0 ? RigidPose.Identity : rest[b - 1], design.Numerics);
            PoseNear(bone.Pose, b == 0 ? RigidPose.Identity : frames[b - 1], design.Numerics);
            if (b == 0) Need(bone.RestPose == RigidPose.Identity && bone.Pose == RigidPose.Identity, "unweighted root must retain exact identity");
        }
        Need(part.Features.Count == 1 && part.Features[0].Kind == FeatureKind.Sweep && part.Features[0].Operation == FeatureOperation.Add, "flexible skin requires the original single analytic sweep");
        var feature = part.Features[0]; var radius = feature.Parameters["radius"];
        Need(feature.Parameters["segments"] == program.RadialSegments && feature.Path.Count == rest.Count + (route.Closed ? 1 : 0), "source sweep/rig identity differs");
        var owner = new int[prototype.Vertices.Length]; Array.Fill(owner, -1); var centers = new Vec3[rest.Count];
        var buckets = new Dictionary<(long, long, long), List<int>>();
        (long, long, long) Cell(Vec3 p) => (checked((long)Math.Floor(p.X / tolerance)), checked((long)Math.Floor(p.Y / tolerance)), checked((long)Math.Floor(p.Z / tolerance)));
        for (var v = 0; v < prototype.Vertices.Length; v++)
        {
            work.Charge(); var key = Cell(prototype.Vertices[v]); if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = []; bucket.Add(v);
        }
        for (var r = 0; r < rest.Count; r++)
        {
            work.Charge(); Need(Near(rest[r].Origin, DesignGeometry.TransformPoint(feature.Frame, feature.Path[r]), tolerance), "rest ring differs from original analytic sweep center");
            var indices = rig.RingVertexIndices[r]; Need(indices.Count == program.RadialSegments, "each source ring requires every radial vertex");
            var sourceIndices = new HashSet<int>();
            for (var radial = 0; radial < program.RadialSegments; radial++)
            {
                work.Charge(); var angle = 2 * Math.PI * radial / program.RadialSegments;
                // Source sweep binormal is tangent cross normal, opposite the bone's right-handed Z.
                var sourcePoint = Add(rest[r].Origin, Add(Scale(rest[r].X, radius * Math.Cos(angle)), Scale(rest[r].Z, -radius * Math.Sin(angle))));
                var cell = Cell(sourcePoint); int found = -1;
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                {
                    work.Charge();
                    if (buckets.TryGetValue((cell.Item1 + x, cell.Item2 + y, cell.Item3 + z), out var candidates))
                        foreach (var candidate in candidates)
                        {
                            work.Charge(); if (!Near(prototype.Vertices[candidate], sourcePoint, tolerance)) continue;
                            Need(found == -1, "ambiguous source sweep correspondence"); found = candidate;
                        }
                }
                Need(found >= 0 && sourceIndices.Add(found), "missing or duplicate analytic source sweep vertex");
            }
            Need(indices.SequenceEqual(sourceIndices.Order()), "actual ring indices do not preserve source sweep ownership");
            Vec3 center = Vec3.Zero;
            var bone = rig.Bones[r + 1];
            var rawDeformation = Compose(bone.Pose, AffineInverse(bone.RestPose));
            var rigidDeformation = Compose(RigidOrientation(bone.Pose), Inverse(RigidOrientation(bone.RestPose)));
            foreach (var v in indices)
            {
                work.Charge(); Need(v >= 0 && v < owner.Length && owner[v] == -1, "source ring partition contains invalid or repeated vertex"); owner[v] = r;
                center = Add(center, Scale(actual.Vertices[v], 1d / indices.Count));
                var transformed = Point(rawDeformation, prototype.Vertices[v]);
                Need(Near(actual.Vertices[v], transformed, tolerance), "actual binary skin differs from one-hot linear blend skinning");
                var rigid = Point(rigidDeformation, prototype.Vertices[v]);
                Need(Near(actual.Vertices[v], rigid, tolerance), "actual bone basis distorts the owned skin beyond approved linear precision");
            }
            centers[r] = center;
            Need(Near(center, rig.RingCenters[r], tolerance) && Near(center, frames[r].Origin, tolerance), "reported/expected ring center differs from actual binary vertices");
        }
        Need(owner.All(r => r >= 0), "actual ring skin omits prototype vertices");
        using var weights = new MemoryStream();
        using (var writer = new BinaryWriter(weights, System.Text.Encoding.UTF8, true))
        {
            writer.Write("BUBOSKN1"u8); writer.Write(checked((uint)owner.Length));
            foreach (var ring in owner) { work.Charge(); writer.Write(checked((uint)ring)); writer.Write(1d); }
        }
        Need(Hash(rig.WeightsSha256) && rig.WeightsSha256 == Convert.ToHexStringLower(SHA256.HashData(weights.GetBuffer().AsSpan(0, checked((int)weights.Length)))), "actual one-hot skin weights identity differs");
        for (var i = 0; i < rig.SegmentLengths.Count; i++)
        { work.Charge(); Need(Near(rig.SegmentLengths[i], Length(Subtract(centers[(i + 1) % centers.Length], centers[i])), tolerance), "reported segment length differs from binary ring centers"); }
        for (var i = 0; i < centers.Length; i++)
        {
            work.Charge(); double angle = 0;
            if (route.Closed || i != 0 && i != centers.Length - 1)
            {
                var before = Subtract(centers[i], centers[(i + centers.Length - 1) % centers.Length]); var after = Subtract(centers[(i + 1) % centers.Length], centers[i]);
                var denominator = Length(before) * Length(after); Need(denominator > 0, "actual ring segment collapsed");
                angle = Math.Acos(Math.Clamp(Dot(before, after) / denominator, -1, 1)) * 180 / Math.PI;
            }
            Need(Near(rig.BendAnglesDegrees[i], angle, design.Numerics.AngularToleranceDegrees), "reported bend differs from actual binary ring centers");
        }
        // Reference segment lengths are invariant under the admitted unit rigid owner transform.
        var worldCenters = centers.Select(p => Point(measurement.WorldPose, p)).ToArray(); var reference = rest.Select(p => p.Origin).ToArray();
        var metrics = FlexibleRingGeometry.Measure(worldCenters, reference, route.Closed, () => work.Charge());
        FlexibleRingGeometry.RequireLimits(metrics, part.Flexible!, policy, tolerance);
        if (!route.Closed)
        {
            Vec3 Attachment(Endpoint endpoint)
            {
                var definition = design.Parts.Single(p => p.Id == design.Instances.Single(i => i.Id == endpoint.InstanceId).PartId);
                var frame = definition.Interfaces.Where(p => p.Id == endpoint.PortId).Select(p => p.Frame).Concat(definition.Terminals.Where(p => p.Id == endpoint.PortId).Select(p => p.Frame)).Single();
                return Point(expected.Instances[endpoint.InstanceId], frame.Origin);
            }
            Need(Near(worldCenters[0], Attachment(route.From!), tolerance) && Near(worldCenters[^1], Attachment(route.To!), tolerance), "actual flexible endpoints detach from owned current ports");
        }
        return new(measurement.InstanceId, metrics.ArcLength, metrics.MinimumSegmentRatio, metrics.MaximumSegmentRatio,
            double.IsPositiveInfinity(metrics.MinimumBendRadius) ? null : metrics.MinimumBendRadius, metrics.EndpointDistance);
    }
}
