using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.FloorPlan.Domain;
using Bubo.FloorPlan.Infrastructure;
using Hephaera.AssetProduction;
namespace Bubo.FloorPlan.Tests;

// These fixtures exercise native admission/failure lifecycle with a deliberately failing process port.
// They cannot produce a real building, Blender result, human approval or runtime acceptance.
public static class ConstructionSessionTests
{
    sealed class ProcessFixture : INativeProcess
    {
        public bool SupportsPrivateMemoryLimit { get; set; } = true;
        public int Calls { get; private set; }
        public Action? DuringRun { get; set; }
        public bool Fail { get; set; } = true;
        public bool SyntheticAuthor { get; set; }
        public Task Run(string executable, IEnumerable<string> arguments, string cwd, string log, int seconds, CancellationToken cancellation = default, long maximumLogBytes = 16777216, IReadOnlyDictionary<string, string>? environment = null)
            => throw new InvalidOperationException("Unbounded native execution must never be selected");
        public Task RunWithLimits(string executable, IEnumerable<string> arguments, string cwd, string log, int seconds, long maximumPrivateBytes, CancellationToken cancellation = default, long maximumLogBytes = 16777216, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls++; Check.True(maximumPrivateBytes == 1_073_741_824 && seconds == 30);
            File.WriteAllText(log, "Blender 4.5.10 LTS\nSIMULATED process-port fixture; no Blender launched.\n");
            if (SyntheticAuthor) WriteSyntheticReport(arguments.ToArray()[^2]);
            DuringRun?.Invoke(); cancellation.ThrowIfCancellationRequested();
            if (Fail && !SyntheticAuthor) throw new InvalidOperationException("Controlled construction process failure");
            return Task.CompletedTask;
        }
        static void WriteSyntheticReport(string jobPath)
        {
            var job = StrictPlanJson.Read<ConstructionBlenderJob>(jobPath); var scene = job.Scene;
            Directory.CreateDirectory(job.OutputDirectory);
            var blend = Path.Combine(job.OutputDirectory, "building.blend");
            File.WriteAllText(blend, "SIMULATED protocol payload. Not a Blender file or model-quality proof.\n");
            static int Components(ConstructionMesh mesh)
            {
                var parent = Enumerable.Range(0, mesh.Vertices.Count).ToArray();
                int Find(int a) { while (parent[a] != a) a = parent[a]; return a; }
                for (var i = 0; i < mesh.Triangles.Count; i += 3) { parent[Find(mesh.Triangles[i + 1])] = Find(mesh.Triangles[i]); parent[Find(mesh.Triangles[i + 2])] = Find(mesh.Triangles[i]); }
                return Enumerable.Range(0, parent.Length).Select(Find).Distinct().Count();
            }
            var report = new ConstructionReport
            {
                SchemaVersion = "bubo-floor-plan-construction-report/v1", JobSha256 = Hephaera.AssetProduction.Workspace.Hash(jobPath), Bundle = job.Bundle.ToDictionary(), Identities = job.Identities, BlenderVersion = "4.5.10 LTS",
                Blend = new("building.blend", Hephaera.AssetProduction.Workspace.Hash(blend), new FileInfo(blend).Length),
                Counts = new(scene.Meshes.Count, scene.Anchors.Count, scene.Materials.Count, scene.Metrics.VertexCount, scene.Metrics.TriangleCount, scene.Metrics.VisualTriangleCount, scene.Metrics.CollisionTriangleCount),
                Minimum = scene.Metrics.Minimum, Maximum = scene.Metrics.Maximum, MaxWorldCoordinateError = 0,
                Meshes = scene.Meshes.Select(m => new ConstructionMeshReport
                {
                    Id = m.Id, SemanticId = m.SemanticId, StoreyId = m.StoreyId, Kind = m.Kind, Representation = m.Representation, CollisionRole = m.CollisionRole, MaterialId = m.MaterialId, ParentAnchorId = m.ParentAnchorId,
                    VertexCount = m.Vertices.Count, TriangleCount = m.Triangles.Count / 3,
                    Minimum = new(m.Vertices.Min(p => p.X), m.Vertices.Min(p => p.Y), m.Vertices.Min(p => p.Z)), Maximum = new(m.Vertices.Max(p => p.X), m.Vertices.Max(p => p.Y), m.Vertices.Max(p => p.Z)), MaxWorldCoordinateError = 0,
                    GeometrySha256 = ConstructionOutputVerifier.SourceGeometryHash(m), SourceGeometrySha256 = ConstructionOutputVerifier.SourceGeometryHash(m), IndexSha256 = ConstructionOutputVerifier.IndexHash(m), MaterialAssignmentsSha256 = ConstructionOutputVerifier.MaterialHash(m),
                    MaterialTriangleCounts = Enumerable.Range(0, m.Triangles.Count / 3).Select(i => m.TriangleMaterialIds?[i] ?? m.MaterialId).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count()),
                    ComponentCount = Components(m), MinimumComponentVolume = .001, Manifold = true
                }).ToList(), Anchors = scene.Anchors, Materials = scene.Materials, IndependentReimport = false, HumanModelAccepted = false, RuntimeAccepted = false
            };
            File.WriteAllBytes(Path.Combine(job.OutputDirectory, "construction-report.json"), JsonSerializer.SerializeToUtf8Bytes(report, StrictPlanJson.Options));
        }
    }
    sealed class HookWorkspace(string root) : IWorkspace
    {
        readonly Workspace inner = new(root);
        public Action<string>? AfterResolve { get; set; }
        public Action<string>? AfterHash { get; set; }
        public string Root => inner.Root;
        public string Resolve(string path) { var result = inner.Resolve(path); AfterResolve?.Invoke(path); return result; }
        public string ResolveWrite(string path) => inner.ResolveWrite(path);
        public string Relative(string path) => inner.Relative(path);
        public string HashFile(string path) { var result = inner.HashFile(path); AfterHash?.Invoke(path); return result; }
        public void Save<T>(string path, T value) => inner.Save(path, value);
        public T Load<T>(string path) => inner.Load<T>(path);
        public FileStream Lock(string path) => inner.Lock(path);
    }
    sealed class Fixture
    {
        public readonly string Root, Repository, BundleDirectory, Catalog, Manifest = "state/floor-plan-to-model/house/v1/manifest.json";
        public readonly HookWorkspace Workspace;
        public readonly DesignSession Design;
        public readonly ProcessFixture Process = new();
        public Fixture(BuildingPlan? plan = null)
        {
            var repo = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repo, "Bubo.slnx"))) repo = Directory.GetParent(repo)?.FullName ?? throw new InvalidOperationException("Fixture repository missing");
            Repository = repo; Root = Path.Combine(repo, ".bubo", "construction-lifecycle-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(Root, "inputs"));
            Workspace = new(Root);
            BundleDirectory = Path.Combine(Root, "runtime/resources/floor-plan-to-model/blender"); Directory.CreateDirectory(BundleDirectory);
            var scripts = new Dictionary<string, string>();
            foreach (var name in new[] { "author.py", "admission.py" })
            {
                var target = Path.Combine(BundleDirectory, name); File.Copy(Path.Combine(Repository, "resources/floor-plan-to-model/blender", name), target);
                scripts.Add("resources/floor-plan-to-model/blender/" + name, Hephaera.AssetProduction.Workspace.Hash(target));
            }
            Catalog = Path.Combine(Root, "runtime/resources/catalog.json"); File.WriteAllBytes(Catalog, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, files = scripts }, StrictPlanJson.Options));
            File.WriteAllBytes(Path.Combine(Root, "inputs/plan.json"), JsonSerializer.SerializeToUtf8Bytes(plan ?? ConstructionPlanExamples.SingleStorey(), StrictPlanJson.Options));
            File.WriteAllText(Path.Combine(Root, "inputs/fake-tool.txt"), "Synthetic process port identity only. Never executed.\n");
            Design = new(Workspace, "synthetic-construction-tests", "state", "staging", new('b', 64), new('c', 64), new('a', 64));
            Design.Initialize("inputs/plan.json", "house", "v1");
        }
        public ConstructionSession Session(long? memory = 1_073_741_824) => new(Workspace, Design, Process, BundleDirectory, 30, 1_048_576, memory, Hephaera.AssetProduction.Workspace.Hash(Catalog));
        public void Approve()
        {
            var preview = JsonSerializer.SerializeToNode(Design.Preview(Manifest), StrictPlanJson.Options)!.AsObject();
            var review = new WholeBuildingReview { SchemaVersion = "bubo-floor-plan-review/v1", Kind = "whole-building-design", Decision = "approved", Human = true, Reviewer = "synthetic-construction-tests", Reason = "Synthetic protocol assertion only; not actual human architectural/model acceptance.", Asset = "house", Version = "v1", PlanSha256 = preview["planSha256"]!.GetValue<string>(), DesignFingerprint = preview["designFingerprint"]!.GetValue<string>(), ReviewedArtifacts = preview["drawings"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()) };
            Directory.CreateDirectory(Path.Combine(Root, "state")); File.WriteAllBytes(Path.Combine(Root, "state/review.json"), JsonSerializer.SerializeToUtf8Bytes(review, StrictPlanJson.Options));
            Design.Record(Manifest, review.Kind, review.Reviewer, "state/review.json");
        }
        public void Build(ConstructionSession? session = null, CancellationToken cancellation = default) => (session ?? Session()).Build(Manifest, Path.Combine(Root, "inputs/fake-tool.txt"), cancellation).GetAwaiter().GetResult();
        public ConstructionState State() => StrictPlanJson.Read<ConstructionState>(Path.Combine(Root, "state/floor-plan-to-model/house/v1/construction.json"));
    }
    public static void Run()
    {
        Check.Case("construction: approved legacy v1 never becomes build-ready", () =>
        {
            var f = new Fixture(PlanExamples.SingleStorey()); f.Approve(); var error = Check.Throws<PlanValidationException>(() => f.Build());
            Check.True(error.Report.Issues.Any(i => i.Code == "construction-schema-required") && f.Process.Calls == 0);
        });
        Check.Case("construction: v2 source without whole-package approval cannot start", () =>
        {
            var f = new Fixture(); Check.Throws<InvalidOperationException>(() => f.Build());
            Check.True(f.Process.Calls == 0 && !File.Exists(Path.Combine(f.Root, "state/floor-plan-to-model/house/v1/construction.json")));
        });
        Check.Case("construction: complete v2 approval includes explicit detail sheet", () =>
        {
            var f = new Fixture(); f.Approve(); var status = JsonSerializer.SerializeToNode(f.Design.Status(f.Manifest), StrictPlanJson.Options)!;
            Check.True(status["drawings"]!.AsObject().ContainsKey("construction-details.svg") && status["wholeBuildingApproved"]!.GetValue<bool>() && status["modelingAvailable"]!.GetValue<bool>() && !status["modelExportAvailable"]!.GetValue<bool>());
        });
        Check.Case("construction: valid thick roof remains visible in required detail drawing", () =>
        {
            var original = ConstructionPlanExamples.SingleStorey(); var plan = original with { Construction = original.Construction! with { RoofThickness = 2 } };
            var svg = System.Xml.Linq.XDocument.Parse(new SvgPlanRenderer().Render(plan)["construction-details.svg"]);
            foreach (var line in svg.Descendants().Where(n => n.Name.LocalName == "line"))
                foreach (var name in new[] { "y1", "y2" }) Check.True(double.Parse(line.Attribute(name)!.Value, System.Globalization.CultureInfo.InvariantCulture) >= 0);
        });
        Check.Case("construction: no implicit native memory budget", () =>
        {
            var f = new Fixture(); f.Approve(); Check.Throws<InvalidOperationException>(() => f.Build(f.Session(null))); Check.True(f.Process.Calls == 0);
        });
        Check.Case("construction: unsupported memory enforcement cannot fall back", () =>
        {
            var f = new Fixture(); f.Approve(); f.Process.SupportsPrivateMemoryLimit = false; Check.Throws<PlatformNotSupportedException>(() => f.Build()); Check.True(f.Process.Calls == 0);
        });
        Check.Case("SIMULATED construction: failed native attempt is retained without current model", () =>
        {
            var f = new Fixture(); f.Approve(); var error = Check.Throws<InvalidOperationException>(() => f.Build()); Check.True(error.Message.Contains("Controlled construction process failure"));
            var state = f.State(); Check.True(f.Process.Calls == 1 && state.Attempts.Count == 1 && state.Attempts[0].Status == "failed" && state.Attempts[0].JobSha256 is not null);
            Check.True(!JsonSerializer.SerializeToNode(f.Session().Status(f.Manifest), StrictPlanJson.Options)!["currentBuildValid"]!.GetValue<bool>());
        });
        Check.Case("SIMULATED construction: cancellation preserves the started failed attempt", () =>
        {
            var f = new Fixture(); f.Approve(); using var cancellation = new CancellationTokenSource(); f.Process.DuringRun = cancellation.Cancel;
            Check.Throws<OperationCanceledException>(() => f.Build(cancellation: cancellation.Token));
            Check.True(f.State().Attempts.Single().Status == "failed" && f.Process.Calls == 1);
        });
        Check.Case("construction: pre-cancelled work creates no attempt", () =>
        {
            var f = new Fixture(); f.Approve(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check.Throws<OperationCanceledException>(() => f.Build(cancellation: cancellation.Token)); Check.True(f.Process.Calls == 0);
        });
        Check.Case("construction: existing asset ownership rejects another build", () =>
        {
            var f = new Fixture(); f.Approve(); using var ownership = f.Workspace.Lock("state/floor-plan-to-model/house/v1/asset.lock");
            Check.Throws<InvalidOperationException>(() => f.Build()); Check.True(f.Process.Calls == 0);
        });
        Check.Case("SIMULATED construction: doctor cannot verify a missing audited bundle", () =>
        {
            var f = new Fixture(); f.Approve(); f.Process.Fail = false; File.Delete(Path.Combine(f.BundleDirectory, "author.py"));
            var result = f.Session().Doctor(f.Manifest, Path.Combine(f.Root, "inputs/fake-tool.txt")).GetAwaiter().GetResult();
            Check.True(JsonSerializer.SerializeToNode(result, StrictPlanJson.Options)!["nativeReadiness"]!.GetValue<string>() == "unverified");
        });
        Check.Case("SIMULATED construction: bundle mutation during version probe cannot retain verified readiness", () =>
        {
            var f = new Fixture(); f.Approve(); f.Process.Fail = false; f.Process.DuringRun = () => File.AppendAllText(Path.Combine(f.BundleDirectory, "author.py"), "\n# changed synthetic fixture\n");
            var result = f.Session().Doctor(f.Manifest, Path.Combine(f.Root, "inputs/fake-tool.txt")).GetAwaiter().GetResult();
            Check.True(JsonSerializer.SerializeToNode(result, StrictPlanJson.Options)!["nativeReadiness"]!.GetValue<string>() == "unverified");
        });
        Check.Case("SIMULATED construction: late job mutation cannot complete an attempt", () =>
        {
            var f = new Fixture(); f.Approve(); f.Process.SyntheticAuthor = true; var hashes = 0;
            f.Process.DuringRun = () => f.Workspace.AfterHash = path =>
            {
                if (Path.GetFileName(path) == "author.py" && ++hashes == 2)
                {
                    var attempt = f.State().Attempts[^1]; File.AppendAllText(Path.Combine(f.Root, attempt.Directory, "job.json"), "\n");
                }
            };
            var error = Check.Throws<Exception>(() => f.Build());
            Check.True(error is IOException or InvalidOperationException); Check.True(f.State().Attempts.Single().Status == "failed");
        });
        Check.Case("SIMULATED construction: late verification cancellation cannot return a current model", () =>
        {
            var f = new Fixture(); f.Approve(); f.Process.SyntheticAuthor = true; using var cancellation = new CancellationTokenSource(); var hashes = 0;
            f.Process.DuringRun = () => f.Workspace.AfterHash = path => { if (Path.GetFileName(path) == "author.py" && ++hashes == 2) cancellation.Cancel(); };
            Check.Throws<OperationCanceledException>(() => f.Build(cancellation: cancellation.Token)); Check.True(f.State().Attempts.Single().Status == "failed");
        });
        Check.Case("SIMULATED construction: status refreshes approval after artifact reads", () =>
        {
            var f = new Fixture(); f.Approve(); f.Process.SyntheticAuthor = true; f.Build(); var changed = false;
            f.Workspace.AfterResolve = path =>
            {
                if (!changed && path.EndsWith("/building.blend", StringComparison.Ordinal)) { changed = true; File.AppendAllText(Path.Combine(f.Root, "state/review.json"), "\n"); }
            };
            Check.Throws<InvalidOperationException>(() => f.Session().Status(f.Manifest)); Check.True(changed);
        });
        Check.Case("SIMULATED construction: status rejects an extra output entry", () =>
        {
            var f = new Fixture(); f.Approve(); f.Process.SyntheticAuthor = true; f.Build(); var attempt = f.State().Attempts.Single();
            File.WriteAllText(Path.Combine(f.Root, attempt.Directory, "output/unreviewed.txt"), "unexpected");
            var error = Check.Throws<InvalidOperationException>(() => f.Session().Status(f.Manifest)); Check.True(error.Message.Contains("unexpected or missing output file"));
        });
        Check.Case("SIMULATED construction: status checks byte ceiling before hashing oversized output", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); plan = plan with { Budgets = plan.Budgets with { MaxExportBytes = 1_048_576 }, Construction = plan.Construction! with { MaxJobBytes = 1_048_576 } };
            var f = new Fixture(plan); f.Approve(); f.Process.SyntheticAuthor = true; f.Build(); var attempt = f.State().Attempts.Single();
            using (var file = new FileStream(Path.Combine(f.Root, attempt.Directory, "output/building.blend"), FileMode.Open, FileAccess.Write)) file.SetLength(1_048_577);
            var error = Check.Throws<InvalidOperationException>(() => f.Session().Status(f.Manifest)); Check.True(error.Message.Contains("approved byte budget"));
        });
    }
}
