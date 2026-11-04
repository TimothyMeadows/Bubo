using System.Text.Json.Nodes;
using Bubo.Vfx.Application;
using Hephaera.AssetProduction;

namespace Bubo.Vfx.Infrastructure;

/// <summary>Read-only host admission. No native process or authoring service is available here.</summary>
public static class VfxCommands
{
    public static VfxInspectionResult Execute(IWorkspace workspace, string operation, string packagePath,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (operation is not ("validate" or "inspect"))
            throw new InvalidOperationException("Unsupported VFX operation; only validate and inspect are available");
        var context = workspace.HostContext
            ?? throw new InvalidOperationException("VFX public execution requires a validated host context");
        if (context.MediaOnly || context.InspectionOnly)
            throw new InvalidOperationException("VFX packages require a full art and engine host context");
        context.ValidateFresh();
        context.RequireReference(packagePath, "input");
        var result = VfxInspection.Inspect(workspace, packagePath, cancellation);
        if (result.Package.StyleSha256 != context.StyleSha256)
            throw new InvalidOperationException("VFX package STYLE identity differs from the selected host policy");
        var engine = context.Engine;
        RequireEngine(engine, "effectiveEngine", result.Package.Engine.Id);
        RequireEngine(engine, "contractSha256", result.Package.Engine.ContractSha256);
        RequireEngine(engine, "definitionSha256", result.Package.Engine.DefinitionSha256);
        RequireEngine(engine, "registrySha256", result.Package.Engine.RegistrySha256);
        cancellation.ThrowIfCancellationRequested();
        context.ValidateFresh();
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    static void RequireEngine(JsonObject engine, string field, string expected)
    {
        if (engine[field] is not JsonValue value || !value.TryGetValue<string>(out var observed)
            || !string.Equals(observed, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("VFX package engine " + field + " differs from the exact host selection");
    }
}
