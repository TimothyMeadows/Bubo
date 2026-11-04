using System.Security.Cryptography;
using System.Text.Json;
using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using Hephaera.AssetProduction;
using static Bubo.Schematic.Domain.RigidTransforms;
using static Bubo.Schematic.Tests.DesignExamples;

namespace Bubo.Schematic.Tests;

/// <summary>SIMULATED reports and binary engineering fixtures; never Blender or human production acceptance.</summary>
public static class AssemblyOutputTests
{
    static readonly string JobHash = new('9', 64);
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static string Name(string kind, int index, int? sub = null) => "bubo." + kind + "." + index.ToString("D6") + (sub is { } value ? "." + value.ToString("D6") : "");
    static void Write<T>(string path, T value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, StrictDesignJson.Options));
    static (Vec3[] Vertices, TriangleIndex[] Triangles) ReadMesh(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path)); reader.ReadBytes(8);
        var vertices = reader.ReadUInt32(); var triangles = reader.ReadUInt32(); var positions = new Vec3[vertices]; var indices = new TriangleIndex[triangles];
        for (var i = 0; i < vertices; i++) positions[i] = V(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        for (var i = 0; i < triangles; i++) indices[i] = new((int)reader.ReadUInt32(), (int)reader.ReadUInt32(), (int)reader.ReadUInt32());
        return (positions, indices);
    }
    static void WriteMesh(string path, IReadOnlyList<Vec3> positions, IReadOnlyList<TriangleIndex> triangles)
    {
        using var writer = new BinaryWriter(File.Create(path)); writer.Write("BUBOPRT1"u8); writer.Write((uint)positions.Count); writer.Write((uint)triangles.Count);
        foreach (var p in positions) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
        foreach (var t in triangles) { writer.Write((uint)t.A); writer.Write((uint)t.B); writer.Write((uint)t.C); }
    }
    static Vec3 LinearColor(MaterialSpec material)
    {
        double Channel(int offset) { var v = Convert.ToInt32(material.Color.Substring(offset, 2), 16) / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return V(Channel(1), Channel(3), Channel(5));
    }

    /// <summary>Shared synthetic native-process helper. Writes only author payload/report; the simulated independent stage copies the receipt separately.</summary>
    public static void Produce(Workspace workspace, AssemblyBuildJob job, string jobHash)
    {
        Directory.CreateDirectory(job.Output); var source = job.Operation == "assemble" ? "assembly.blend" : "diagnostic.blend";
        File.WriteAllText(Path.Combine(job.Output, source), "SIMULATED EDITABLE ASSEMBLY PROTOCOL FIXTURE; NO ACTUAL BLENDER SCENE");
        long vertices = 0, triangles = 0, bytes = new FileInfo(Path.Combine(job.Output, source)).Length;
        Dictionary<string, RigidPose> Worlds(AssemblyPose pose)
        {
            var result = pose.Instances.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            RigidPose Group(string id)
            {
                if (result.TryGetValue(id, out var value)) return value;
                var group = job.Design.Subassemblies.Single(g => g.Id == id);
                return result[id] = Compose(group.ParentId is null ? RigidPose.Identity : Group(group.ParentId), FromFrame(group.Frame));
            }
            foreach (var group in job.Design.Subassemblies) Group(group.Id); return result;
        }
        IReadOnlyList<AssemblyInstanceMeasurement> Pose(AssemblyPose pose, string prefix, string clipId, int sampleIndex)
        {
            var worlds = Worlds(pose); var result = new List<AssemblyInstanceMeasurement>();
            for (var index = 0; index < job.Design.Instances.Count; index++)
            {
                var instance = job.Design.Instances[index]; var part = job.Design.Parts.Single(p => p.Id == instance.PartId); var prototype = job.Parts.Parts.Single(p => p.Id == part.Id);
                var mesh = ReadMesh(Path.Combine(job.PartsDirectory, prototype.GeometryFile)); var evaluated = mesh.Vertices.ToArray();
                var world = worlds[instance.Id]; var local = Compose(Inverse(worlds[instance.ParentId]), world);
                var program = job.Rigs.SingleOrDefault(r => r.InstanceId == instance.Id); AssemblyRigMeasurement? rig = null;
                if (program is not null)
                {
                    var frames = sampleIndex < 0 ? program.RestRingFrames : program.Clips.Single(c => c.ClipId == clipId).Samples.Single(s => s.SampleIndex == sampleIndex).RingFrames;
                    var rings = new List<IReadOnlyList<int>>(); var centers = new List<Vec3>();
                    using var weights = new MemoryStream(); using var writer = new BinaryWriter(weights); writer.Write("BUBOSKN1"u8); writer.Write((uint)evaluated.Length);
                    for (var ring = 0; ring < frames.Count; ring++)
                    {
                        var indices = Enumerable.Range(ring * program.RadialSegments, program.RadialSegments).ToArray(); rings.Add(indices); Vec3 center = Vec3.Zero;
                        foreach (var vertex in indices)
                        {
                            evaluated[vertex] = Point(Compose(frames[ring], Inverse(program.RestRingFrames[ring])), mesh.Vertices[vertex]);
                            center = Add(center, Scale(evaluated[vertex], 1d / indices.Length)); writer.Write((uint)ring); writer.Write(1d);
                        }
                        centers.Add(center);
                    }
                    var lengths = Enumerable.Range(0, program.ClosedPath ? centers.Count : centers.Count - 1).Select(i => Length(Subtract(centers[(i + 1) % centers.Count], centers[i]))).ToArray();
                    var angles = Enumerable.Range(0, centers.Count).Select(i =>
                    {
                        if (!program.ClosedPath && (i == 0 || i == centers.Count - 1)) return 0d;
                        var a = Subtract(centers[i], centers[(i + centers.Count - 1) % centers.Count]); var b = Subtract(centers[(i + 1) % centers.Count], centers[i]);
                        return Math.Acos(Math.Clamp(Dot(a, b) / (Length(a) * Length(b)), -1, 1)) * 180 / Math.PI;
                    }).ToArray();
                    var bones = new List<AssemblyBoneMeasurement> { new("root", null, RigidPose.Identity, RigidPose.Identity, program.BoneLength) };
                    bones.AddRange(frames.Select((frame, i) => new AssemblyBoneMeasurement(Name("ring", i), "root", program.RestRingFrames[i], frame, program.BoneLength)));
                    writer.Flush(); rig = new(Name("rig", index), local, world, bones, rings, Hash(weights.ToArray()), centers, lengths, angles);
                }
                var filename = prefix + "." + index.ToString("D6") + ".geometry.bin"; var path = Path.Combine(job.Output, filename);
                WriteMesh(path, evaluated, mesh.Triangles); vertices += evaluated.Length; triangles += mesh.Triangles.Length; bytes += new FileInfo(path).Length;
                var material = job.Design.Materials.Single(m => m.Id == part.MaterialId); var ports = new List<AssemblyPortMeasurement>();
                foreach (var (kind, definitions) in new[] { ("interface", part.Interfaces.Select(p => (p.Id, p.Frame))), ("terminal", part.Terminals.Select(p => (p.Id, p.Frame))) })
                    foreach (var (port, ordinal) in definitions.Select((port, ordinal) => (port, ordinal)))
                    { var frame = FromFrame(port.Frame); ports.Add(new(port.Id, kind, Name(kind, index, ordinal), frame, Compose(world, frame))); }
                result.Add(new() { InstanceId = instance.Id, PartId = part.Id, ObjectName = Name("instance", index), ParentId = instance.ParentId,
                    LocalPose = local, WorldPose = world, MaterialId = part.MaterialId, MaterialColorLinear = LinearColor(material), MaterialRoughness = material.Roughness,
                    MaterialMetallic = material.Metallic, Ports = ports, PrototypeBlendSha256 = prototype.BlendSha256, PrototypeGeometrySha256 = prototype.GeometrySha256,
                    GeometryFile = filename, GeometrySha256 = workspace.HashFile(path), VertexCount = evaluated.Length, TriangleCount = mesh.Triangles.Length, Rig = rig });
            }
            return result;
        }
        var instances = Pose(job.Plan.Rest, "rest", "rest", -1); var clips = new List<AssemblyClipMeasurement>();
        for (var clipIndex = 0; clipIndex < job.Plan.Clips.Count; clipIndex++)
        {
            var clip = job.Plan.Clips[clipIndex]; var actions = new List<AssemblyActionMeasurement>();
            for (var i = 0; i < job.Design.Instances.Count; i++)
                foreach (var kind in job.Rigs.Any(r => r.InstanceId == job.Design.Instances[i].Id) ? new[] { "instance", "rig" } : new[] { "instance" })
                {
                    var channels = kind == "instance" ? 7 : 7 * (1 + job.Rigs.Single(r => r.InstanceId == job.Design.Instances[i].Id).RestRingFrames.Count);
                    actions.Add(new(job.Design.Instances[i].Id, Name(kind, i), Name("action", clipIndex, actions.Count), channels, channels * clip.Samples.Count));
                }
            var samples = new List<AssemblySampleMeasurement>();
            if (job.Operation == "prove-motion")
                for (var s = 0; s < clip.Samples.Count; s++) samples.Add(new(s, clip.Samples[s].TimeSeconds,
                    Pose(clip.Samples[s].Pose, "clip." + clipIndex.ToString("D6") + ".sample." + s.ToString("D6"), clip.Id, s)));
            clips.Add(new(clip.Id, actions, samples));
        }
        var report = new AssemblyBuildReport { SchemaVersion = "bubo-schematic-assembly/v1", JobSha256 = jobHash, Identity = job.Identity, BlenderVersion = "4.5.3 LTS",
            Operation = job.Operation, SourceFile = source, SourceSha256 = workspace.HashFile(Path.Combine(job.Output, source)), Instances = instances, Clips = clips,
            TotalVertices = vertices, TotalTriangles = triangles, TotalBytes = bytes };
        Write(Path.Combine(job.Output, job.Operation == "assemble" ? "assembly-report.json" : "motion-report.json"), report);
    }

    sealed class Fixture
    {
        public Workspace Workspace { get; }
        public AssemblyBuildJob Job { get; set; }
        public string Report => Path.Combine(Job.Output, Job.Operation == "assemble" ? "assembly-report.json" : "motion-report.json");
        public string Verification => Path.Combine(Job.Output, Job.Operation == "assemble" ? "assembly-verification.json" : "motion-verification.json");
        public Fixture(AssemblyDesign? design = null, bool motion = false)
        {
            var repository = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repository, "Bubo.slnx"))) repository = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException("Fixture repository missing");
            var root = Path.Combine(repository, ".bubo/schematic-assembly-output-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); Workspace = new(root);
            design ??= Rigid(); var plan = new AssemblyMotionCompiler().Compile(design, design.AssemblyProfile!.SamplesPerRange, design.AssemblyProfile.SamplesPerCycle);
            var rigs = new FlexibleMotionCompiler().Compile(design, plan); var partsDirectory = Workspace.Resolve("parts"); Directory.CreateDirectory(partsDirectory);
            var definitions = PartOutputVerifier.PartIdentities(design); var artifacts = new List<PartArtifact>(); long partBytes = 0;
            foreach (var part in design.Parts)
            {
                Vec3[] positions; TriangleIndex[] indices;
                if (part.Kind == PartKind.Rigid)
                {
                    var box = part.Features.Single(); Check.True(box.Kind == FeatureKind.Box, "Synthetic output fixture supports actual boxes only");
                    var size = V(box.Parameters["width"], box.Parameters["depth"], box.Parameters["height"]);
                    positions = [V(), V(size.X), V(size.X, size.Y), V(0, size.Y), V(z: size.Z), V(size.X, z: size.Z), V(size.X, size.Y, size.Z), V(0, size.Y, size.Z)];
                    positions = positions.Select(p => DesignGeometry.TransformPoint(box.Frame, p)).ToArray();
                    int[] raw = [0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7];
                    indices = Enumerable.Range(0, raw.Length / 3).Select(i => new TriangleIndex(raw[3 * i], raw[3 * i + 1], raw[3 * i + 2])).ToArray();
                }
                else
                {
                    var rig = rigs.First(r => design.Instances.Single(i => i.Id == r.InstanceId).PartId == part.Id); var radius = part.Features[0].Parameters["radius"]; var radial = rig.RadialSegments;
                    positions = rig.RestRingFrames.SelectMany(frame => Enumerable.Range(0, radial).Select(i => Add(frame.Origin,
                        Add(Scale(frame.X, radius * Math.Cos(2 * Math.PI * i / radial)), Scale(frame.Z, -radius * Math.Sin(2 * Math.PI * i / radial)))))).ToArray();
                    var topology = new List<TriangleIndex>();
                    for (var r = 0; r < (rig.ClosedPath ? rig.RestRingFrames.Count : rig.RestRingFrames.Count - 1); r++)
                        for (var i = 0; i < radial; i++)
                        {
                            var next = (r + 1) % rig.RestRingFrames.Count; var k = (i + 1) % radial;
                            topology.Add(new(r * radial + i, r * radial + k, next * radial + k)); topology.Add(new(r * radial + i, next * radial + k, next * radial + i));
                        }
                    if (!rig.ClosedPath)
                        for (var i = 1; i < radial - 1; i++)
                        {
                            topology.Add(new(radial - 1, radial - 1 - i, radial - 2 - i)); var offset = (rig.RestRingFrames.Count - 1) * radial;
                            topology.Add(new(offset, offset + i, offset + i + 1));
                        }
                    indices = topology.ToArray();
                }
                var binary = Path.Combine(partsDirectory, part.Id + ".geometry.bin"); WriteMesh(binary, positions, indices);
                var blend = Path.Combine(partsDirectory, part.Id + ".blend"); File.WriteAllText(blend, "SIMULATED EDITABLE PART; NO ACTUAL BLENDER SCENE");
                partBytes += new FileInfo(binary).Length + new FileInfo(blend).Length; var material = design.Materials.Single(m => m.Id == part.MaterialId);
                artifacts.Add(new() { Id = part.Id, DefinitionSha256 = definitions[part.Id], MaterialId = part.MaterialId, BlendFile = part.Id + ".blend", BlendSha256 = Workspace.HashFile(blend),
                    GeometryFile = part.Id + ".geometry.bin", GeometrySha256 = Workspace.HashFile(binary), VertexCount = positions.Length, TriangleCount = indices.Length,
                    Minimum = V(), Maximum = part.Size, Volume = 1, SurfaceArea = 6, Components = 1, Manifold = true,
                    MaterialColorLinear = LinearColor(material), MaterialRoughness = material.Roughness, MaterialMetallic = material.Metallic });
            }
            var parts = new PartsReport { SchemaVersion = "bubo-schematic-parts/v1", JobSha256 = new('8', 64), Identity = new(new('a', 64), new('b', 64), new('c', 64), new('d', 64)),
                BlenderVersion = "4.5.3 LTS", Parts = artifacts, Instances = design.Instances, TotalBytes = partBytes,
                WeightedTriangles = design.Instances.Sum(i => (long)artifacts.Single(p => p.Id == i.PartId).TriangleCount) };
            Write(Path.Combine(partsDirectory, "parts-report.json"), parts); Write(Path.Combine(partsDirectory, "parts-verification.json"), parts);
            var reportHash = Workspace.HashFile(Path.Combine(partsDirectory, "parts-report.json")); var verifyHash = Workspace.HashFile(Path.Combine(partsDirectory, "parts-verification.json"));
            var output = Workspace.Resolve("output"); Directory.CreateDirectory(output); var source = Workspace.Resolve("source.blend"); File.WriteAllText(source, "SIMULATED immutable source");
            Job = new() { SchemaVersion = "bubo-schematic-assembly-job/v1", Operation = motion ? "prove-motion" : "assemble", ProjectRoot = Workspace.Root,
                Output = output, Design = design, PartsDirectory = partsDirectory, PartsReportSha256 = reportHash, PartsVerificationSha256 = verifyHash, Parts = parts,
                Identity = new(parts.Identity.DesignSha256, parts.Identity.DesignFingerprint, parts.Identity.RuntimeBuildSha256, parts.Identity.ToolSha256, parts.JobSha256, reportHash, verifyHash),
                Bundle = new(StringComparer.Ordinal), Limits = new(250_000, Math.Min(250_000, design.Budgets.MaxGeneratedTriangles), design.Budgets.MaxExportBytes,
                    StrictDesignJson.MaximumInputBytes, 3 + design.Instances.Count * (motion ? plan.Sampling.TotalSamples : 1), design.Budgets.MaxMotionSamples, design.AssemblyProfile.MaxGeometryChecks),
                Plan = plan, Rigs = rigs, SourceAssembly = motion ? source : null, SourceAssemblySha256 = motion ? Workspace.HashFile(source) : null };
            Produce(Workspace, Job, JobHash); File.Copy(Report, Verification);
        }
        public AssemblyNativeVerification Verify(CancellationToken ct = default) => AssemblyOutputVerifier.Verify(Workspace, Job.Output, Job, JobHash, ct);
        public void Change(Func<AssemblyBuildReport, AssemblyBuildReport> change)
        { var report = change(StrictDesignJson.Read<AssemblyBuildReport>(Report)); Write(Report, report); Write(Verification, report); }
        public void ChangeGeometry(Action<string> change)
        {
            var report = StrictDesignJson.Read<AssemblyBuildReport>(Report); var path = Path.Combine(Job.Output, report.Instances[0].GeometryFile); change(path);
            Change(r => r with { Instances = r.Instances.Select((i, index) => index == 0 ? i with { GeometrySha256 = Workspace.HashFile(path) } : i).ToArray() });
        }
    }
    static AssemblyDesign Rigid(bool moving = false)
    {
        var part = Part("block-definition", V(1, 1, 1), Box(1, 1, 1));
        var d = Base("assembly-output-fixture", [part], [Instance("first", part.Id), Instance("second", part.Id, x: moving ? 1 : 3)]) with
        { SchemaVersion = "bubo-assembly-design/v2", AssemblyProfile = FlexibleTests.Profile() with { MaxGeometryChecks = 10_000_000 } };
        if (!moving) return d;
        return d with { Parts = [part with { Interfaces = [Port("mount", V()), Port("slider", V(1))] }],
            Joints = [Joint("slide", JointKind.Slider, "first", "second", "slider") with { Axis = V(1), Minimum = 0, Maximum = .5, Rest = 0, Clearance = 0 }] };
    }
    static AssemblyDesign Hose()
    {
        var d = FlexibleTests.Open();
        return d with { AssemblyProfile = d.AssemblyProfile! with { MaxGeometryChecks = 10_000_000, Contacts =
            [new() { Id = "start-contact", FirstInstanceId = "start", SecondInstanceId = "flex", Frame = F(-.01, -.01, -.01), Size = V(.12, .12, .12), Reason = "Synthetic owned hose end fitting" },
                new() { Id = "end-contact", FirstInstanceId = "end", SecondInstanceId = "flex", Frame = F(-.01, -.01, -.01), Size = V(.12, .12, .12), Reason = "Synthetic owned hose end fitting" }] } };
    }
    static AssemblyDesign MovingHose()
    {
        var d = Hose(); var support = Part("support-definition", V(1.2, .3, 1.2), Box(.1, .1, .1) with { Frame = F(0, .15, .1) }) with
        { Interfaces = [Port("mount", d.Routes.Single().Points[^1])] };
        return d with { Parts = [.. d.Parts, support], Instances = [.. d.Instances, Instance("support", support.Id)],
            Joints = [Joint("end-slide", JointKind.Slider, "support", "end") with { Axis = V(1), Minimum = 0, Maximum = .05, Rest = 0, Clearance = 0 }] };
    }
    static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or JsonException or EndOfStreamException or OverflowException) { return; }
        throw new InvalidOperationException("Expected native assembly output rejection");
    }
    public static void Run()
    {
        Check.Case("assembly output: SIMULATED native binaries preserve separate repeated rigid instances", () =>
        { var f = new Fixture(); var result = f.Verify(); Check.True(result.Poses.Count == 1 && result.Poses[0].Clearance.IsValid && result.WorkChecks > 0 && result.Report.TotalVertices == 16); });
        Check.Case("assembly output: assemble retains actions while proof checks every actual sampled pose", () =>
        {
            var a = new Fixture(Rigid(true)); Check.True(a.Verify().Poses.Count == 1 && a.Verify().Report.Clips.All(c => c.Samples.Count == 0 && c.Actions.Count == 2));
            var f = new Fixture(Rigid(true), true); var result = f.Verify(); Check.True(result.Poses.Count == f.Job.Plan.Sampling.TotalSamples && result.Poses.Count > 1);
            f.Change(r => r with { Clips = r.Clips.Select(c => c with { Samples = c.Samples.Select(s => s with { Instances = r.Instances }).ToArray() }).ToArray() }); Reject(() => f.Verify());
        });
        Check.Case("assembly output: SIMULATED source rings and one-hot skin are measured from actual binary vertices", () =>
        {
            var f = new Fixture(Hose()); var result = f.Verify(); var measured = result.Poses.Single().Flexible.Single();
            Check.True(measured.InstanceId == "flex" && measured.MinimumBendRadius is null && Math.Abs(measured.MinimumSegmentRatio - 1) < 1e-9);
            f.Change(r => r with { Instances = r.Instances.Select(i => i.Rig is null ? i : i with { Rig = i.Rig with { WeightsSha256 = new('0', 64) } }).ToArray() }); Reject(() => f.Verify());
        });
        Check.Case("assembly output: binary skin cannot be forged by unchanged self-reported ring metrics", () =>
        {
            var f = new Fixture(Hose()); var report = StrictDesignJson.Read<AssemblyBuildReport>(f.Report); var flex = report.Instances.Single(i => i.Rig is not null);
            var path = Path.Combine(f.Job.Output, flex.GeometryFile); var mesh = ReadMesh(path); mesh.Vertices[0] = Add(mesh.Vertices[0], V(.001)); WriteMesh(path, mesh.Vertices, mesh.Triangles);
            f.Change(r => r with { Instances = r.Instances.Select(i => i.InstanceId == flex.InstanceId ? i with { GeometrySha256 = f.Workspace.HashFile(path) } : i).ToArray() }); Reject(() => f.Verify());
        });
        Check.Case("assembly output: full motion proof checks actual deformed hose positions and owned endpoints at every sample", () =>
        {
            var f = new Fixture(MovingHose(), true); var result = f.Verify(); Check.True(result.Poses.Count == f.Job.Plan.Sampling.TotalSamples);
            Check.True(result.Poses.SelectMany(p => p.Flexible).Max(m => m.MaximumSegmentRatio) > 1.04);
            f.Change(r => r with { Clips = r.Clips.Select(c => c with { Samples = c.Samples.Select(s => s with { Instances = s.Instances.Select(i => i.Rig is null ? i : i with
            { Rig = i.Rig with { Bones = i.Rig.Bones.Select(b => b with { Pose = b.RestPose }).ToArray() } }).ToArray() }).ToArray() }).ToArray() }); Reject(() => f.Verify());
        });
        Check.Case("assembly output: actual reopened receipts, seven-hash ancestry and immutable prototypes must agree", () =>
        {
            var a = new Fixture(); Write(a.Verification, StrictDesignJson.Read<AssemblyBuildReport>(a.Verification) with { TotalVertices = 99 }); Reject(() => a.Verify());
            var b = new Fixture(); b.Change(r => r with { Identity = r.Identity with { PartsJobSha256 = new('0', 64) } }); Reject(() => b.Verify());
            var c = new Fixture(); File.AppendAllText(Path.Combine(c.Job.PartsDirectory, c.Job.Parts.Parts[0].BlendFile), "tampered"); Reject(() => c.Verify());
            var d = new Fixture(motion: true); File.AppendAllText(d.Job.SourceAssembly!, "tampered"); Reject(() => d.Verify());
        });
        Check.Case("assembly output: truncated and huge binary headers reject before allocation even with matching claimed hashes", () =>
        {
            var a = new Fixture(); a.ChangeGeometry(path => { using var stream = File.OpenWrite(path); stream.SetLength(16); }); Reject(() => a.Verify());
            var b = new Fixture(); b.ChangeGeometry(path => { using var stream = File.OpenWrite(path); stream.Position = 8; using var writer = new BinaryWriter(stream); writer.Write(uint.MaxValue); }); Reject(() => b.Verify());
            var c = new Fixture(); c.ChangeGeometry(path => { using var stream = File.OpenWrite(path); stream.Position = 16; using var writer = new BinaryWriter(stream); writer.Write(double.NaN); }); Reject(() => c.Verify());
        });
        Check.Case("assembly output: topology, rigid positions, actual materials and local/unit poses are independently checked", () =>
        {
            var a = new Fixture(); a.ChangeGeometry(path => { var mesh = ReadMesh(path); mesh.Triangles[0] = mesh.Triangles[0] with { A = 1 }; WriteMesh(path, mesh.Vertices, mesh.Triangles); }); Reject(() => a.Verify());
            var b = new Fixture(); b.ChangeGeometry(path => { var mesh = ReadMesh(path); mesh.Vertices[0] = V(.1); WriteMesh(path, mesh.Vertices, mesh.Triangles); }); Reject(() => b.Verify());
            var c = new Fixture(); c.Change(r => r with { Instances = r.Instances.Select(i => i with { MaterialRoughness = .9 }).ToArray() }); Reject(() => c.Verify());
            var d = new Fixture(); d.Change(r => r with { Instances = r.Instances.Select(i => i with { LocalPose = i.LocalPose with { X = V(2) } }).ToArray() }); Reject(() => d.Verify());
            var e = new Fixture(); e.Change(r => r with { Instances = r.Instances.Select(i => i with { GeometryFile = "../rest.000000.geometry.bin" }).ToArray() }); Reject(() => e.Verify());
        });
        Check.Case("assembly output: strict approved dimensional and angular tolerances reject float32-representable basis tampering", () =>
        {
            var source = Rigid(); var precise = source with { Numerics = source.Numerics with { LinearTolerance = 1e-9, AngularToleranceDegrees = 1e-9 } };
            var a = new Fixture(precise); var scale = 1 + Math.Pow(2, -20);
            a.Change(r => r with { Instances = r.Instances.Select(i => i with
            { WorldPose = i.WorldPose with { X = Scale(i.WorldPose.X, scale) }, LocalPose = i.LocalPose with { X = Scale(i.LocalPose.X, scale) } }).ToArray() });
            Reject(() => a.Verify());
            var b = new Fixture(source with { Numerics = source.Numerics with { AngularToleranceDegrees = 1e-9 } }); var skew = Math.Pow(2, -25);
            RigidPose Shear(RigidPose p) => p with { X = Add(p.X, Scale(p.Y, skew)), Y = Add(p.Y, Scale(p.X, skew)) };
            b.Change(r => r with { Instances = r.Instances.Select(i => i with { WorldPose = Shear(i.WorldPose), LocalPose = Shear(i.LocalPose) }).ToArray() });
            Reject(() => b.Verify());
        });
        Check.Case("assembly output: float32 pure orientation rounding must satisfy actual approved geometry precision", () =>
        {
            var d = Rigid(); d = d with { Instances = d.Instances.Select(i => i with { Frame = i.Frame with { RotationDegrees = V(z: 45) } }).ToList() };
            RigidPose Float32(RigidPose pose)
            {
                Vec3 Rounded(Vec3 p) => V((float)p.X, (float)p.Y, (float)p.Z);
                return pose with { X = Rounded(pose.X), Y = Rounded(pose.Y), Z = Rounded(pose.Z) };
            }
            void Round(Fixture fixture) => fixture.Change(r => r with { Instances = r.Instances.Select(i => i with
            { LocalPose = Float32(i.LocalPose), WorldPose = Float32(i.WorldPose), Ports = i.Ports.Select(p => p with { WorldPose = Float32(p.WorldPose) }).ToArray() }).ToArray() });
            var a = new Fixture(d); Round(a); Check.True(a.Verify().Poses.Single().Clearance.IsValid);
            var b = new Fixture(d with { Numerics = d.Numerics with { LinearTolerance = 1e-10 } }); Round(b); Reject(() => b.Verify());
        });
        Check.Case("assembly output: raw rounded bone rest matrices use affine inversion without inventing rest skin deformation", () =>
        {
            var d = Hose(); var frame = F(.1, .1) with { RotationDegrees = V(z: 45) };
            Vec3 Transformed(Vec3 p) => DesignGeometry.TransformPoint(frame, p);
            var route = d.Routes.Single(); var points = route.Points.Select(Transformed).ToList();
            d = d with { Numerics = d.Numerics with { LinearTolerance = 1e-12, MinimumFeatureSize = 1e-6 },
                Parts = d.Parts.Select(p => p.Kind == PartKind.Rigid ? p : p with { Size = V(2, 2, .1), Features = [p.Features.Single() with { Frame = frame }] }).ToList(),
                Routes = [route with { Points = points }], Instances = d.Instances.Select(i => i.Id == "start" ? i with { Frame = i.Frame with { Origin = points[0] } }
                    : i.Id == "end" ? i with { Frame = i.Frame with { Origin = points[^1] } } : i).ToList() };
            RigidPose Rounded(RigidPose pose)
            {
                Vec3 Float32(Vec3 p) => V((float)p.X, (float)p.Y, (float)p.Z);
                return pose with { X = Float32(pose.X), Y = Float32(pose.Y), Z = Float32(pose.Z) };
            }
            var f = new Fixture(d);
            f.Change(r => r with { Instances = r.Instances.Select(i => i.Rig is null ? i : i with { Rig = i.Rig with
            { Bones = i.Rig.Bones.Select(b => b.Name == "root" ? b : b with { RestPose = Rounded(b.RestPose), Pose = Rounded(b.Pose) }).ToArray() } }).ToArray() });
            Check.True(f.Verify().Poses.Single().Flexible.Count == 1);
        });
        Check.Case("assembly output: exact output inventory and cumulative bytes include both bounded strict reports", () =>
        {
            var a = new Fixture(); File.WriteAllText(Path.Combine(a.Job.Output, "extra.txt"), "unexpected"); Reject(() => a.Verify());
            var b = new Fixture(); var bytes = StrictDesignJson.Read<AssemblyBuildReport>(b.Report).TotalBytes;
            b.Job = b.Job with { Design = b.Job.Design with { Budgets = b.Job.Design.Budgets with { MaxExportBytes = bytes } }, Limits = b.Job.Limits with { MaxOutputBytes = bytes } }; Reject(() => b.Verify());
            var c = new Fixture(); File.WriteAllText(c.Report, "{\"schemaVersion\":\"x\",\"schemaVersion\":\"y\"}"); Reject(() => c.Verify());
            var d = new Fixture(); using (var file = File.OpenWrite(d.Report)) file.SetLength(StrictDesignJson.MaximumInputBytes + 1L); Reject(() => d.Verify());
        });
        Check.Case("assembly output: cancellation and one cumulative geometry ledger stop remaining poses", () =>
        {
            var a = new Fixture(); using var ct = new CancellationTokenSource(); ct.Cancel(); Check.Throws<OperationCanceledException>(() => a.Verify(ct.Token));
            var d = Rigid() with { AssemblyProfile = FlexibleTests.Profile() with { MaxGeometryChecks = 10 } }; var b = new Fixture(d); Check.Throws<AssemblyGeometryException>(() => b.Verify());
        });
        Check.Case("assembly output: actual clearance failure retains structured witness evidence", () =>
        {
            var d = Rigid(); d = d with { Instances = [d.Instances[0], d.Instances[1] with { Frame = F(.5) }] }; var f = new Fixture(d);
            var error = Check.Throws<AssemblyOutputVerificationException>(() => f.Verify()); Check.True(!error.Report.IsValid && error.Report.Issues.Any(i => i.Code == "interference"));
        });
    }
}
