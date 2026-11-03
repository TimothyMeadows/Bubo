using System.Text.Json;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Infrastructure;

public static class SchematicCommands
{
    public static Task Execute(IWorkspace workspace, Dictionary<string, string> options, string operation, CancellationToken cancellation)
    {
        string Need(string key) => options.GetValueOrDefault(key) ?? throw new InvalidOperationException("Missing --" + key);
        var host = workspace.HostContext ?? throw new InvalidOperationException("Schematic execution requires validated host context");
        if (host.Pipeline != "SCHEMATIC_TO_MODEL") throw new InvalidOperationException("Host must explicitly select SCHEMATIC_TO_MODEL");
        var session = new DesignSession(workspace, host.TaskId, host.StateRoot, host.StagingRoot, HostPolicyIdentity.Fingerprint(host), Need("runtime-build-sha256"), host.StyleSha256);
        object result = operation switch
        {
            "init" => session.Initialize(Need("design"), Need("asset"), Need("version"), options.GetValueOrDefault("parent-manifest"), cancellation),
            "preview" => session.Preview(Need("manifest"), cancellation),
            "validate" => session.Validate(Need("manifest"), cancellation),
            "record" => session.Record(Need("manifest"), Need("kind"), Need("reviewer"), Need("evidence"), cancellation),
            "status" => session.Status(Need("manifest"), cancellation),
            "doctor" => new { schemaVersion = "bubo-schematic-doctor/v1", phase = "design-foundation", nativeDesignReady = true,
                blenderRequiredForDesign = false, partConstructionAvailable = false, assemblyAvailable = false, motionProofAvailable = false, modelExportAvailable = false,
                design = session.Status(Need("manifest"), cancellation) },
            _ => throw new InvalidOperationException("Schematic construction, assembly, motion and export require later reviewed deliveries")
        };
        Console.WriteLine(JsonSerializer.Serialize(result, Workspace.Json));
        return Task.CompletedTask;
    }
}
