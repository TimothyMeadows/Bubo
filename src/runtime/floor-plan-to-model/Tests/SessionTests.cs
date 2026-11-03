using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.FloorPlan.Application;
using Bubo.FloorPlan.Domain;
using Bubo.FloorPlan.Infrastructure;
using Hephaera.AssetProduction;
namespace Bubo.FloorPlan.Tests;

public static class SessionTests
{
    const string Manifest = "state/task/floor-plan-to-model/house/v1/manifest.json";
    static readonly string Policy = new('b', 64), Runtime = new('c', 64), Style = new('a', 64);
    static JsonObject View(object value) => JsonSerializer.SerializeToNode(value, StrictPlanJson.Options)!.AsObject();
    sealed class Fixture
    {
        public string Root { get; }
        public Workspace Workspace { get; }
        public DesignSession Session { get; }
        public Fixture(IArchitecturalPreviewRenderer? renderer = null)
        {
            var source = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(source, "Bubo.slnx"))) source = Directory.GetParent(source)?.FullName ?? throw new InvalidOperationException("Fixture repository root missing");
            Root = Path.Combine(source, ".bubo", "floor-plan-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(Root, "inputs"));
            Workspace = new(Root); WritePlan(PlanExamples.SingleStorey());
            Session = new(Workspace, "test", "state/task", "staging", Policy, Runtime, Style, renderer: renderer);
        }
        public void WritePlan(BuildingPlan plan) => File.WriteAllBytes(Path.Combine(Root, "inputs/plan.json"), JsonSerializer.SerializeToUtf8Bytes(plan, StrictPlanJson.Options));
        public JsonObject Initialize() => View(Session.Initialize("inputs/plan.json", "house", "v1"));
        public JsonObject Preview() => View(Session.Preview(Manifest));
        public WholeBuildingReview Review(JsonObject status) => new()
        {
            SchemaVersion = "bubo-floor-plan-review/v1", Kind = "whole-building-design", Decision = "approved", Human = true,
            Reviewer = "synthetic-review-fixture", Reason = "Synthetic protocol fixture only; this is not actual human acceptance.",
            Asset = "house", Version = "v1", PlanSha256 = status["planSha256"]!.GetValue<string>(), DesignFingerprint = status["designFingerprint"]!.GetValue<string>(),
            ReviewedArtifacts = status["drawings"]!.AsObject().ToDictionary(x => x.Key, x => x.Value!.GetValue<string>())
        };
        public void Evidence(WholeBuildingReview review, string path = "state/task/human-review.json")
        { Directory.CreateDirectory(Path.GetDirectoryName(Workspace.ResolveWrite(path))!); File.WriteAllBytes(Workspace.ResolveWrite(path), JsonSerializer.SerializeToUtf8Bytes(review, StrictPlanJson.Options)); }
        public JsonObject Record() => View(Session.Record(Manifest, "whole-building-design", "synthetic-review-fixture", "state/task/human-review.json"));
    }
    sealed class FailingRenderer(CancellationTokenSource? cancel = null) : IArchitecturalPreviewRenderer
    {
        public IReadOnlyDictionary<string, string> Render(BuildingPlan plan, CancellationToken cancellationToken = default)
        { if (cancel is not null) { cancel.Cancel(); cancellationToken.ThrowIfCancellationRequested(); } throw new InvalidOperationException("Controlled renderer failure"); }
    }
    public static void Run()
    {
        Check.Case("JSON: native example bytes match audited LF resources on every host", () =>
        {
            var root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "Bubo.slnx"))) root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Fixture repository root missing");
            var examples = new Dictionary<string, BuildingPlan> { ["single-storey"] = PlanExamples.SingleStorey(), ["multi-storey"] = PlanExamples.MultiStorey(), ["angled-courtyard"] = PlanExamples.AngledCourtyard(), ["stacked-three-storey"] = PlanExamples.StackedThreeStorey() };
            foreach (var (name, plan) in examples)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, StrictPlanJson.Options);
                Check.True(!bytes.Contains((byte)'\r') && bytes.Contains((byte)'\n'), "Native JSON must use portable LF bytes");
                Check.True(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "resources/floor-plan-to-model/examples", name + ".json"))), "Serialized example differs from its audited resource: " + name);
            }
        });
        Check.Case("JSON: three typed examples roundtrip strictly", () =>
        { foreach (var plan in new[] { PlanExamples.SingleStorey(), PlanExamples.MultiStorey(), PlanExamples.AngledCourtyard() }) new BuildingPlanValidator().RequireValid(StrictPlanJson.Parse<BuildingPlan>(JsonSerializer.SerializeToUtf8Bytes(plan, StrictPlanJson.Options))); });
        Check.Case("JSON: unknown fields, duplicate fields, wrong case and missing point coordinates fail", () =>
        {
            var raw = JsonSerializer.Serialize(PlanExamples.SingleStorey(), StrictPlanJson.Options);
            Check.Throws<JsonException>(() => StrictPlanJson.Parse<BuildingPlan>(System.Text.Encoding.UTF8.GetBytes(raw.Replace("\"coordinateSystem\"", "\"CoordinateSystem\""))));
            var node = JsonNode.Parse(raw)!.AsObject(); node["unknown"] = true; Check.Throws<JsonException>(() => StrictPlanJson.Parse<BuildingPlan>(JsonSerializer.SerializeToUtf8Bytes(node)));
            Check.Throws<InvalidOperationException>(() => StrictPlanJson.Parse<BuildingPlan>(System.Text.Encoding.UTF8.GetBytes(raw.Insert(1, "\"id\":\"duplicate\","))));
            node = JsonNode.Parse(raw)!.AsObject(); node["storeys"]![0]!["footprint"]!["outer"]![0]!.AsObject().Remove("x");
            Check.Throws<JsonException>(() => StrictPlanJson.Parse<BuildingPlan>(JsonSerializer.SerializeToUtf8Bytes(node)));
        });
        Check.Case("JSON: exact enum strings and bounded documents", () =>
        {
            var raw = JsonSerializer.Serialize(PlanExamples.SingleStorey(), StrictPlanJson.Options);
            Check.Throws<JsonException>(() => StrictPlanJson.Parse<BuildingPlan>(System.Text.Encoding.UTF8.GetBytes(raw.Replace("\"door\"", "\"Door\""))));
            Check.Throws<InvalidOperationException>(() => StrictPlanJson.Parse<BuildingPlan>(new byte[StrictPlanJson.MaximumInputBytes + 1]));
        });
        Check.Case("session: immutable initialization and no automatic acceptance", () =>
        {
            var f = new Fixture(); var result = f.Initialize(); Check.True(!result["wholeBuildingApproved"]!.GetValue<bool>() && !result["modelingAvailable"]!.GetValue<bool>() && !result["modelExportAvailable"]!.GetValue<bool>());
            Check.True(File.ReadAllBytes(f.Workspace.Resolve("inputs/plan.json")).SequenceEqual(File.ReadAllBytes(f.Workspace.Resolve("staging/floor-plan-to-model/house/v1/original/building-plan.json"))));
            Check.Throws<InvalidOperationException>(() => f.Initialize());
        });
        Check.Case("session: complete real preview package stays within owned staging", () =>
        {
            var f = new Fixture(); f.Initialize(); var result = f.Preview(); Check.True(result["completeArchitecturalPreview"]!.GetValue<bool>() && result["drawings"]!.AsObject().Count == 7 && !result["wholeBuildingApproved"]!.GetValue<bool>());
            Check.True(f.Session.Validate(Manifest).IsValid); Check.True(View(f.Session.Status(Manifest))["designFingerprint"]!.GetValue<string>() == result["designFingerprint"]!.GetValue<string>());
        });
        Check.Case("session: partial package, false human and wrong review identity cannot approve", () =>
        {
            var f = new Fixture(); f.Initialize(); var status = f.Preview(); var good = f.Review(status);
            foreach (var review in new[] { good with { Human = false }, good with { Version = "other" }, good with { Kind = "individual-floor" }, good with { Reviewer = "different" }, good with { PlanSha256 = new('0', 64) }, good with { ReviewedArtifacts = good.ReviewedArtifacts.Skip(1).ToDictionary(x => x.Key, x => x.Value) } })
            { f.Evidence(review); Check.Throws<InvalidOperationException>(() => f.Record()); }
            Check.True(!View(f.Session.Status(Manifest))["wholeBuildingApproved"]!.GetValue<bool>());
        });
        Check.Case("session: explicit synthetic complete review and later rejection retain history", () =>
        {
            var f = new Fixture(); f.Initialize(); var status = f.Preview(); var good = f.Review(status); f.Evidence(good); Check.True(f.Record()["wholeBuildingApproved"]!.GetValue<bool>());
            f.Evidence(good with { Decision = "rejected" }, "state/task/rejection.json"); var rejected = View(f.Session.Record(Manifest, good.Kind, good.Reviewer, "state/task/rejection.json"));
            Check.True(!rejected["wholeBuildingApproved"]!.GetValue<bool>() && rejected["reviews"]!.GetValue<int>() == 2);
        });
        Check.Case("session: reusing a caller review path preserves earlier exact evidence snapshots", () =>
        {
            var f = new Fixture(); f.Initialize(); var preview = f.Preview(); var good = f.Review(preview); f.Evidence(good); f.Record();
            var first = StrictPlanJson.Read<DesignManifest>(f.Workspace.Resolve(Manifest)).Reviews[0];
            var bytes = File.ReadAllBytes(f.Workspace.Resolve(first.Evidence));
            f.Evidence(good with { Decision = "rejected" }); f.Record();
            var records = StrictPlanJson.Read<DesignManifest>(f.Workspace.Resolve(Manifest)).Reviews;
            Check.True(records.Count == 2 && records[0].Evidence != records[1].Evidence && bytes.SequenceEqual(File.ReadAllBytes(f.Workspace.Resolve(records[0].Evidence))));
            Check.True(!View(f.Session.Status(Manifest))["wholeBuildingApproved"]!.GetValue<bool>());
        });
        Check.Case("session: changed source requires new version and explicit immutable parent ancestry", () =>
        {
            var f = new Fixture(); f.Initialize(); var original = f.Preview(); f.Evidence(f.Review(original)); f.Record();
            File.AppendAllText(f.Workspace.Resolve("inputs/plan.json"), "\n"); Check.Throws<InvalidOperationException>(() => f.Session.Status(Manifest));
            Check.Throws<InvalidOperationException>(() => f.Session.Initialize("inputs/plan.json", "house", "v2"));
            var next = View(f.Session.Initialize("inputs/plan.json", "house", "v2", Manifest)); Check.True(!next["wholeBuildingApproved"]!.GetValue<bool>() && next["reviews"]!.GetValue<int>() == 0);
            Check.True(File.Exists(f.Workspace.Resolve("staging/floor-plan-to-model/house/v2/original/parent-manifest.json")));
        });
        Check.Case("session: policy/runtime/style changes cannot inherit approvals", () =>
        {
            var f = new Fixture(); f.Initialize(); f.Preview();
            foreach (var (policy, runtime, style) in new[] { (new string('d', 64), Runtime, Style), (Policy, new string('d', 64), Style), (Policy, Runtime, new string('d', 64)) })
                Check.Throws<InvalidOperationException>(() => new DesignSession(f.Workspace, "test", "state/task", "staging", policy, runtime, style).Status(Manifest));
        });
        Check.Case("session: tampered drawing or package index invalidates output", () =>
        {
            foreach (var file in new[] { "floor-01.svg", "preview-index.json" })
            { var f = new Fixture(); f.Initialize(); var preview = f.Preview(); File.AppendAllText(f.Workspace.Resolve(preview["drawingDirectory"]!.GetValue<string>() + "/" + file), "changed"); Check.Throws<InvalidOperationException>(() => f.Session.Status(Manifest)); }
        });
        Check.Case("session: mutated recorded review cannot retain acceptance", () =>
        { var f = new Fixture(); f.Initialize(); var preview = f.Preview(); f.Evidence(f.Review(preview)); f.Record(); File.AppendAllText(f.Workspace.Resolve("state/task/human-review.json"), "\n"); Check.Throws<InvalidOperationException>(() => f.Session.Status(Manifest)); });
        Check.Case("session: renderer failure is retained without claiming preview or approval", () =>
        {
            var f = new Fixture(new FailingRenderer()); f.Initialize(); Check.Throws<InvalidOperationException>(() => f.Preview()); var status = View(f.Session.Status(Manifest));
            Check.True(status["lastPreviewStatus"]!.GetValue<string>() == "failed" && status["attempts"]!.GetValue<int>() == 1 && !status["completeArchitecturalPreview"]!.GetValue<bool>() && !status["wholeBuildingApproved"]!.GetValue<bool>());
        });
        Check.Case("session: cancellation preserves started failed attempt", () =>
        { using var cancel = new CancellationTokenSource(); var f = new Fixture(new FailingRenderer(cancel)); f.Initialize(); Check.Throws<OperationCanceledException>(() => f.Session.Preview(Manifest, cancel.Token)); Check.True(View(f.Session.Status(Manifest))["lastPreviewStatus"]!.GetValue<string>() == "failed"); });
        Check.Case("session: canonical paths, no overwrite and exclusive ownership", () =>
        {
            var f = new Fixture(); Check.Throws<InvalidOperationException>(() => f.Session.Initialize("../outside.json", "house", "v1")); Check.Throws<InvalidOperationException>(() => f.Session.Initialize("inputs/plan.json", "../house", "v1"));
            f.Initialize(); Check.Throws<InvalidOperationException>(() => f.Session.Status("state/task/elsewhere.json"));
            using var ownership = f.Workspace.Lock("state/task/floor-plan-to-model/house/v1/asset.lock"); Check.Throws<InvalidOperationException>(() => f.Preview());
        });
        Check.Case("session: task-owned design inputs are allowed without self-reference", () =>
        { var f = new Fixture(); Directory.CreateDirectory(f.Workspace.ResolveWrite("state/task/source")); File.Copy(f.Workspace.Resolve("inputs/plan.json"), f.Workspace.ResolveWrite("state/task/source/design.json")); f.Session.Initialize("state/task/source/design.json", "house", "v1"); Check.True(View(f.Session.Status(Manifest))["phase"]!.GetValue<string>() == "design-foundation"); });
    }
}
