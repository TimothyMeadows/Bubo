using System.Text.Json.Nodes;
using Hephaera.AssetProduction;
using Hephaera.ModelAction.Application;
namespace Hephaera.ModelAction.Infrastructure;
public sealed class EngineResolver(IWorkspace workspace, string baseline, INativeProcess process, string task) : IEngineResolver
{
    public Task<JsonObject> Resolve(CancellationToken cancellation) => CanonicalEngineContext.Resolve(workspace, baseline, process, task, cancellation);
}
