using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Tests;

/// <summary>Simulated native protocol fixtures. No Blender execution or human model acceptance is asserted.</summary>
public static class PartTests
{
    const string Manifest = "state/task/schematic-to-model/fixture/v1/manifest.json";
    static JsonObject View(object value) => JsonSerializer.SerializeToNode(value, StrictDesignJson.Options)!.AsObject();
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static AssemblyDesign Source() => DesignExamples.Base("part-fixture",
        [DesignExamples.Part("block-definition", DesignExamples.V(1, 1, 1), DesignExamples.Box(1, 1, 1)),
            DesignExamples.Part("secondary-definition", DesignExamples.V(.5, .5, .5), DesignExamples.Box(.5, .5, .5))],
        [DesignExamples.Instance("first", "block-definition"), DesignExamples.Instance("second", "block-definition", x: 2), DesignExamples.Instance("secondary", "secondary-definition", x:4)]);

    sealed class Fixture
    {
        public string Root { get; }
        public Workspace Workspace { get; }
        public DesignSession Design { get; }
        public SimulatedProcess Process { get; }
        public string Tool { get; }
        public string Bundle { get; }
        public string CatalogHash { get; }
        public PartSession Session(long? memory = 268_435_456, int timeout = 60, long logs = 65536) => new(Workspace, Design, Process, Bundle, timeout, logs, memory, CatalogHash);
        public Fixture(bool approved = true, AssemblyDesign? source = null)
        {
            var repository = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repository, "Bubo.slnx"))) repository = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException("Fixture repository missing");
            Root = Path.Combine(repository, ".bubo/schematic-parts-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "inputs")); Workspace = new(Root);
            File.WriteAllBytes(Workspace.Resolve("inputs/design.json"), JsonSerializer.SerializeToUtf8Bytes(source ?? Source(), StrictDesignJson.Options));
            Design = new(Workspace, "test", "state/task", "staging", new('b', 64), new('c', 64), new('a', 64));
            Design.Initialize("inputs/design.json", "fixture", "v1");
            if (approved)
            {
                var status = View(Design.Preview(Manifest));
                var evidence = new WholeAssemblyReview { SchemaVersion = "bubo-schematic-review/v1", Kind = "whole-assembly-design", Decision = "approved", Human = true,
                    Reviewer = "synthetic-part-fixture", Reason = "Synthetic approval protocol fixture only; no actual human production acceptance.", Asset = "fixture", Version = "v1",
                    DesignSha256 = status["designSha256"]!.GetValue<string>(), DesignFingerprint = status["designFingerprint"]!.GetValue<string>(),
                    ReviewedArtifacts = status["artifacts"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()) };
                Workspace.Save("state/task/review.json", evidence); Design.Record(Manifest, evidence.Kind, evidence.Reviewer, "state/task/review.json");
            }
            Bundle = Workspace.Resolve("resources/schematic-to-model/blender"); Directory.CreateDirectory(Bundle);
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "parts_author.py", "parts_verify.py", "parts_geometry.py", "parts_admission.py" })
            {
                var bytes = Encoding.UTF8.GetBytes("# Simulated native protocol fixture, never an actual Blender resource: " + name + "\n");
                File.WriteAllBytes(Path.Combine(Bundle, name), bytes); files.Add("resources/schematic-to-model/blender/" + name, Digest(bytes));
            }
            Workspace.Save("resources/catalog.json", new { files }); CatalogHash = Workspace.HashProjectFile("resources/catalog.json");
            Tool = Workspace.Resolve("inputs/simulated-blender.exe"); File.WriteAllText(Tool, "simulated existing Blender identity; never executed");
            Process = new(Workspace);
        }
        public JsonObject Build() => View(Session().BuildParts(Manifest, Tool).GetAwaiter().GetResult());
        public JsonObject Status() => View(Session().Status(Manifest));
        public JsonObject State() => JsonNode.Parse(File.ReadAllText(Workspace.Resolve("state/task/schematic-to-model/fixture/v1/parts.json")))!.AsObject();
    }
    sealed class SimulatedProcess(Workspace workspace) : INativeProcess
    {
        public bool MemorySupported = true;
        public bool SupportsPrivateMemoryLimit => MemorySupported;
        public int Launches;
        public List<int> Timeouts { get; } = [];
        public Action<string, PartBuildJob?>? After;
        public string Version = "Blender 4.5.3 LTS";
        public int LogPadding;
        public Task Run(string executable, IEnumerable<string> arguments, string cwd, string log, int seconds, CancellationToken cancellation = default, long maximumLogBytes = 16777216, IReadOnlyDictionary<string, string>? environment = null)
            => throw new InvalidOperationException("Unbounded launches are forbidden in simulated part fixtures");
        public Task RunWithLimits(string executable, IEnumerable<string> arguments, string cwd, string log, int seconds, long maximumPrivateBytes, CancellationToken cancellation = default, long maximumLogBytes = 16777216, IReadOnlyDictionary<string, string>? environment = null)
        {
            cancellation.ThrowIfCancellationRequested(); Check.True(MemorySupported && maximumPrivateBytes >= 67_108_864); Launches++; Timeouts.Add(seconds);
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            var args = arguments.ToArray();
            if (args is ["--version"]) { File.WriteAllText(log, Version + new string(' ',LogPadding)); After?.Invoke("version", null); cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            var index = Array.IndexOf(args, "--"); Check.True(index > 1);
            var script = args[Array.IndexOf(args, "--python") + 1];
            Check.True(script.Contains("/bundle/", StringComparison.Ordinal) || script.Contains("\\bundle\\", StringComparison.Ordinal), "Only the immutable attempt script is launched");
            Check.True(args.Contains("--disable-autoexec") && args.Contains("--offline-mode"));
            var job = StrictDesignJson.Read<PartBuildJob>(args[index + 1]); Check.True(Digest(File.ReadAllBytes(args[index + 1])) == args[index + 2]);
            if (Path.GetFileName(script) == "parts_author.py") Produce(job, args[index + 2]);
            else
            {
                Check.True(Path.GetFileName(script) == "parts_verify.py");
                var report = StrictDesignJson.Read<PartsReport>(Path.Combine(job.Output, "parts-report.json"));
                File.WriteAllBytes(Path.Combine(job.Output, "parts-verification.json"), JsonSerializer.SerializeToUtf8Bytes(report, StrictDesignJson.Options));
            }
            File.WriteAllText(log, "Simulated bounded native protocol; not actual Blender evidence.\n" + new string(' ',LogPadding));
            After?.Invoke(Path.GetFileName(script), job); cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }
        void Produce(PartBuildJob job, string jobHash)
        {
            Directory.CreateDirectory(job.Output); var parts = new List<PartArtifact>(); long total = 0;
            foreach (var definition in job.Design.Parts)
            {
                var size = definition.Size;
                var points = new[] { DesignExamples.V(), DesignExamples.V(size.X), DesignExamples.V(size.X,size.Y), DesignExamples.V(0,size.Y),
                    DesignExamples.V(z:size.Z),DesignExamples.V(size.X,z:size.Z),DesignExamples.V(size.X,size.Y,size.Z),DesignExamples.V(0,size.Y,size.Z) };
                int[] indices = [0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7];
                var binary = Path.Combine(job.Output, definition.Id + ".geometry.bin");
                using (var writer = new BinaryWriter(File.Create(binary)))
                {
                    writer.Write("BUBOPRT1"u8); writer.Write((uint)points.Length); writer.Write((uint)(indices.Length / 3));
                    foreach (var p in points) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
                    foreach (var i in indices) writer.Write((uint)i);
                }
                var blend = Path.Combine(job.Output, definition.Id + ".blend"); File.WriteAllText(blend, "SIMULATED EDITABLE PART PROTOCOL FIXTURE " + definition.Id);
                total += new FileInfo(blend).Length + new FileInfo(binary).Length;
                var material = job.Design.Materials.Single(m => m.Id == definition.MaterialId);
                double Linear(int i) { var s = Convert.ToInt32(material.Color.Substring(i,2),16) / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s+.055)/1.055,2.4); }
                parts.Add(new() { Id = definition.Id, DefinitionSha256 = job.PartIdentities[definition.Id], MaterialId = definition.MaterialId,
                    BlendFile = definition.Id + ".blend", BlendSha256 = workspace.HashFile(blend), GeometryFile = definition.Id + ".geometry.bin", GeometrySha256 = workspace.HashFile(binary),
                    VertexCount = 8, TriangleCount = 12, Minimum = Vec3.Zero, Maximum = size, Volume = size.X * size.Y * size.Z,
                    SurfaceArea = 2*(size.X*size.Y+size.X*size.Z+size.Y*size.Z), Components = 1, Manifold = true,
                    MaterialColorLinear = DesignExamples.V(Linear(1),Linear(3),Linear(5)), MaterialRoughness = material.Roughness, MaterialMetallic = material.Metallic });
            }
            var report = new PartsReport { SchemaVersion = "bubo-schematic-parts/v1", JobSha256 = jobHash, Identity = job.Identity, BlenderVersion = "4.5.3 LTS", Parts = parts,
                Instances = job.Design.Instances, WeightedTriangles = job.Design.Instances.Count * 12L, TotalBytes = total };
            File.WriteAllBytes(Path.Combine(job.Output, "parts-report.json"), JsonSerializer.SerializeToUtf8Bytes(report, StrictDesignJson.Options));
        }
    }
    static void RewriteReports(PartBuildJob job, Func<PartsReport, PartsReport> change)
    {
        foreach (var name in new[] { "parts-report.json", "parts-verification.json" })
        {
            var path = Path.Combine(job.Output, name); if (!File.Exists(path)) continue;
            var report = change(StrictDesignJson.Read<PartsReport>(path)); File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(report, StrictDesignJson.Options));
        }
    }
    public static void Run()
    {
        Check.Case("parts: native admission preserves definitions, occurrences and hard resolution budgets", () =>
        {
            var source = Source(); var readiness = new PartConstructionReadiness(); Check.True(readiness.Validate(source).IsReady);
            var identities = PartOutputVerifier.PartIdentities(source);
            Check.True(identities.Count == 2 && identities["block-definition"] != identities["secondary-definition"]);
            var material = source.Materials[0]; var altered = source with { Materials = [material with { Roughness = .7 }] };
            Check.True(PartOutputVerifier.PartIdentities(altered)["block-definition"] != identities["block-definition"], "Resolved material binds each definition identity");
            Check.True(!readiness.Validate(source with { Instances=source.Instances.Take(2).ToList() }).IsReady, "An uninstantiated definition cannot bypass declaration inventory validation");
            Check.True(!readiness.Validate(source with { Budgets = source.Budgets with { MaxGeneratedTriangles = 23 } }).IsReady, "All definitions count against raw triangles");
            var repeated = source with { Instances = [..source.Instances, DesignExamples.Instance("third", "block-definition", x:6)] };
            Check.True(!readiness.Validate(repeated with { Budgets = source.Budgets with { MaxGeneratedTriangles = 37 } }).IsReady, "All occurrences count against weighted triangles");
        });
        Check.Case("parts: controlled width-scaled vessel loft and curved or closed circular sweeps pass readiness", () =>
        {
            var readiness = new PartConstructionReadiness();
            foreach (var design in new[] { DesignExamples.Vehicle(), DesignExamples.Device(), DesignExamples.Vessel() })
            {
                var report = readiness.Validate(design); Check.True(report.IsReady,string.Join("; ",report.Issues));
            }
            AssemblyDesign Sweep(List<Vec3> points)
            {
                var feature = new ShapeFeature { Id="tube",Kind=FeatureKind.Sweep,Operation=FeatureOperation.Add,Frame=DesignExamples.F(.1,.1,.1),
                    Parameters=new(){{"radius",.05},{"segments",16}},Profile=[],Path=points,Sections=[] };
                return DesignExamples.Base("sweep-readiness",[DesignExamples.Part("tube-definition",DesignExamples.V(1.2,1.2,.2),feature)],
                    [DesignExamples.Instance("tube-instance","tube-definition")]);
            }
            var open = Sweep([DesignExamples.V(),DesignExamples.V(1),DesignExamples.V(1,1)]); Check.True(readiness.Validate(open).IsReady);
            var closed = Sweep([DesignExamples.V(),DesignExamples.V(1),DesignExamples.V(1,1),DesignExamples.V(0,1),DesignExamples.V()]); Check.True(readiness.Validate(closed).IsReady);
            var sharp = Sweep([DesignExamples.V(),DesignExamples.V(1),DesignExamples.V(.1,.1)]);
            Check.True(readiness.Validate(sharp).Issues.Any(i=>i.Code=="part-sweep-profile"));
            var overlap = Sweep([DesignExamples.V(),DesignExamples.V(1),DesignExamples.V(1,1),DesignExamples.V(0,1)]);
            overlap = overlap with {Parts=[overlap.Parts[0] with {Size=DesignExamples.V(2.2,2.2,1.2),Features=[overlap.Parts[0].Features[0] with {Frame=DesignExamples.F(.6,.6,.6),Parameters=new(){{"radius",.55},{"segments",16}}}]}]};
            Check.True(readiness.Validate(overlap).Issues.Any(i=>i.Code=="part-sweep-profile"),"Nearby U-shaped tubes cannot be exempted by short arclength");
            var vessel = DesignExamples.Vessel(); var hull = vessel.Parts[0]; var loft = hull.Features[0];
            var sheared = loft.Sections.Select((s,i) => i==2 ? s with {Profile=s.Profile.Select(p => p with {X=p.X+.02*p.Y}).ToList()} : s).ToList();
            var unsupported = vessel with {Parts=[hull with {Size=hull.Size with {X=13},Features=[loft with {Sections=sheared}]},..vessel.Parts.Skip(1)]};
            Check.True(readiness.Validate(unsupported).Issues.Any(i=>i.Code=="part-loft-profile"));
        });
        Check.Case("parts simulated: prerequisites block before native execution without fallback", () =>
        {
            var unapproved = new Fixture(approved:false); Check.Throws<InvalidOperationException>(() => unapproved.Build()); Check.True(unapproved.Process.Launches == 0);
            var f = new Fixture(); Check.Throws<InvalidOperationException>(() => f.Session(memory:null).BuildParts(Manifest,f.Tool).GetAwaiter().GetResult());
            f.Process.MemorySupported = false; Check.Throws<PlatformNotSupportedException>(() => f.Build()); Check.True(f.Process.Launches == 0);
            var doctor = View(f.Session().Doctor(Manifest,f.Tool).GetAwaiter().GetResult()); Check.True(doctor["nativeReadiness"]!.GetValue<string>() == "unverified");
        });
        Check.Case("parts simulated: independent prototypes retain exact occurrence map and no later acceptance", () =>
        {
            var f = new Fixture(); var status = f.Build(); Check.True(status["currentBuildValid"]!.GetValue<bool>() && status["independentReimport"]!.GetValue<bool>());
            Check.True(status["definitions"]!.GetValue<int>() == 2 && status["physicalOccurrences"]!.GetValue<int>() == 3);
            foreach (var key in new[] {"assemblyAvailable","motionProofAvailable","modelExportAvailable","humanModelAccepted","runtimeAcceptance"}) Check.True(!status[key]!.GetValue<bool>());
            Check.True(f.Process.Launches == 3 && f.Status()["currentBuildValid"]!.GetValue<bool>());
            Check.True(f.Process.Timeouts[0] <= 30 && f.Process.Timeouts[1] >= f.Process.Timeouts[2] && f.Process.Timeouts.All(t => t <= 60));
        });
        Check.Case("parts simulated: canonical asset lock spans construction and design evidence", () =>
        {
            var f = new Fixture();
            f.Process.After = (stage, _) => { if (stage == "version") Check.Throws<InvalidOperationException>(() => f.Design.Status(Manifest)); };
            Check.True(f.Build()["currentBuildValid"]!.GetValue<bool>());
        });
        Check.Case("parts simulated: failed latest attempt retains older attempt without success fallback", () =>
        {
            var f = new Fixture(); f.Build(); f.Process.Version = "Blender 4.4.0"; Check.Throws<InvalidOperationException>(() => f.Build());
            var state = f.State(); Check.True(state["attempts"]!.AsArray().Count == 2);
            Check.True(f.Status()["lastStatus"]!.GetValue<string>() == "failed" && !f.Status()["currentBuildValid"]!.GetValue<bool>());
        });
        Check.Case("parts simulated: independent report disagreement and forged topology fail native verification", () =>
        {
            var f = new Fixture(); f.Process.After = (stage, job) =>
            {
                if (stage != "parts_verify.py") return;
                var path = Path.Combine(job!.Output,"parts-verification.json"); var report = StrictDesignJson.Read<PartsReport>(path);
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(report with { WeightedTriangles=report.WeightedTriangles+1 },StrictDesignJson.Options));
            };
            Check.Throws<InvalidOperationException>(() => f.Build()); Check.True(f.Status()["lastStatus"]!.GetValue<string>() == "failed");
            var g = new Fixture(); g.Process.After = (stage, job) =>
            {
                if (stage != "parts_verify.py") return;
                var path = Path.Combine(job!.Output, "block-definition.geometry.bin"); var bytes = File.ReadAllBytes(path);
                Array.Copy(bytes, 16+8*24, bytes,16+8*24+4,4); File.WriteAllBytes(path,bytes);
                RewriteReports(job, report => report with { Parts=report.Parts.Select(p => p.Id == "block-definition" ? p with { GeometrySha256=Digest(bytes) } : p).ToList() });
            };
            Check.Throws<InvalidOperationException>(() => g.Build()); Check.True(g.Status()["lastStatus"]!.GetValue<string>() == "failed");
        });
        Check.Case("parts simulated: extra inventory, report payload tampering and occurrence remapping fail", () =>
        {
            foreach (var mode in new[] {"extra","bytes","instances"})
            {
                var f = new Fixture(); f.Process.After = (stage, job) =>
                {
                    if (stage != "parts_verify.py") return;
                    if (mode == "extra") File.WriteAllText(Path.Combine(job!.Output,"extra.blend"),"unregistered");
                    else RewriteReports(job!, report => mode == "bytes" ? report with {TotalBytes=report.TotalBytes+1} : report with {Instances=report.Instances.Take(1).ToList()});
                };
                Check.Throws<InvalidOperationException>(() => f.Build());
            }
        });
        Check.Case("parts simulated: source, selected tool, copied bundle and audited bundle mutations retain failed history", () =>
        {
            foreach (var mode in new[] {"source","tool","copy","bundle"})
            {
                var f = new Fixture(); f.Process.After = (stage, job) =>
                {
                    if (stage != "parts_author.py") return;
                    var path = mode switch { "source" => f.Workspace.Resolve("inputs/design.json"), "tool" => f.Tool,
                        "bundle" => Path.Combine(f.Bundle,"parts_geometry.py"), _ => Path.Combine(Directory.GetParent(job!.Output)!.FullName,"bundle/parts_geometry.py") };
                    File.AppendAllText(path," ");
                };
                Check.Throws<InvalidOperationException>(() => f.Build()); Check.True(f.State()["attempts"]![0]!["status"]!.GetValue<string>() == "failed");
            }
        });
        Check.Case("parts simulated: cancellation retires into a preserved failed attempt", () =>
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource();
            f.Process.After = (stage, _) => { if (stage == "parts_author.py") cancel.Cancel(); };
            Check.Throws<OperationCanceledException>(() => f.Session().BuildParts(Manifest,f.Tool,cancel.Token).GetAwaiter().GetResult());
            Check.True(f.Status()["lastStatus"]!.GetValue<string>() == "failed" && f.State()["attempts"]!.AsArray().Count == 1);
        });
        Check.Case("parts simulated: native binary bounds and material values are measured despite matching forged reports", () =>
        {
            foreach (var mode in new[] {"bounds","material","count"})
            {
                var f = new Fixture(); f.Process.After = (stage,job) =>
                {
                    if (stage != "parts_verify.py") return;
                    if (mode == "material") RewriteReports(job!, report => report with { Parts=report.Parts.Select(p => p with { MaterialRoughness=.99 }).ToList() });
                    else
                    {
                        var path = Path.Combine(job!.Output,"block-definition.geometry.bin"); var bytes = File.ReadAllBytes(path);
                        if (mode == "bounds") BitConverter.GetBytes(2d).CopyTo(bytes,16);
                        else BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes,8);
                        File.WriteAllBytes(path,bytes);
                        RewriteReports(job, report => report with { Parts=report.Parts.Select(p => p.Id == "block-definition" ? p with { GeometrySha256=Digest(bytes) } : p).ToList() });
                    }
                };
                Check.Throws<InvalidOperationException>(() => f.Build());
            }
        });
        Check.Case("parts simulated: cumulative process deadline and log budget do not restart between native stages", () =>
        {
            var f = new Fixture(); f.Process.LogPadding = 800;
            Check.Throws<InvalidOperationException>(() => f.Session(logs:2048).BuildParts(Manifest,f.Tool).GetAwaiter().GetResult());
            Check.True(f.Process.Launches == 2 && f.Status()["lastStatus"]!.GetValue<string>() == "failed");
            var g = new Fixture();
            g.Process.After = (stage,_) => { if (stage is "version" or "parts_author.py") Thread.Sleep(600); };
            var timeout = Check.Throws<Exception>(() => g.Session(timeout:1).BuildParts(Manifest,g.Tool).GetAwaiter().GetResult());
            Check.True(timeout is OperationCanceledException or TimeoutException);
            Check.True(g.Process.Launches == 2 && g.Status()["lastStatus"]!.GetValue<string>() == "failed");
        });
        Check.Case("parts simulated: paired opposite edges cannot hide a nonmanifold pinched vertex", () =>
        {
            var source = Source(); source = source with {Parts=[source.Parts[0] with {Size=DesignExamples.V(2,2,2),Features=[DesignExamples.Box(2,2,2)]},..source.Parts.Skip(1)]};
            var f = new Fixture(source:source); f.Process.After = (stage,job) =>
            {
                if (stage != "parts_verify.py") return;
                var path = Path.Combine(job!.Output,"block-definition.geometry.bin"); var before = new FileInfo(path).Length;
                Vec3[] cube = [DesignExamples.V(),DesignExamples.V(1),DesignExamples.V(1,1),DesignExamples.V(0,1),DesignExamples.V(z:1),DesignExamples.V(1,z:1),DesignExamples.V(1,1,1),DesignExamples.V(0,1,1)];
                int[] triangles = [0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7];
                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    writer.Write("BUBOPRT1"u8); writer.Write(15u); writer.Write(24u);
                    foreach(var point in cube) { writer.Write(point.X);writer.Write(point.Y);writer.Write(point.Z); }
                    foreach(var point in cube.Skip(1)) {writer.Write(point.X+1);writer.Write(point.Y+1);writer.Write(point.Z+1);}
                    foreach(var index in triangles) writer.Write((uint)index);
                    foreach(var index in triangles) writer.Write((uint)(index==0?6:index+7));
                }
                var added = new FileInfo(path).Length - before; var hash = Digest(File.ReadAllBytes(path));
                RewriteReports(job, report => report with {TotalBytes=report.TotalBytes+added,WeightedTriangles=60,
                    Parts=report.Parts.Select(p=>p.Id=="block-definition"? p with {GeometrySha256=hash,VertexCount=15,TriangleCount=24,Volume=2,SurfaceArea=12}:p).ToList()});
            };
            var error = Check.Throws<InvalidOperationException>(()=>f.Build()); Check.True(error.Message.Contains("vertex link",StringComparison.Ordinal));
        });
    }
}
