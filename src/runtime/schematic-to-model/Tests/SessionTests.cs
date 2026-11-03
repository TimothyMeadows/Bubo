using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;
using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Tests;

public static class SessionTests
{
    const string Manifest = "state/task/schematic-to-model/device/v1/manifest.json";
    static readonly string Policy = new('b', 64), Runtime = new('c', 64), Style = new('a', 64);
    static JsonObject View(object value) => JsonSerializer.SerializeToNode(value, StrictDesignJson.Options)!.AsObject();
    sealed class Fixture
    {
        public string Root { get; }
        public Workspace Workspace { get; }
        public DesignSession Session { get; }
        public Fixture(ISchematicPreviewRenderer? renderer = null)
        {
            var repository = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repository, "Bubo.slnx"))) repository = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException("Fixture repository missing");
            Root = Path.Combine(repository, ".bubo/schematic-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "inputs"));
            Workspace = new(Root); Write(DesignExamples.Device());
            Session = new(Workspace, "test", "state/task", "staging", Policy, Runtime, Style, renderer: renderer);
        }
        public void Write(AssemblyDesign design) => File.WriteAllBytes(Workspace.Resolve("inputs/design.json"), JsonSerializer.SerializeToUtf8Bytes(design, StrictDesignJson.Options));
        public JsonObject Init() => View(Session.Initialize("inputs/design.json", "device", "v1"));
        public JsonObject Preview() => View(Session.Preview(Manifest));
        public WholeAssemblyReview Review(JsonObject status) => new()
        {
            SchemaVersion = "bubo-schematic-review/v1", Kind = "whole-assembly-design", Decision = "approved", Human = true,
            Reviewer = "synthetic-fixture", Reason = "Synthetic protocol evidence only; not human production acceptance.", Asset = "device", Version = "v1",
            DesignSha256 = status["designSha256"]!.GetValue<string>(), DesignFingerprint = status["designFingerprint"]!.GetValue<string>(),
            ReviewedArtifacts = status["artifacts"]!.AsObject().ToDictionary(x => x.Key, x => x.Value!.GetValue<string>())
        };
        public void Evidence(WholeAssemblyReview review)
        { Directory.CreateDirectory(Workspace.ResolveWrite("state/task")); File.WriteAllBytes(Workspace.ResolveWrite("state/task/review.json"), JsonSerializer.SerializeToUtf8Bytes(review, StrictDesignJson.Options)); }
        public JsonObject Record() => View(Session.Record(Manifest, "whole-assembly-design", "synthetic-fixture", "state/task/review.json"));
    }
    sealed class FailingRenderer(CancellationTokenSource? cancel = null) : ISchematicPreviewRenderer
    {
        public IReadOnlyDictionary<string, string> Render(AssemblyDesign design, CancellationToken cancellation = default)
        { cancel?.Cancel(); cancellation.ThrowIfCancellationRequested(); throw new InvalidOperationException("Controlled renderer failure"); }
    }
    sealed class SwapWorkspace(Workspace inner, string target, Action swap, Action? restore = null) : IWorkspace
    {
        int reads;
        public string Root => inner.Root;
        public HostExecutionContext? HostContext => inner.HostContext;
        public string Resolve(string path)
        {
            if (path == target)
            {
                reads++;
                if (reads == (restore is null ? 1 : 2)) swap();
                else if (reads == 3) restore?.Invoke();
            }
            return inner.Resolve(path);
        }
        public string ResolveWrite(string path) => inner.ResolveWrite(path);
        public string Relative(string path) => inner.Relative(path);
        public string HashFile(string path) => inner.HashFile(path);
        public string HashProjectFile(string path) => inner.HashProjectFile(path);
        public void Save<T>(string path, T value) => inner.Save(path, value);
        public T Load<T>(string path) => inner.Load<T>(path);
        public FileStream Lock(string path) => inner.Lock(path);
    }
    static HostExecutionContext Context(Fixture fixture, string? parent = null)
    {
        File.WriteAllText(fixture.Workspace.Resolve("policy.json"), "{\"kind\":\"synthetic-reference-race-fixture\"}");
        var references = new[] { "policy.json", "inputs/design.json" }.Concat(parent is null ? [] : new[] { parent })
            .Select(path => new { path, sha256 = fixture.Workspace.HashProjectFile(path), kind = path == "policy.json" ? "policy" : "input" }).ToArray();
        var value = new
        {
            schemaVersion = "bubo-host-context/v1", projectRoot = fixture.Root, task = "test",
            roots = new { state = "state/task", staging = "staging", tools = new[] { "tools" }, inputs = new[] { "inputs" }, outputs = new[] { "outputs" } },
            policy = new { pipeline = "SCHEMATIC_TO_MODEL", styleSha256 = Style, engine = new { effectiveEngine = "THREEJS" }, libraries = Array.Empty<string>() },
            references, tools = Array.Empty<string>(), budgets = new { timeoutSeconds = 30, maxLogBytes = 1048576 }, permissions = new { promotion = false, provisioning = false }
        };
        File.WriteAllBytes(fixture.Workspace.Resolve("context.json"), JsonSerializer.SerializeToUtf8Bytes(value));
        return HostExecutionContext.Load(fixture.Root, "context.json");
    }
    public static void Run()
    {
        Check.Case("session: source bytes changed after reference admission are rejected", () =>
        {
            var f = new Fixture(); var context = Context(f);
            var workspace = new SwapWorkspace(new(f.Root, context), "inputs/design.json", () => f.Write(DesignExamples.Device() with { Name = "Swapped after admission" }));
            var session = new DesignSession(workspace, "test", "state/task", "staging", HostPolicyIdentity.Fingerprint(context), Runtime, Style);
            var error = Check.Throws<InvalidOperationException>(() => session.Initialize("inputs/design.json", "device", "v1"));
            Check.True(error.Message.Contains("after registered reference admission", StringComparison.Ordinal));
            Check.True(!File.Exists(f.Workspace.Resolve(Manifest)));
        });
        Check.Case("session: parent snapshot bytes must match the admitted parent reference", () =>
        {
            var f = new Fixture(); f.Init(); var context = Context(f, Manifest);
            var workspace = new SwapWorkspace(new(f.Root, context), Manifest, () =>
            {
                var state = JsonNode.Parse(File.ReadAllBytes(f.Workspace.Resolve(Manifest)))!.AsObject(); state["runtimeBuildSha256"] = new string('d',64);
                File.WriteAllBytes(f.Workspace.Resolve(Manifest), JsonSerializer.SerializeToUtf8Bytes(state));
            });
            var session = new DesignSession(workspace, "test", "state/task", "staging", HostPolicyIdentity.Fingerprint(context), Runtime, Style);
            var error = Check.Throws<InvalidOperationException>(() => session.Initialize("inputs/design.json", "device", "v2", Manifest));
            Check.True(error.Message.Contains("after registered reference admission", StringComparison.Ordinal));
            Check.True(!File.Exists(f.Workspace.Resolve(session.ManifestPath("device", "v2"))));
        });
        Check.Case("session: asset-scoped ownership serializes first versions", () =>
        {
            var f = new Fixture();
            using (f.Workspace.Lock("state/task/schematic-to-model/device/asset.lock"))
                Check.Throws<InvalidOperationException>(() => f.Session.Initialize("inputs/design.json", "device", "v2"));
            Check.True(!File.Exists(f.Workspace.Resolve(f.Session.ManifestPath("device", "v2"))));
            f.Init();
        });
        Check.Case("session: split-read review swaps cannot persist inconsistent assertions", () =>
        {
            var f = new Fixture(); f.Init(); var status = f.Preview(); var review = f.Review(status); f.Evidence(review);
            const string evidence = "state/task/review.json";
            var context = Context(f, evidence);
            var workspace = new SwapWorkspace(new(f.Root, context), evidence,
                () => f.Evidence(review with { Decision = "rejected" }), () => f.Evidence(review));
            var session = new DesignSession(workspace, "test", "state/task", "staging", Policy, Runtime, Style);
            Check.Throws<InvalidOperationException>(() => session.Record(Manifest, review.Kind, review.Reviewer, evidence));
            var manifest = StrictDesignJson.Read<DesignManifest>(f.Workspace.Resolve(Manifest));
            Check.True(manifest.Reviews.Count == 0, "A failed review admission cannot persist a decision from different bytes");
        });
        Check.Case("JSON: examples strict roundtrip and canonical enum spelling", () =>
        {
            foreach (var design in new[] { DesignExamples.Vehicle(), DesignExamples.Device(), DesignExamples.Vessel() })
                new AssemblyDesignValidator().RequireValid(StrictDesignJson.Parse<AssemblyDesign>(JsonSerializer.SerializeToUtf8Bytes(design, StrictDesignJson.Options)));
            var raw = JsonSerializer.Serialize(DesignExamples.Device(), StrictDesignJson.Options);
            Check.Throws<JsonException>(() => StrictDesignJson.Parse<AssemblyDesign>(System.Text.Encoding.UTF8.GetBytes(raw.Replace("\"rigid\"", "\"Rigid\""))));
        });
        Check.Case("JSON: audited examples equal validated native fixtures with portable LF bytes", () =>
        {
            var repository = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repository, "Bubo.slnx"))) repository = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException("Fixture repository missing");
            foreach (var (name, design) in new Dictionary<string, AssemblyDesign> { ["vehicle"] = DesignExamples.Vehicle(), ["device"] = DesignExamples.Device(), ["vessel"] = DesignExamples.Vessel() })
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(design, StrictDesignJson.Options);
                Check.True(!bytes.Contains((byte)'\r'));
                Check.True(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(repository, "resources/schematic-to-model/examples", name + ".json"))), "Audited example differs from native fixture: " + name);
            }
        });
        Check.Case("JSON: duplicate, unknown, mis-cased and missing dimension fields rejected", () =>
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(DesignExamples.Device(), StrictDesignJson.Options);
            var node = JsonNode.Parse(bytes)!.AsObject(); node["code"] = "untrusted()";
            Check.Throws<JsonException>(() => StrictDesignJson.Parse<AssemblyDesign>(JsonSerializer.SerializeToUtf8Bytes(node)));
            var raw = System.Text.Encoding.UTF8.GetString(bytes);
            Check.Throws<JsonException>(() => StrictDesignJson.Parse<AssemblyDesign>(System.Text.Encoding.UTF8.GetBytes(raw.Insert(1, "\"id\":\"duplicate\","))));
            Check.Throws<JsonException>(() => StrictDesignJson.Parse<AssemblyDesign>(System.Text.Encoding.UTF8.GetBytes(raw.Replace("\"coordinateSystem\"", "\"CoordinateSystem\""))));
            node = JsonNode.Parse(bytes)!.AsObject(); node["parts"]![0]!["size"]!.AsObject().Remove("x");
            Check.Throws<JsonException>(() => StrictDesignJson.Parse<AssemblyDesign>(JsonSerializer.SerializeToUtf8Bytes(node)));
            Check.Throws<InvalidOperationException>(() => StrictDesignJson.Parse<AssemblyDesign>(new byte[StrictDesignJson.MaximumInputBytes + 1]));
        });
        Check.Case("session: immutable originals and foundation readiness are explicit", () =>
        {
            var f = new Fixture(); var status = f.Init();
            foreach (var key in new[] { "wholeAssemblyApproved", "partConstructionAvailable", "assemblyAvailable", "motionProofAvailable", "modelExportAvailable", "runtimeAcceptance" }) Check.True(!status[key]!.GetValue<bool>());
            Check.True(File.ReadAllBytes(f.Workspace.Resolve("inputs/design.json")).SequenceEqual(File.ReadAllBytes(f.Workspace.Resolve("staging/schematic-to-model/device/v1/original/assembly-design.json"))));
            Check.Throws<InvalidOperationException>(() => f.Init());
        });
        Check.Case("session: complete deterministic previews and exact whole-design review", () =>
        {
            var f = new Fixture(); f.Init(); var first = f.Preview(); var second = f.Preview();
            Check.True(first["designFingerprint"]!.ToString() == second["designFingerprint"]!.ToString());
            Check.True(first["drawingDirectory"]!.ToString() != second["drawingDirectory"]!.ToString());
            f.Evidence(f.Review(second)); var approved = f.Record(); Check.True(approved["wholeAssemblyApproved"]!.GetValue<bool>());
            var retry = f.Record(); Check.True(retry["reviews"]!.GetValue<int>() == 1, "Identical human assertion retries are idempotent");
            Check.True(approved["partConstructionAvailable"]!.GetValue<bool>());
            Check.True(f.Session.Validate(Manifest).IsValid);
        });
        Check.Case("session: partial, nonhuman and wrong-fingerprint approval rejected", () =>
        {
            var f = new Fixture(); f.Init(); var status = f.Preview(); var review = f.Review(status);
            f.Evidence(review with { Human = false }); Check.Throws<InvalidOperationException>(() => f.Record());
            f.Evidence(review with { DesignFingerprint = new string('d', 64) }); Check.Throws<InvalidOperationException>(() => f.Record());
            var incomplete = review.ReviewedArtifacts.ToDictionary(x => x.Key, x => x.Value); incomplete.Remove(incomplete.Keys.First());
            f.Evidence(review with { ReviewedArtifacts = incomplete }); Check.Throws<InvalidOperationException>(() => f.Record());
        });
        Check.Case("session: preview and recorded human evidence tampering fail freshness", () =>
        {
            var f = new Fixture(); f.Init(); var status = f.Preview(); f.Evidence(f.Review(status)); f.Record();
            File.AppendAllText(f.Workspace.Resolve("state/task/review.json"), " ");
            Check.Throws<InvalidOperationException>(() => f.Session.Status(Manifest));
            Check.Throws<InvalidOperationException>(() => f.Session.Validate(Manifest));
            var g = new Fixture(); g.Init(); var other = g.Preview();
            File.AppendAllText(g.Workspace.Resolve(other["drawingDirectory"]!.GetValue<string>() + "/bom.json"), " ");
            Check.Throws<InvalidOperationException>(() => g.Session.Status(Manifest));
        });
        Check.Case("session: revision invalidates old evidence and retains explicit ancestry", () =>
        {
            var f = new Fixture(); f.Init(); var status = f.Preview(); f.Evidence(f.Review(status)); f.Record();
            f.Write(DesignExamples.Device() with { Name = "Revised device" });
            Check.Throws<InvalidOperationException>(() => f.Session.Status(Manifest));
            Check.Throws<InvalidOperationException>(() => f.Session.Initialize("inputs/design.json", "device", "v2"));
            var next = View(f.Session.Initialize("inputs/design.json", "device", "v2", Manifest));
            Check.True(!next["wholeAssemblyApproved"]!.GetValue<bool>());
            Check.True(File.Exists(f.Workspace.Resolve("staging/schematic-to-model/device/v2/original/parent-manifest.json")));
        });
        Check.Case("session: changed host/runtime/style identities reject existing version", () =>
        {
            var f = new Fixture(); f.Init();
            foreach (var index in Enumerable.Range(0, 3))
            {
                var session = new DesignSession(f.Workspace, "test", "state/task", "staging", index == 0 ? new string('d',64) : Policy,
                    index == 1 ? new string('d',64) : Runtime, index == 2 ? new string('d',64) : Style);
                Check.Throws<InvalidOperationException>(() => session.Status(Manifest));
            }
        });
        Check.Case("session: failed and interrupted attempts retain history", () =>
        {
            var f = new Fixture(new FailingRenderer()); f.Init();
            Check.Throws<InvalidOperationException>(() => f.Preview());
            var status = View(f.Session.Status(Manifest)); Check.True(status["lastPreviewStatus"]!.GetValue<string>() == "failed");
            Check.True(status["attempts"]!.GetValue<int>() == 1);
            using var cancel = new CancellationTokenSource(); var g = new Fixture(new FailingRenderer(cancel)); g.Init();
            Check.Throws<OperationCanceledException>(() => g.Session.Preview(Manifest, cancel.Token));
            Check.True(View(g.Session.Status(Manifest))["lastPreviewStatus"]!.GetValue<string>() == "failed");
        });
        Check.Case("session: path escapes, canonical identity and state/stage overlap rejected", () =>
        {
            var f = new Fixture();
            Check.Throws<InvalidOperationException>(() => f.Session.Initialize("../outside.json", "device", "v1"));
            Check.Throws<InvalidOperationException>(() => f.Session.Initialize("inputs/design.json", "..", "v1"));
            Check.Throws<InvalidOperationException>(() => new DesignSession(f.Workspace, "test", "state", "state/staging", Policy, Runtime, Style));
            if (OperatingSystem.IsWindows()) Check.Throws<InvalidOperationException>(() => new DesignSession(f.Workspace, "test", "state", "STATE/staging", Policy, Runtime, Style));
            f.Init(); var other = "state/task/copy.json"; File.Copy(f.Workspace.Resolve(Manifest), f.Workspace.Resolve(other));
            Check.Throws<InvalidOperationException>(() => f.Session.Status(other));
        });
    }
}
