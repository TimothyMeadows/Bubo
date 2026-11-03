using System.Text.Json;
using Bubo.FloorPlan.Domain;
using Bubo.FloorPlan.Infrastructure;
using Bubo.FloorPlan.Tests;
using Hephaera.AssetProduction;

if (args is ["--review-construction-source", var qaRoot, var context, var runtimeDirectory, var reviewJob, var blender])
    return await ConstructionBlenderReviewDriver.Run(qaRoot, context, runtimeDirectory, reviewJob, blender);

if (args is [var writer, var directory] && writer is ("--write-examples" or "--write-construction-examples"))
{
    var root = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(root, "Bubo.slnx"))) root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Bubo test repository root missing");
    var workspace = new Workspace(root);
    var target = workspace.ResolveWrite(directory);
    var expected = writer == "--write-examples" ? "resources/floor-plan-to-model/examples" : "resources/floor-plan-to-model/examples-v2";
    if (workspace.Relative(target) != expected) throw new InvalidOperationException("Fixture writer only writes the declared owned source example directory");
    var examples = writer == "--write-examples" ? new Dictionary<string, BuildingPlan> { ["single-storey"] = PlanExamples.SingleStorey(), ["multi-storey"] = PlanExamples.MultiStorey(), ["angled-courtyard"] = PlanExamples.AngledCourtyard(), ["stacked-three-storey"] = PlanExamples.StackedThreeStorey() }
        : new Dictionary<string, BuildingPlan> { ["single-storey"] = ConstructionPlanExamples.SingleStorey(), ["multi-storey"] = ConstructionPlanExamples.MultiStorey(), ["angled-courtyard"] = ConstructionPlanExamples.AngledCourtyard(), ["stacked-three-storey"] = ConstructionPlanExamples.StackedThreeStorey() };
    Directory.CreateDirectory(target);
    foreach (var (name, plan) in examples)
    {
        var validation = new BuildingPlanValidator().Validate(plan);
        if (!validation.IsValid) Console.Error.WriteLine(JsonSerializer.Serialize(validation));
        new BuildingPlanValidator().RequireValid(plan);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, StrictPlanJson.Options);
        var parsed = StrictPlanJson.Parse<BuildingPlan>(bytes); new BuildingPlanValidator().RequireValid(parsed);
        File.WriteAllBytes(workspace.ResolveWrite(Path.Combine(target, name + ".json")), bytes);
        Console.WriteLine("Wrote validated native source example: " + name);
    }
    return 0;
}
var constructionDomainOnly = args is ["--construction-domain-tests"];
if (args.Length != 0 && !constructionDomainOnly) throw new InvalidOperationException("Unknown native fixture operation");
if (!constructionDomainOnly) { DomainTests.Run(); PreviewTests.Run(); SessionTests.Run(); }
ConstructionContractTests.Run();
ConstructionGeometryTests.Run();
if (!constructionDomainOnly) ConstructionSessionTests.Run();
Console.WriteLine(JsonSerializer.Serialize(new { status = Check.Failed == 0 ? "passed" : "failed", Check.Passed, Check.Failed, scope = "Native design and deterministic construction geometry; process/human fixtures synthetic, actual Blender evidence separate" }));
return Check.Failed == 0 ? 0 : 1;
