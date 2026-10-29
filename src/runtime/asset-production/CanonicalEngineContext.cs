using System.Text.Json.Nodes;

namespace OpenCaw.AssetProduction;

// Compatibility name retained; only caller-supplied engine evidence is used.
public static class CanonicalEngineContext
{
    public static Task<JsonObject> Resolve(IWorkspace workspace, string runtimeRoot, INativeProcess process, string task, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var context = workspace.HostContext
            ?? throw new InvalidOperationException("A validated Bubo host context is required; no host engine resolver is invoked");
        if (task != context.TaskId)
            throw new InvalidOperationException("Host context does not belong to this task");
        context.ValidateFresh();
        return Task.FromResult(context.Engine.DeepClone().AsObject());
    }
}
