using System.Text.Json;
using Bubo.FloorPlan.Domain;
using Bubo.FloorPlan.Infrastructure;
using Bubo.FloorPlan.Tests;
using Hephaera.AssetProduction;

if (args is ["--write-examples", var directory])
{
    var root = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(root, "Bubo.slnx"))) root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Bubo test repository root missing");
    var workspace = new Workspace(root);
    var target = workspace.ResolveWrite(directory);
    if (workspace.Relative(target) != "resources/floor-plan-to-model/examples") throw new InvalidOperationException("Fixture writer only writes the declared owned source example directory");
    var examples = new Dictionary<string, BuildingPlan> { ["single-storey"] = PlanExamples.SingleStorey(), ["multi-storey"] = PlanExamples.MultiStorey(), ["angled-courtyard"] = PlanExamples.AngledCourtyard(), ["stacked-three-storey"] = PlanExamples.StackedThreeStorey() };
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
if (args.Length != 0) throw new InvalidOperationException("Unknown native fixture operation");
DomainTests.Run();
PreviewTests.Run();
SessionTests.Run();
Console.WriteLine(JsonSerializer.Serialize(new { status = Check.Failed == 0 ? "passed" : "failed", Check.Passed, Check.Failed, scope = "Native floor-plan design-only fixtures; human assertions synthetic, no model/export/runtime claim" }));
return Check.Failed == 0 ? 0 : 1;
