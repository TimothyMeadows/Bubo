using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Tests;

public static class AssemblyTests
{
    public static void Run()
    {
        Check.Case("v2 complete examples bind assembly and rig choices into every required preview", () =>
        {
            foreach (var design in new[] { AssemblyExamples.Vehicle(), AssemblyExamples.Device(), AssemblyExamples.Vessel() })
            {
                var validator = new AssemblyDesignValidator(); validator.RequireValid(design);
                var original = JsonSerializer.SerializeToUtf8Bytes(design, StrictDesignJson.Options);
                var copy = StrictDesignJson.Parse<AssemblyDesign>(original);
                Check.True(JsonSerializer.SerializeToUtf8Bytes(copy, StrictDesignJson.Options).AsSpan().SequenceEqual(original), "Complete typed design/profile roundtrip must preserve every authored choice");
                var files = new PreviewCompiler(validator, new SvgSchematicRenderer()).Compile(copy);
                Check.True(PreviewCompiler.RequiredFiles(design).SetEquals(files.Keys));
                Check.True(files.ContainsKey("assembly-details.svg") && files.ContainsKey("flex-rigs.svg"));
                Check.True(files["assembly-details.svg"].Contains("geometry checks 50000000", StringComparison.Ordinal));
                var profile = design.AssemblyProfile!;
                var plan = new AssemblyMotionCompiler().Compile(copy, profile.SamplesPerRange, profile.SamplesPerCycle);
                Check.True(plan.Sampling.TotalSamples > 1 && plan.Clips.Count > 0);
                _ = new FlexibleMotionCompiler().Compile(copy, plan);
            }
        });
        Check.Case("v1 approvals cannot silently acquire v2 profiles or omit v2 drawing budgets", () =>
        {
            var validator = new AssemblyDesignValidator(); var current = AssemblyExamples.Vehicle();
            Check.True(!validator.Validate(current with { SchemaVersion = "bubo-assembly-design/v1" }).IsValid);
            Check.True(!validator.Validate(current with { AssemblyProfile = null }).IsValid);
            var minimum = PreviewCompiler.RequiredFiles(current).Count - 1;
            Check.True(!validator.Validate(current with { Budgets = current.Budgets with { MaxDrawings = minimum - 1 } }).IsValid);
            Check.True(validator.Validate(current with { Budgets = current.Budgets with { MaxDrawings = minimum } }).IsValid);
        });
        Check.Case("v2 whole-design approval cannot omit either assembly or flexible policy sheet", () =>
        {
            var repository = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repository, "Bubo.slnx"))) repository = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException("Fixture repository missing");
            var root = Path.Combine(repository, ".bubo/assembly-policy-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inputs")); var workspace = new Workspace(root);
            File.WriteAllBytes(workspace.ResolveWrite("inputs/design.json"), JsonSerializer.SerializeToUtf8Bytes(AssemblyExamples.Vehicle(), StrictDesignJson.Options));
            var session = new DesignSession(workspace, "test", "state/task", "staging", new('b', 64), new('c', 64), new('a', 64));
            const string manifest = "state/task/schematic-to-model/car/v1/manifest.json";
            session.Initialize("inputs/design.json", "car", "v1");
            var preview = JsonSerializer.SerializeToNode(session.Preview(manifest), StrictDesignJson.Options)!.AsObject();
            var artifacts = preview["artifacts"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
            foreach (var missing in new[] { "assembly-details.svg", "flex-rigs.svg" })
            {
                var partial = new Dictionary<string, string>(artifacts); partial.Remove(missing);
                var review = new WholeAssemblyReview { SchemaVersion = "bubo-schematic-review/v1", Kind = "whole-assembly-design", Decision = "approved", Human = true,
                    Reviewer = "synthetic-policy-fixture", Reason = "Synthetic incomplete-review protocol assertion; no actual human asset acceptance.", Asset = "car", Version = "v1",
                    DesignSha256 = preview["designSha256"]!.GetValue<string>(), DesignFingerprint = preview["designFingerprint"]!.GetValue<string>(), ReviewedArtifacts = partial };
                workspace.Save("state/task/review.json", review);
                Check.Throws<InvalidOperationException>(() => session.Record(manifest, review.Kind, review.Reviewer, "state/task/review.json"));
            }
        });
    }
}
