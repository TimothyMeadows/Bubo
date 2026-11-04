using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using Hephaera.AssetProduction;
using static Bubo.Schematic.Domain.RigidTransforms;

namespace Bubo.Schematic.Infrastructure;

/// <summary>Independently admits actual split/reordered GLB geometry, skin bindings and sampled motion.</summary>
public static class AssemblyExportOutputVerifier
{
    internal sealed record Mesh(Vec3[] Vertices, TriangleIndex[] Triangles, int[] Logical);
    static void Need([DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Schematic export output: " + message); }
    static bool Finite(Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    static bool Near(Vec3 a, Vec3 b, double tolerance) => Finite(a) && Finite(b) && Length(Subtract(a, b)) <= tolerance;
    static bool Near(double a, double b, double tolerance) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= tolerance;
    static string Canonical<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(StrictDesignJson.Options) { WriteIndented = false });
    static string Bone(int instance, int index) => "bubo.bone." + instance.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + "." + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public static AssemblyExportNativeVerification Verify(IWorkspace workspace, string output, AssemblyExportJob job,
        string jobSha256, CancellationToken cancellation = default)
    {
        Need(job.SchemaVersion == "bubo-schematic-export-job/v1" && job.Operation is "produce" or "verify" or "review"
            && (job.Operation == "verify" ? job.Representation is "rest" or "diagnostic" : job.Representation is null)
            && job.ProjectRoot == workspace.Root && job.Output == workspace.Resolve(output) && ExportPackageValidator.IsHash(jobSha256), "job identity/layout differs");
        ExportPackageValidator.RequireIdentity(job.Identity); Limits(job.Limits);
        var inputs = InputFiles(job.Inputs).ToArray();
        Need(inputs.Select(f => workspace.Resolve(f.Path)).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() == inputs.Length,
            "aliased input references are forbidden");
        foreach (var file in inputs) RequireFile(workspace, file, file.Path.EndsWith(".json", StringComparison.Ordinal) ? job.Limits.MaxJobBytes : job.Limits.MaxSourceBytes, cancellation);
        var refs = job.Inputs; var id = job.Identity;
        Need(refs.AssemblyJob.Sha256 == id.AssemblyJobSha256 && refs.AssemblyReport.Sha256 == id.AssemblyReportSha256
            && refs.AssemblyVerification.Sha256 == id.AssemblyVerificationSha256 && refs.SourceAssembly.Sha256 == id.AssemblySourceSha256
            && refs.MotionJob.Sha256 == id.MotionJobSha256 && refs.MotionReport.Sha256 == id.MotionReportSha256
            && refs.MotionVerification.Sha256 == id.MotionVerificationSha256 && refs.SourceMotion.Sha256 == id.MotionSourceSha256,
            "source reference ancestry differs");
        if (job.Operation == "verify") Need(refs.Glb is not null && refs.ProducerReport is not null, "exact delivered GLB and producer receipt are required");
        var assemblyJob = StrictDesignJson.Read<AssemblyBuildJob>(workspace.Resolve(refs.AssemblyJob.Path));
        var motionJob = StrictDesignJson.Read<AssemblyBuildJob>(workspace.Resolve(refs.MotionJob.Path));
        var assembly = StrictDesignJson.Read<AssemblyBuildReport>(workspace.Resolve(refs.AssemblyReport.Path));
        var motion = StrictDesignJson.Read<AssemblyBuildReport>(workspace.Resolve(refs.MotionReport.Path));
        var assemblyNative = StrictDesignJson.Read<AssemblyNativeVerification>(workspace.Resolve(refs.AssemblyVerification.Path));
        var motionNative = StrictDesignJson.Read<AssemblyNativeVerification>(workspace.Resolve(refs.MotionVerification.Path));
        Need(assemblyJob.Operation == "assemble" && motionJob.Operation == "prove-motion" && assemblyJob.Identity == id.Build && motionJob.Identity == id.Build
            && assembly.JobSha256 == id.AssemblyJobSha256 && motion.JobSha256 == id.MotionJobSha256 && assembly.Identity == id.Build && motion.Identity == id.Build
            && assembly.SourceSha256 == id.AssemblySourceSha256 && motion.SourceSha256 == id.MotionSourceSha256
            && motionJob.SourceAssemblySha256 == id.AssemblySourceSha256 && Canonical(assemblyJob.Design) == Canonical(motionJob.Design)
            && Canonical(assemblyNative.Report) == Canonical(assembly) && Canonical(motionNative.Report) == Canonical(motion), "verified current source jobs/reports disagree");
        var design = motionJob.Design;
        new AssemblyDesignValidator().RequireValid(design, cancellation); new AssemblyProfileValidator().RequireValid(design, cancellation);
        Need(job.Limits.MaxOutputBytes <= design.Budgets.MaxExportBytes && job.Limits.MaxMotionSamples == design.Budgets.MaxMotionSamples
            && job.Limits.MaxGeometryChecks == design.AssemblyProfile!.MaxGeometryChecks, "limits differ from approved design");
        var reportPath = output + "/report.json";
        Regular(workspace, reportPath);
        var report = StrictDesignJson.Read<AssemblyExportReport>(workspace.Resolve(reportPath));
        Need(report.SchemaVersion == "bubo-schematic-export/v1" && report.Operation == job.Operation && report.Representation == job.Representation
            && report.JobSha256 == jobSha256 && report.Identity == id && report.Limits == job.Limits
            && Canonical(report.Toolchain) == Canonical(job.Toolchain) && Canonical(report.Bundle) == Canonical(job.Bundle)
            && Canonical(report.Inputs) == Canonical(refs) && !report.HumanModelAccepted && !report.RuntimeAccepted
            && report.BlenderVersion.StartsWith("4.5.", StringComparison.Ordinal), "report identity/current bindings differ");
        var inventory = Inventory(workspace, output, report.Outputs, job.Limits, cancellation);
        if (job.Operation != "verify")
        {
            Need(report.Raw is null && report.Imported is null, "producer/review must not claim independent geometry verification");
            if (job.Operation == "produce")
            {
                Need(inventory.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "assembly.glb", "diagnostic.glb" }), "canonical rest/diagnostic GLB producer inventory differs");
                foreach (var name in inventory.Keys)
                {
                    using var stream = File.OpenRead(workspace.Resolve(Path.Combine(output, name))); using var reader = new BinaryReader(stream);
                    Need(stream.Length >= 20 && reader.ReadUInt32() == 0x46546c67 && reader.ReadUInt32() == 2 && reader.ReadUInt32() == stream.Length, "produced GLB framing is invalid");
                }
            }
            else
            {
                Need(refs.Package is not null && refs.RestVerification is not null && refs.DiagnosticVerification is not null, "review requires exact sealed package and both current technical certificates");
                Need(inventory.ContainsKey("review.json") && inventory.Count is > 1 && inventory.Count <= job.Limits.MaxViews + 1
                    && inventory.Keys.All(n => n == "review.json" || n.EndsWith(".png", StringComparison.Ordinal)), "bounded review JSON/image inventory differs");
                var views = StrictDesignJson.Read<AssemblyExportReview>(workspace.Resolve(Path.Combine(output, "review.json")));
                Review(views, inventory, motion, design, job.Limits);
                foreach (var name in inventory.Keys.Where(n => n.EndsWith(".png", StringComparison.Ordinal)))
                {
                    using var stream = File.OpenRead(workspace.Resolve(Path.Combine(output, name))); Span<byte> header = new byte[24]; stream.ReadExactly(header);
                    Need(header[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }) && header[12..16].SequenceEqual("IHDR"u8)
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[16..20]) == job.Limits.ImageSize
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[20..24]) == job.Limits.ImageSize, "review image framing/dimensions differ");
                }
            }
            foreach (var file in inputs) RequireFile(workspace, file, file.Path.EndsWith(".json", StringComparison.Ordinal) ? job.Limits.MaxJobBytes : job.Limits.MaxSourceBytes, cancellation);
            return new("bubo-schematic-export-native-verification/v1", job.Operation == "produce" ? "package" : "review", null, report, 0, 0, 0);
        }
        Need(report.Raw is not null && report.Imported is not null, "independent raw and imported measurements are required");
        var work = new GeometryWork(job.Limits.MaxGeometryChecks, cancellation); int verifiedPoses = 0; long verifiedTriangles = 0;
        var sourceDirectory = Path.GetDirectoryName(workspace.Resolve(job.Representation == "rest" ? refs.AssemblyReport.Path : refs.MotionReport.Path))!;
        var source = job.Representation == "rest" ? assembly : motion;
        long allSamples = 1;
        foreach (var clip in motion.Clips) allSamples = checked(allSamples + clip.Samples.Count);
        Need(allSamples == motionJob.Plan.Sampling.TotalSamples && allSamples <= job.Limits.MaxMotionSamples, "whole diagnostic sample budget differs");
        Need(checked(allSamples * design.Instances.Count) <= 32768, "complete pose-instance inventory exceeds hard limit");
        var usedOutputFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tree, imported) in new[] { (report.Raw!, false), (report.Imported!, true) })
        {
            Need(tree.NodeCount is > 0 && tree.NodeCount <= job.Limits.MaxNodes && tree.AccessorCount is > 0 && tree.AccessorCount <= job.Limits.MaxAccessors
                && tree.PrimitiveCount is > 0 && tree.PrimitiveCount <= job.Limits.MaxPrimitives, "bounded GLB structure differs");
            Need(tree.Clips.Count == (job.Representation == "rest" ? 0 : motion.Clips.Count), "actual animation inventory differs");
            void Pose(IReadOnlyList<AssemblyExportInstanceMeasurement> actual, IReadOnlyList<AssemblyInstanceMeasurement> expected, bool rest)
            {
                work.Charge(); Need(actual.Count == design.Instances.Count && expected.Count == design.Instances.Count, "physical instance omitted/added");
                var nodes = new HashSet<string>(StringComparer.Ordinal); var geometryNodes = new HashSet<string>(StringComparer.Ordinal); var sourceNodeIndices = new HashSet<int>();
                var meshes = new List<PosedMesh>(actual.Count);
                long perPoseVertices = 0, perPoseTriangles = 0;
                for (var i = 0; i < actual.Count; i++)
                {
                    work.Charge(); var a = actual[i]; var e = expected[i];
                    Need(a.InstanceId == e.InstanceId && a.PartId == e.PartId && a.Node == e.ObjectName && nodes.Add(a.Node), "missing/fused/renamed physical node");
                    // The pinned importer inserts its owned armature as the skin mesh's parent.
                    // Keep that actual wrapper visible; raw GLB hierarchy must remain exact and
                    // the imported mesh's measured local/world frames must prove the same pose.
                    var importerSkinParent = imported && e.Rig is not null && tree.Instances.Count == design.Instances.Count
                        && tree.Instances[i].Skin is { } restSkin && a.ParentId == restSkin.RigNode && restSkin.RigNode == e.Rig.ArmatureName;
                    Need(a.ParentId == e.ParentId || importerSkinParent, "actual parent is neither approved hierarchy nor the measured owned importer skin wrapper");
                    var expectedLocal = importerSkinParent ? Compose(AffineInverse(e.Rig!.ArmatureWorldPose), e.WorldPose) : e.LocalPose;
                    PoseNear(a.LocalPose, expectedLocal, design.Numerics); PoseNear(a.WorldPose, e.WorldPose, design.Numerics);
                    var binding = a.Geometry;
                    Need(binding.SourceNodeIndex >= 0 && binding.SourceNodeIndex < report.Raw!.NodeCount && sourceNodeIndices.Add(binding.SourceNodeIndex)
                        && !string.IsNullOrWhiteSpace(binding.Node) && binding.Node.Length <= 256 && !binding.Node.Any(char.IsControl) && geometryNodes.Add(binding.Node),
                        "actual geometry node or raw/SDK occurrence mapping is missing/fused");
                    Need(binding.SourceNodeIndex == report.Raw.Instances[i].Geometry.SourceNodeIndex
                        && binding.Node == tree.Instances[i].Geometry.Node && binding.ParentNode == tree.Instances[i].Geometry.ParentNode
                        && binding.SemanticNodeKind == tree.Instances[i].Geometry.SemanticNodeKind, "actual raw/SDK geometry ownership changes between representations or poses");
                    var separatedMesh = imported && binding.SemanticNodeKind == "empty";
                    if (separatedMesh)
                    {
                        Need(e.Rig is not null && tree.Instances[i].Skin?.RigNode == e.Rig.ArmatureName && binding.Node != a.Node
                            && binding.ParentNode == e.Rig.ArmatureName && a.ParentId == e.ParentId, "unapproved semantic-empty/owned-mesh importer wrapper");
                        PoseNear(binding.LocalPose, Compose(AffineInverse(e.Rig.ArmatureWorldPose), e.WorldPose), design.Numerics);
                    }
                    else
                    {
                        Need(binding.SemanticNodeKind == "mesh" && binding.Node == a.Node, "raw/direct physical node is not its measured mesh");
                        var parentName = importerSkinParent ? e.Rig!.ArmatureName : ParentNode(design, e.ParentId);
                        Need(binding.ParentNode == parentName, "actual mesh parent mapping differs");
                        PoseNear(binding.LocalPose, a.LocalPose, design.Numerics);
                    }
                    PoseNear(binding.WorldPose, e.WorldPose, design.Numerics);
                    Need(a.MaterialId == e.MaterialId && Near(a.MaterialColorLinear, e.MaterialColorLinear, 1e-6) && Near(a.MaterialRoughness, e.MaterialRoughness, 1e-6)
                        && Near(a.MaterialMetallic, e.MaterialMetallic, 1e-6), "physical material assignment/parameters differ");
                    Need(a.Ports.Count == e.Ports.Count, "functional interface/terminal omitted");
                    for (var p = 0; p < a.Ports.Count; p++)
                    {
                        var ap = a.Ports[p]; var ep = e.Ports[p]; work.Charge();
                        Need(ap.PortId == ep.PortId && ap.Kind == ep.Kind && ap.ObjectName == ep.ObjectName, "interface/terminal ownership differs");
                        PoseNear(ap.LocalPose, ep.LocalPose, design.Numerics); PoseNear(ap.WorldPose, ep.WorldPose, design.Numerics);
                    }
                    Need(a.VertexCount >= e.VertexCount && a.VertexCount <= job.Limits.MaxVertices && a.TriangleCount == e.TriangleCount, "split vertex/triangle counts differ");
                    perPoseVertices = checked(perPoseVertices + a.VertexCount); perPoseTriangles = checked(perPoseTriangles + a.TriangleCount);
                    Need(perPoseVertices <= job.Limits.MaxVertices && perPoseTriangles <= job.Limits.MaxTriangles, "complete per-pose geometry exceeds declared limits");
                    var observed = ReadMesh(workspace, output, a.GeometryFile, a.GeometrySha256, a.VertexCount, a.TriangleCount, true, job.Limits, inventory, usedOutputFiles, work);
                    var original = ReadMesh(workspace, sourceDirectory, e.GeometryFile, e.GeometrySha256, e.VertexCount, e.TriangleCount, false, job.Limits, null, null, work);
                    CompareGeometry(observed, original, binding.WorldPose, e.WorldPose, design.Numerics, work);
                    verifiedTriangles = checked(verifiedTriangles + observed.Triangles.Length);
                    if (rest) Skin(workspace, output, a, e, i, imported, observed, job.Limits, design.Numerics, inventory, usedOutputFiles, work);
                    else Need(a.Skin is null, "sample duplicates full skin; use bound rest skin and actual sampled deformation");
                    // glTF normal seams duplicate indices. Retain the full raw evidence above,
                    // but give the manifold checker an exact-coordinate logical representation.
                    // This cannot close an actual gap: CompareGeometry rejects unequal copies.
                    var logicalPositions = new Vec3[original.Vertices.Length];
                    for (var v = 0; v < observed.Vertices.Length; v++) { work.Charge(); logicalPositions[observed.Logical[v]] = observed.Vertices[v]; }
                    var logicalTriangles = new TriangleIndex[observed.Triangles.Length];
                    for (var t = 0; t < logicalTriangles.Length; t++) { work.Charge(); var triangle = observed.Triangles[t]; logicalTriangles[t] = new(observed.Logical[triangle.A], observed.Logical[triangle.B], observed.Logical[triangle.C]); }
                    meshes.Add(new(a.InstanceId, binding.WorldPose, logicalPositions, logicalTriangles));
                }
                if (imported)
                {
                    var clearance = AssemblyClearanceVerifier.Verify(design, meshes, work, measureSurfaceMinimum: false);
                    Need(clearance.IsValid, "actual imported geometry clearance/interference differs: " + string.Join("; ", clearance.Issues.Select(x => x.Code)));
                }
                verifiedPoses++;
            }
            Pose(tree.Instances, source.Instances, true);
            for (var c = 0; c < tree.Clips.Count; c++)
            {
                var a = tree.Clips[c]; var e = motion.Clips[c]; var planned = motionJob.Plan.Clips[c];
                Need(a.Id == e.Id && a.PortableAnimationName == "bubo.clip." + c.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)
                    && a.Samples.Count == e.Samples.Count && e.Samples.Count == planned.Samples.Count, "portable clip identity/sample count differs");
                Channels(a, e, motion.Instances, job.Limits, work);
                for (var s = 0; s < a.Samples.Count; s++)
                {
                    Need(a.Samples[s].Index == s && Near(a.Samples[s].TimeSeconds, e.Samples[s].TimeSeconds, 1e-7), "actual animation sample time differs");
                    Pose(a.Samples[s].Instances, e.Samples[s].Instances, false);
                }
            }
        }
        Need(usedOutputFiles.SetEquals(inventory.Keys), "extra/unmeasured verification output is forbidden");
        foreach (var file in inputs) RequireFile(workspace, file, file.Path.EndsWith(".json", StringComparison.Ordinal) ? job.Limits.MaxJobBytes : job.Limits.MaxSourceBytes, cancellation);
        Inventory(workspace, output, report.Outputs, job.Limits, cancellation);
        return new("bubo-schematic-export-native-verification/v1", job.Representation!, refs.Glb!.Sha256, report, work.Checks, verifiedPoses, verifiedTriangles);
    }

    static IEnumerable<AssemblyExportFile> InputFiles(AssemblyExportInputs input)
    {
        yield return input.AssemblyJob; yield return input.AssemblyReport; yield return input.AssemblyVerification;
        yield return input.MotionJob; yield return input.MotionReport; yield return input.MotionVerification;
        yield return input.SourceAssembly; yield return input.SourceMotion;
        foreach (var item in new[] { input.Glb, input.ProducerReport, input.Package, input.RestVerification, input.DiagnosticVerification }) if (item is not null) yield return item;
    }
    static string ParentNode(AssemblyDesign design, string id)
    {
        var index = design.Instances.FindIndex(i => i.Id == id);
        if (index >= 0) return "bubo.instance." + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        index = design.Subassemblies.FindIndex(g => g.Id == id); Need(index >= 0, "unknown physical parent identity");
        return "bubo.group." + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
    public static void RequireLimits(AssemblyExportLimits limits) => Limits(limits);
    static void Limits(AssemblyExportLimits l)
    {
        Need(l.MaxJobBytes == StrictDesignJson.MaximumInputBytes && l.MaxSourceBytes is > 0 and <= ExportPackageValidator.MaximumBytes
            && l.MaxOutputBytes is > 0 and <= ExportPackageValidator.MaximumBytes && l.MaxVertices is > 0 and <= 750000 && l.MaxTriangles is > 0 and <= 250000
            && l.MaxNodes is > 0 and <= 65536 && l.MaxAccessors is > 0 and <= 262144 && l.MaxFiles is > 0 and <= ExportPackageValidator.MaximumFiles
            && l.MaxMotionSamples is > 0 and <= 32768 && l.MaxGeometryChecks is > 0 and <= GeometryWork.HardMaximum
            && l.MaxSkinJoints is > 0 and <= 16384 && l.MaxChannels is > 0 and <= 32768 && l.MaxPrimitives is > 0 and <= 16384
            && l.MaxViews is > 0 and <= 4096 && l.ImageSize is >= 64 and <= 4096 && l.RenderSamples is >= 1 and <= 256, "invalid/unbounded native limits");
    }
    static void Regular(IWorkspace workspace, string path)
    {
        var absolute = workspace.Resolve(path); var cursor = absolute;
        while (!string.Equals(cursor, workspace.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        { Need((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "linked source/output path forbidden"); cursor = Path.GetDirectoryName(cursor) ?? throw new InvalidOperationException("Output escapes workspace"); }
        Need(File.Exists(absolute) && (File.GetAttributes(absolute) & FileAttributes.Directory) == 0, "regular existing source/output file required");
    }
    static void RequireFile(IWorkspace workspace, AssemblyExportFile file, long max, CancellationToken ct)
    {
        Need(ExportPackageValidator.IsHash(file.Sha256) && file.Bytes > 0 && file.Bytes <= max, "invalid file reference/budget"); Regular(workspace, file.Path);
        Need(new FileInfo(workspace.Resolve(file.Path)).Length == file.Bytes && PartOutputVerifier.FileHash(workspace, file.Path, ct, max) == file.Sha256, "referenced bytes/hash changed");
    }
    static Dictionary<string, AssemblyExportFile> Inventory(IWorkspace workspace, string output, IReadOnlyList<AssemblyExportFile> files, AssemblyExportLimits limits, CancellationToken ct)
    {
        Need(files.Count is > 0 && files.Count < limits.MaxFiles, "bounded nonempty output inventory required");
        var result = new Dictionary<string, AssemblyExportFile>(StringComparer.Ordinal); long total = new FileInfo(workspace.Resolve(output + "/report.json")).Length;
        var caseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            Need(ExportPackageValidator.IsPortablePath(file.Path) && file.Path != "report.json" && caseNames.Add(file.Path) && result.TryAdd(file.Path, file), "ambiguous output filename");
            Need(file.Bytes <= limits.MaxOutputBytes - total, "whole output bytes exceed approved budget"); total = checked(total + file.Bytes);
            RequireFile(workspace, file with { Path = Path.Combine(output, file.Path) }, limits.MaxOutputBytes, ct);
        }
        var found = new HashSet<string>(StringComparer.Ordinal); var pending = new Queue<string>(); pending.Enqueue(workspace.Resolve(output)); var directories = 0;
        while (pending.TryDequeue(out var dir))
        {
            ct.ThrowIfCancellationRequested(); Need(++directories <= limits.MaxFiles && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0, "linked/unbounded output directories");
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested(); var attrs = File.GetAttributes(entry); Need((attrs & FileAttributes.ReparsePoint) == 0, "linked output entry");
                if ((attrs & FileAttributes.Directory) != 0) pending.Enqueue(entry);
                else { Need(found.Count < limits.MaxFiles, "output file ceiling exceeded"); found.Add(Path.GetRelativePath(workspace.Resolve(output), entry).Replace('\\', '/')); }
            }
        }
        found.Remove("report.json"); Need(found.SetEquals(result.Keys), "unexpected or missing output files"); return result;
    }
    static Mesh ReadMesh(IWorkspace workspace, string directory, string name, string hash, int vertexCount, int triangleCount,
        bool exported, AssemblyExportLimits limits, Dictionary<string, AssemblyExportFile>? inventory, HashSet<string>? used, GeometryWork work)
    {
        Need(ExportPackageValidator.IsPortablePath(name) && vertexCount is > 0 && vertexCount <= limits.MaxVertices && triangleCount is > 0 && triangleCount <= limits.MaxTriangles, "invalid mesh filename/counts");
        var bytes = checked(16L + (exported ? 28L : 24L) * vertexCount + 12L * triangleCount);
        if (inventory is not null) Need(inventory.TryGetValue(name, out var file) && file.Sha256 == hash && file.Bytes == bytes && used!.Add(name), "geometry file absent/aliased or identity differs");
        RequireFile(workspace, new(Path.Combine(directory, name), hash, bytes), limits.MaxSourceBytes, work.CancellationToken);
        using var stream = new FileStream(workspace.Resolve(Path.Combine(directory, name)), FileMode.Open, FileAccess.Read, FileShare.Read); using var reader = new BinaryReader(stream);
        Need(reader.ReadBytes(8).AsSpan().SequenceEqual(exported ? "BUBOEXP1"u8 : "BUBOPRT1"u8) && reader.ReadUInt32() == vertexCount && reader.ReadUInt32() == triangleCount, "binary mesh header differs");
        var vertices = new Vec3[vertexCount]; var logical = new int[vertexCount]; var triangles = new TriangleIndex[triangleCount];
        for (var i = 0; i < vertices.Length; i++) { work.Charge(); vertices[i] = new() { X = reader.ReadDouble(), Y = reader.ReadDouble(), Z = reader.ReadDouble() }; Need(Finite(vertices[i]), "nonfinite delivered vertex"); }
        for (var i = 0; i < logical.Length; i++) { work.Charge(); logical[i] = exported ? checked((int)reader.ReadUInt32()) : i; }
        for (var i = 0; i < triangles.Length; i++)
        {
            work.Charge(); var a = reader.ReadUInt32(); var b = reader.ReadUInt32(); var c = reader.ReadUInt32();
            Need(a < vertexCount && b < vertexCount && c < vertexCount && a != b && a != c && b != c, "invalid triangle"); triangles[i] = new((int)a, (int)b, (int)c);
        }
        Need(stream.ReadByte() == -1, "binary grew while read"); return new(vertices, triangles, logical);
    }

    static TriangleIndex CanonicalTriangle(TriangleIndex triangle)
    {
        if (triangle.B < triangle.A && triangle.B < triangle.C) return new(triangle.B, triangle.C, triangle.A);
        return triangle.C < triangle.A && triangle.C < triangle.B ? new(triangle.C, triangle.A, triangle.B) : triangle;
    }
    internal static void CompareGeometry(Mesh actual, Mesh source, RigidPose actualWorld, RigidPose sourceWorld, NumericalPolicy policy, GeometryWork work)
    {
        var sourceUsed = new bool[source.Vertices.Length]; var representatives = new Vec3?[source.Vertices.Length]; var rawUsed = new bool[actual.Vertices.Length];
        for (var v = 0; v < actual.Vertices.Length; v++)
        {
            work.Charge(); var logical = actual.Logical[v]; Need(logical >= 0 && logical < source.Vertices.Length, "logical source index outside prototype"); sourceUsed[logical] = true;
            var a = actual.Vertices[v]; var e = source.Vertices[logical]; var world = Point(actualWorld, a);
            Need(representatives[logical] is null || representatives[logical] == a, "split copies of one logical vertex have a real geometric gap"); representatives[logical] = a;
            Need(Near(a, e, policy.LinearTolerance) && Near(world, Point(sourceWorld, e), policy.LinearTolerance), "actual local/world geometry changes approved dimensions/openings/deformation");
            Need(Math.Max(Math.Abs(a.X), Math.Max(Math.Abs(a.Y), Math.Abs(a.Z))) <= policy.MaxCoordinateMagnitude, "actual local coordinate escapes approved bounds");
            Need(Math.Max(Math.Abs(world.X), Math.Max(Math.Abs(world.Y), Math.Abs(world.Z))) <= policy.MaxCoordinateMagnitude, "actual world coordinate escapes approved bounds");
        }
        Need(sourceUsed.All(x => x), "logical mapping omits original physical vertices");
        var triangles = new Dictionary<TriangleIndex, int>();
        foreach (var t in source.Triangles) { work.Charge(); var key = CanonicalTriangle(t); triangles[key] = checked(triangles.GetValueOrDefault(key) + 1); }
        foreach (var t in actual.Triangles)
        {
            work.Charge(); rawUsed[t.A] = rawUsed[t.B] = rawUsed[t.C] = true;
            var key = CanonicalTriangle(new(actual.Logical[t.A], actual.Logical[t.B], actual.Logical[t.C]));
            Need(triangles.TryGetValue(key, out var count) && count > 0, "oriented triangle topology/multiplicity changed"); triangles[key] = count - 1;
        }
        Need(rawUsed.All(x => x) && triangles.Values.All(x => x == 0), "unreferenced split vertex or missing/fused triangle");
    }
    static void PoseNear(RigidPose actual, RigidPose expected, NumericalPolicy policy)
    {
        Need(Finite(actual.Origin) && Finite(actual.X) && Finite(actual.Y) && Finite(actual.Z) && Dot(actual.X, Cross(actual.Y, actual.Z)) > 0, "invalid handedness/finite frame");
        double Angle(Vec3 a, Vec3 b) => Math.Atan2(Length(Cross(Normalize(a), Normalize(b))), Math.Clamp(Dot(Normalize(a), Normalize(b)), -1, 1)) * 180 / Math.PI;
        Need(Near(actual.Origin, expected.Origin, policy.LinearTolerance) && Angle(actual.X, expected.X) <= policy.AngularToleranceDegrees
            && Angle(actual.Y, expected.Y) <= policy.AngularToleranceDegrees && Angle(actual.Z, expected.Z) <= policy.AngularToleranceDegrees
            && Math.Abs(Length(actual.X) - 1) * policy.MinimumFeatureSize <= policy.LinearTolerance
            && Math.Abs(Length(actual.Y) - 1) * policy.MinimumFeatureSize <= policy.LinearTolerance
            && Math.Abs(Length(actual.Z) - 1) * policy.MinimumFeatureSize <= policy.LinearTolerance, "actual hierarchy/pivot frame differs");
    }
    static void Channels(AssemblyExportClip actual, AssemblyClipMeasurement source, IReadOnlyList<AssemblyInstanceMeasurement> rest, AssemblyExportLimits limits, GeometryWork work)
    {
        Need(actual.Channels.Count is > 0 && actual.Channels.Count <= limits.MaxChannels, "bounded channels required");
        var expectedNodes = new HashSet<string>(rest.Select(i => i.ObjectName), StringComparer.Ordinal); var optionalRoots = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rest.Count; i++) if (rest[i].Rig is { } rig)
        { expectedNodes.Add(rig.ArmatureName); optionalRoots.Add(Bone(i, 0)); for (var b = 1; b < rig.Bones.Count; b++) expectedNodes.Add(Bone(i, b)); }
        var channels = new HashSet<(string Node, string Path)>(); var actualNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in actual.Channels)
        {
            work.Charge(); Need((expectedNodes.Contains(channel.Node) || optionalRoots.Contains(channel.Node)) && channel.Path is "translation" or "rotation" or "scale" && channels.Add((channel.Node, channel.Path)), "unknown/duplicate animation target/path");
            Need(channel.Times.Count == source.Samples.Count, "animation channel lost sampled keys");
            for (var t = 0; t < channel.Times.Count; t++) { work.Charge(); Need(Near(channel.Times[t], source.Samples[t].TimeSeconds, 1e-7), "animation sampler times differ"); }
            actualNodes.Add(channel.Node);
        }
        Need(expectedNodes.IsSubsetOf(actualNodes) && expectedNodes.All(n => channels.Contains((n, "translation")) && channels.Contains((n, "rotation"))), "omitted independent rigid/bone motion channels");
    }
    static void Review(AssemblyExportReview review, Dictionary<string, AssemblyExportFile> inventory, AssemblyBuildReport motion, AssemblyDesign design, AssemblyExportLimits limits)
    {
        Need(review.SchemaVersion == "bubo-schematic-export-review/v1" && review.Complete && !review.HumanModelAccepted && !review.RuntimeAccepted
            && review.Views.Count is > 0 && review.Views.Count <= limits.MaxViews && review.Views.Count == inventory.Count - 1, "complete bounded review inventory is required");
        var viewPaths = new HashSet<string>(StringComparer.Ordinal);
        var physical = motion.Instances.Select(i => i.InstanceId).ToHashSet(StringComparer.Ordinal);
        foreach (var view in review.Views)
        {
            Need(viewPaths.Add(view.Path) && inventory.TryGetValue(view.Path, out var file) && file.Sha256 == view.Sha256 && file.Bytes == view.Bytes
                && view.Path.EndsWith(".png", StringComparison.Ordinal) && view.Kind is "exterior" or "section" or "exploded" or "diagnostic"
                && view.InstanceIds.Count == physical.Count && view.InstanceIds.ToHashSet(StringComparer.Ordinal).SetEquals(physical), "view hash/type/physical inventory differs");
            if (view.Kind != "diagnostic") Need(view.ClipId is null && view.SampleIndex is null && view.TimeSeconds is null, "static review has diagnostic labels");
        }
        Need(new[] { "exterior", "section", "exploded" }.All(kind => review.Views.Any(v => v.Kind == kind)), "exterior/internal/exploded review coverage is incomplete");
        var staticViews = Enumerable.Range(0,8).Select(i => (Path: "exterior." + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".png", Kind: "exterior"))
            .Concat(Enumerable.Range(0,Math.Max(1,design.Sections.Count)).Select(i => (Path: (design.Sections.Count==0?"section.inspection-only.":"section.") + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".png", Kind: "section")))
            .Append((Path: "exploded.000000.png", Kind: "exploded")).ToArray();
        Need(review.Views.Count(v => v.Kind != "diagnostic") == staticViews.Length
            && staticViews.All(v => review.Views.Any(actual => actual.Path == v.Path && actual.Kind == v.Kind)), "fixed exterior views, every approved internal section and exploded inventory are required");
        Need(review.ClipCoverage.Count == motion.Clips.Count, "diagnostic review omits a clip");
        var frames = new HashSet<string>(StringComparer.Ordinal);
        for (var c = 0; c < motion.Clips.Count; c++)
        {
            var clip = motion.Clips[c]; var covered = review.ClipCoverage[c];
            Need(covered.ClipId == clip.Id && covered.PortableAnimationName == "bubo.clip." + c.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)
                && covered.SampleCount == clip.Samples.Count && covered.FrameFiles.Count == clip.Samples.Count, "whole diagnostic clip review differs");
            for (var s = 0; s < clip.Samples.Count; s++)
            {
                var path = covered.FrameFiles[s]; Need(frames.Add(path), "one image cannot stand in for multiple diagnostic poses");
                var view = review.Views.SingleOrDefault(v => v.Path == path);
                Need(view is not null && view.Kind == "diagnostic" && view.ClipId == clip.Id && view.SampleIndex == s
                    && view.TimeSeconds is { } time && Near(time, clip.Samples[s].TimeSeconds, 1e-7), "diagnostic endpoint/intermediate sample review is missing");
            }
        }
        Need(review.Views.Count(v => v.Kind == "diagnostic") == frames.Count, "unexpected or unlabeled diagnostic images");
    }
    static void Skin(IWorkspace workspace, string output, AssemblyExportInstanceMeasurement actual, AssemblyInstanceMeasurement source,
        int instanceIndex, bool imported, Mesh mesh, AssemblyExportLimits limits, NumericalPolicy policy,
        Dictionary<string, AssemblyExportFile> inventory, HashSet<string> used, GeometryWork work)
    {
        if (source.Rig is null) { Need(actual.Skin is null, "rigid physical node acquired an undeclared skin"); return; }
        var skin = actual.Skin; var expected = source.Rig; Need(skin is not null && skin.RigNode == expected.ArmatureName, "flexible owned rig is missing/changed");
        PoseNear(skin.LocalPose, expected.ArmatureLocalPose, policy); PoseNear(skin.WorldPose, expected.ArmatureWorldPose, policy);
        Need(skin.Joints.Count == expected.Bones.Count && skin.Joints.Count <= limits.MaxSkinJoints && skin.Parents.Count == skin.Joints.Count
            && skin.InverseBindMatrices.Count == skin.Joints.Count && skin.Bones.Count == skin.Joints.Count, "joint/inverse-bind/bone inventory differs");
        for (var b = 0; b < expected.Bones.Count; b++)
        {
            work.Charge(); var name = Bone(instanceIndex, b); var parent = b == 0 ? null : Bone(instanceIndex, 0);
            Need(skin.Joints[b] == name && skin.Parents[b] == parent && skin.Bones[b].Name == name && skin.Bones[b].ParentName == parent,
                "cross-rig joint ownership or hierarchy changed");
            Need(double.IsFinite(skin.Bones[b].Length) && skin.Bones[b].Length > 0, "invalid imported bone measurement");
            if (!imported) PoseNear(skin.Bones[b].RestPose, expected.Bones[b].RestPose, policy);
            var matrix = skin.InverseBindMatrices[b]; Need(matrix.Count == 16 && matrix.All(double.IsFinite), "invalid actual inverse bind");
            if (!imported) VerifyBind(matrix, source.WorldPose, expected.ArmatureWorldPose, expected.Bones[b].RestPose, policy);
        }
        Need(ExportPackageValidator.IsPortablePath(skin.WeightsFile), "invalid weights filename");
        Need(inventory.TryGetValue(skin.WeightsFile, out var weightsFile)
            && weightsFile.Sha256 == skin.WeightsSha256 && used.Add(skin.WeightsFile), "owned weights output missing/aliased");
        RequireFile(workspace, weightsFile! with { Path = Path.Combine(output, skin.WeightsFile) }, limits.MaxOutputBytes, work.CancellationToken);
        using var stream = File.OpenRead(workspace.Resolve(Path.Combine(output, skin.WeightsFile))); using var reader = new BinaryReader(stream);
        Need(reader.ReadBytes(8).AsSpan().SequenceEqual("BUBOSKX1"u8) && reader.ReadUInt32() == mesh.Vertices.Length, "skin binary header differs");
        var rings = new int[source.VertexCount]; Array.Fill(rings, -1);
        for (var ring = 0; ring < expected.RingVertexIndices.Count; ring++) foreach (var vertex in expected.RingVertexIndices[ring])
        { work.Charge(); Need(vertex >= 0 && vertex < rings.Length && rings[vertex] == -1, "source ring ownership invalid"); rings[vertex] = ring + 1; }
        Need(rings.All(r => r > 0), "source flexible vertices are not completely owned");
        for (var v = 0; v < mesh.Vertices.Length; v++)
        {
            work.Charge(); Need(reader.ReadUInt32() == 1, "owned one-hot skin gained/lost influences"); var joint = reader.ReadUInt32(); var weight = reader.ReadDouble();
            Need(joint == rings[mesh.Logical[v]] && Near(weight, 1, 1e-7), "wrong rig/ring skin binding, weight or root influence");
        }
        Need(stream.ReadByte() == -1, "unexpected/trailing skin data");
    }
    static RigidPose AffineInverse(RigidPose a)
    {
        var determinant = Dot(a.X, Cross(a.Y, a.Z)); Need(double.IsFinite(determinant) && determinant > 0, "singular bind frame");
        var r0 = Scale(Cross(a.Y, a.Z), 1 / determinant); var r1 = Scale(Cross(a.Z, a.X), 1 / determinant); var r2 = Scale(Cross(a.X, a.Y), 1 / determinant);
        var inverse = new RigidPose(Vec3.Zero, new() { X = r0.X, Y = r1.X, Z = r2.X }, new() { X = r0.Y, Y = r1.Y, Z = r2.Y }, new() { X = r0.Z, Y = r1.Z, Z = r2.Z });
        return inverse with { Origin = Scale(Direction(inverse, a.Origin), -1) };
    }
    static void VerifyBind(IReadOnlyList<double> gltf, RigidPose mesh, RigidPose rig, RigidPose bone, NumericalPolicy policy)
    {
        // Column-major C^-1 * glTF IBM * C, where C maps source (x,y,z) to glTF (x,z,-y).
        Vec3 ToGltf(Vec3 v) => new() { X = v.X, Y = v.Z, Z = -v.Y };
        Vec3 FromGltf(Vec3 v) => new() { X = v.X, Y = -v.Z, Z = v.Y };
        Vec3 Apply(Vec3 v, bool point)
        {
            var q = ToGltf(v); var w = point ? 1d : 0d;
            Need(Near(gltf[3] * q.X + gltf[7] * q.Y + gltf[11] * q.Z + gltf[15] * w, w, 1e-6), "inverse bind is not affine");
            return FromGltf(new() { X = gltf[0] * q.X + gltf[4] * q.Y + gltf[8] * q.Z + gltf[12] * w,
                Y = gltf[1] * q.X + gltf[5] * q.Y + gltf[9] * q.Z + gltf[13] * w, Z = gltf[2] * q.X + gltf[6] * q.Y + gltf[10] * q.Z + gltf[14] * w });
        }
        var actual = new RigidPose(Apply(Vec3.Zero, true), Apply(RigidPose.Identity.X, false), Apply(RigidPose.Identity.Y, false), Apply(RigidPose.Identity.Z, false));
        var expected = Compose(AffineInverse(Compose(rig, bone)), mesh); PoseNear(actual, expected, policy);
    }
}
