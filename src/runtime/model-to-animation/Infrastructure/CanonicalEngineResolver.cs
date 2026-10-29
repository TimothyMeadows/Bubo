using System.Text.Json.Nodes;
using OpenCaw.AssetProduction;
using OpenCaw.ModelAnimation.Application;
namespace OpenCaw.ModelAnimation.Infrastructure;

// Compatibility adapter for the validated caller-supplied engine selection.
// It never launches a host resolver or reads instruction documents.
public sealed class CanonicalEngineResolver(IWorkspace workspace, string baseline, INativeProcess process, string task) : IEngineResolver
{
    public Task<JsonObject> Resolve(CancellationToken cancellation) =>
        CanonicalEngineContext.Resolve(workspace, baseline, process, task, cancellation);
}
