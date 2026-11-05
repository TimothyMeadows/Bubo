using System.Text.Json.Nodes;
using Hephaera.AssetProduction;
using Hephaera.ModelAction.Domain;
namespace Hephaera.ModelAction.Application;
public interface IEngineResolver { Task<JsonObject> Resolve(CancellationToken cancellation); }

// The caller supplies the selected identities and finite recipe budgets.
// Infrastructure performs guarded file/preparation inspection; application code
// consumes typed facts without loading host policy or invoking a renderer.
public sealed record ActionEffectInspectionContext(JsonObject Engine, string StyleSha256,
    string RuntimeBuildSha256, JsonObject Budgets);
public interface IActionEffectDependencyInspector
{
    ActionEffectInspection Inspect(IWorkspace workspace, ActionEffectSource source,
        ActionEffectInspectionContext context, CancellationToken cancellation);
    ActionEffectReviewSubject ReviewSubject(IWorkspace workspace, ActionEffectSource source,
        ActionEffectInspectionContext context, CancellationToken cancellation);
}
