using System.Text.Json;
using Bubo.Host;
using Hephaera.AssetProduction;
namespace Bubo.FloorPlan.Infrastructure;
public static class FloorPlanCommands
{
    public static Task Execute(IWorkspace workspace, Dictionary<string,string> options, string operation, CancellationToken cancellation)
    {
        string Need(string key) => options.GetValueOrDefault(key) ?? throw new InvalidOperationException("Missing --" + key);
        var host = workspace.HostContext ?? throw new InvalidOperationException("Floor-plan public execution requires a validated host context");
        if (host.Pipeline != "FLOOR_PLAN_TO_MODEL") throw new InvalidOperationException("Host context must explicitly select FLOOR_PLAN_TO_MODEL");
        var session = new DesignSession(workspace, host.TaskId, host.StateRoot, host.StagingRoot, HostPolicyIdentity.Fingerprint(host), Need("runtime-build-sha256"), host.StyleSha256);
        object result = operation switch
        {
            "init" => session.Initialize(Need("plan"), Need("asset"), Need("version"), options.GetValueOrDefault("parent-manifest"), cancellation),
            "preview" => session.Preview(Need("manifest"), cancellation),
            "validate" => session.Validate(Need("manifest"), cancellation),
            "record" => session.Record(Need("manifest"), Need("kind"), Need("reviewer"), Need("evidence"), cancellation),
            "status" => session.Status(Need("manifest"), cancellation),
            "doctor" => new { schemaVersion = "bubo-floor-plan-doctor/v1", phase = "design-foundation", vectorPreviewAvailable = true, modelingAvailable = false, modelExportAvailable = false, provisioningRequired = false, note = "Design validation and SVG previews only. No Blender/model/engine readiness inferred." },
            _ => throw new InvalidOperationException("Unsupported floor-plan design operation")
        };
        Console.WriteLine(JsonSerializer.Serialize(result, Workspace.Json));
        return Task.CompletedTask;
    }
}
