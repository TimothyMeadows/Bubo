using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Host;
using Hephaera.AssetProduction;
namespace Bubo.FloorPlan.Infrastructure;
public static class FloorPlanCommands
{
    public static async Task Execute(IWorkspace workspace, INativeProcess process, Dictionary<string,string> options, string operation, CancellationToken cancellation)
    {
        string Need(string key) => options.GetValueOrDefault(key) ?? throw new InvalidOperationException("Missing --" + key);
        var host = workspace.HostContext ?? throw new InvalidOperationException("Floor-plan public execution requires a validated host context");
        if (host.Pipeline != "FLOOR_PLAN_TO_MODEL") throw new InvalidOperationException("Host context must explicitly select FLOOR_PLAN_TO_MODEL");
        var session = new DesignSession(workspace, host.TaskId, host.StateRoot, host.StagingRoot, HostPolicyIdentity.Fingerprint(host), Need("runtime-build-sha256"), host.StyleSha256);
        var construction = new ConstructionSession(workspace, session, process, Path.Combine(AppContext.BaseDirectory, "resources/floor-plan-to-model/blender"), host.TimeoutSeconds, host.MaxLogBytes, host.MaxPrivateBytes, Need("runtime-resource-catalog-sha256"));
        object result = operation switch
        {
            "init" => session.Initialize(Need("plan"), Need("asset"), Need("version"), options.GetValueOrDefault("parent-manifest"), cancellation),
            "preview" => session.Preview(Need("manifest"), cancellation),
            "validate" => session.Validate(Need("manifest"), cancellation),
            "record" => session.Record(Need("manifest"), Need("kind"), Need("reviewer"), Need("evidence"), cancellation),
            "status" => Status(),
            "doctor" => await construction.Doctor(Need("manifest"), options.GetValueOrDefault("blender"), cancellation),
            "build" => await construction.Build(Need("manifest"), Need("blender"), cancellation),
            _ => throw new InvalidOperationException("Unsupported floor-plan design operation")
        };
        Console.WriteLine(JsonSerializer.Serialize(result, Workspace.Json));
        object Status()
        {
            var result = JsonSerializer.SerializeToNode(session.Status(Need("manifest"), cancellation), Workspace.Json)!.AsObject();
            if (session.InspectConstruction(Need("manifest"), cancellation).Plan.Construction is not null)
                result["construction"] = JsonSerializer.SerializeToNode(construction.Status(Need("manifest"), cancellation), Workspace.Json);
            return result;
        }
    }
}
