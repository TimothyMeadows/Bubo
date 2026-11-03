using System.Text.Json;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Infrastructure;

public static class SchematicCommands
{
    public static async Task Execute(IWorkspace workspace, INativeProcess process, Dictionary<string, string> options, string operation, CancellationToken cancellation)
    {
        string Need(string key) => options.GetValueOrDefault(key) ?? throw new InvalidOperationException("Missing --" + key);
        var host = workspace.HostContext ?? throw new InvalidOperationException("Schematic execution requires validated host context");
        if (host.Pipeline != "SCHEMATIC_TO_MODEL") throw new InvalidOperationException("Host must explicitly select SCHEMATIC_TO_MODEL");
        var session = new DesignSession(workspace, host.TaskId, host.StateRoot, host.StagingRoot, HostPolicyIdentity.Fingerprint(host), Need("runtime-build-sha256"), host.StyleSha256);
        var parts = new PartSession(workspace, session, process, Path.Combine(AppContext.BaseDirectory, "resources/schematic-to-model/blender"),
            host.TimeoutSeconds, host.MaxLogBytes, host.MaxPrivateBytes, Need("runtime-resource-catalog-sha256"));
        object result = operation switch
        {
            "init" => session.Initialize(Need("design"), Need("asset"), Need("version"), options.GetValueOrDefault("parent-manifest"), cancellation),
            "preview" => session.Preview(Need("manifest"), cancellation),
            "validate" => session.Validate(Need("manifest"), cancellation),
            "record" => session.Record(Need("manifest"), Need("kind"), Need("reviewer"), Need("evidence"), cancellation),
            "status" => Status(),
            "doctor" => await parts.Doctor(Need("manifest"), options.GetValueOrDefault("blender"), cancellation),
            "build-parts" => await parts.BuildParts(Need("manifest"), Need("blender"), cancellation),
            _ => throw new InvalidOperationException("Schematic assembly, motion and canonical model export require later reviewed deliveries")
        };
        Console.WriteLine(JsonSerializer.Serialize(result, Workspace.Json));
        object Status()
        {
            var status = JsonSerializer.SerializeToNode(session.Status(Need("manifest"), cancellation), Workspace.Json)!.AsObject();
            status["parts"] = JsonSerializer.SerializeToNode(parts.Status(Need("manifest"), cancellation), Workspace.Json);
            return status;
        }
    }
}
