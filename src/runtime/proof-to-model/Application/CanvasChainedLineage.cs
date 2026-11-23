using System.Text.Json.Nodes;
using Hephaera.ProofToModel.Domain;

namespace Hephaera.ProofToModel.Application;

public sealed partial class Pipeline
{
    CanvasStageLineage InheritedCanvasLineage(CanvasStageLineage current)
    {
        var reference=current.InheritedLineage??throw new InvalidOperationException("Explicit inherited v1 reference required");
        RequireTaskEvidence(reference.Path);Host.RequireReference(reference.Path);CanvasReference(reference);
        var inherited=CanvasStageLineage.Parse(CanvasBytes(reference.Path));
        CanvasChainedLineage.ValidateInherited(current,inherited);
        return inherited;
    }
    int CanvasPriorAttemptCount(CanvasStageLineage lineage,string stage) => checked(lineage.PriorAttempts[stage].Length+(lineage.InheritedLineage is null?0:InheritedCanvasLineage(lineage).PriorAttempts[stage].Length));
    IEnumerable<string> CanvasAncestryPaths()
    {
        if(CanvasLineage() is not {InheritedLineage:not null} lineage) return [];
        var inherited=InheritedCanvasLineage(lineage);
        return new[]{lineage.InheritedLineage!.Path,lineage.Authorization.Path,lineage.InputApproval.Path,lineage.PriorManifest!.Path,inherited.Authorization.Path,inherited.InputApproval.Path,inherited.PriorManifest!.Path};
    }
    CanvasStageLineage? ValidateCanvasInheritedLineage(CanvasStageLineage lineage,Manifest? prior)
    {
        if(lineage.InheritedLineage is null)return null; // Original v1 path unchanged.
        if(prior is null||prior.StageLineageFile!=lineage.InheritedLineage.Path||prior.PreprocessingProfileFile is null)throw new InvalidOperationException("Inherited lineage must be the actual immediate prior manifest's paired v1 metadata");
        CanvasChainedLineage.ValidateStageBoundIdentity(prior.StageLineageFile,Hash(prior.StageLineageFile),prior.Stages.Values.SelectMany(stage=>stage.Inputs));
        var inherited=InheritedCanvasLineage(lineage);
        inherited.ValidateScope(m.Task,m.Asset,prior.Version);
        if(inherited.SourceVersion.Equals(m.Version,StringComparison.OrdinalIgnoreCase)||inherited.SourceVersion.Equals(prior.Version,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("No lineage cycle or same-version ancestor");
        Host.RequireReference(inherited.Authorization.Path);Host.RequireReference(inherited.InputApproval.Path);
        CanvasReference(inherited.Authorization);CanvasReference(inherited.InputApproval);
        var originalState=TaskRoot(m.Task)+"/proof-to-model/"+m.Asset+"/"+inherited.SourceVersion;
        var originalArt=StagingRoot+"/proof-to-model/"+m.Asset+"/"+inherited.SourceVersion;
        var originalManifest=inherited.PriorManifest??throw new InvalidOperationException("Actual inherited original manifest required");
        if(inherited.PriorStateRoot!=originalState||inherited.PriorStaging!=originalArt||originalManifest.Path!=originalState+"/manifest.json")throw new InvalidOperationException("Exact same-task/class original state/staging required");
        RequireCanvasReadOnlyRoot(originalState);RequireCanvasReadOnlyRoot(originalArt);
        RequireTaskEvidence(originalManifest.Path);Host.RequireReference(originalManifest.Path);CanvasReference(originalManifest);
        var original=w.Load<Manifest>(originalManifest.Path);
        if(original.SchemaVersion!=3||original.ConditioningProfile!=m.ConditioningProfile||original.Task!=m.Task||original.Asset!=m.Asset||original.Version!=inherited.SourceVersion||original.Staging!=originalArt||original.PreprocessingProfileFile is not null||original.StageLineageFile is not null||Hash(original.Proof)!=Hash(m.Proof)||Hash(original.Toolchain)!=Hash(m.Toolchain)||Hash(original.Rights)!=Hash(m.Rights)||Hash(original.Recipe)!=Hash(m.Recipe))throw new InvalidOperationException("Inherited original must remain unchanged four-view base; no recursive recovery");
        foreach(var stage in CanvasStageLineage.Stages)
        {
            var observed=CanvasAttemptInventory.Scan(w,originalArt,original.History,stage);
            var persisted=original.History.Count(item=>item["event"]?.GetValue<string>()=="stage-start"&&item["stage"]?.GetValue<string>()==stage);
            inherited.ValidatePrior(stage,observed,CanvasAttemptInventory.Directories(w,originalArt,stage),persisted);
            var configured=stage is "classify" or "prepare-proofs"?w.Load<JsonObject>(m.Recipe)["proofPreparation"]!["maxSetRevisions"]!.GetValue<int>():Toolchain["limits"]!["maxAttempts"]!.GetValue<int>();
            _=inherited.Limit(stage,configured);
        }
        foreach(var item in new[]{(State:original,Lineage:inherited),(State:prior,Lineage:lineage)})
        {
            var expected=item.Lineage.PriorAttempts["preprocess"].Single();
            if(!item.State.Stages.TryGetValue("preprocess",out var failed)||failed.Status!="failed"||failed.Attempt!=expected||item.State.Stages.ContainsKey("reconstruct"))throw new InvalidOperationException("Both actually retained preprocess stages must be FAILED, with no model stage");
        }
        foreach(var stage in new[]{"reconstruct","finish","guided","review"})
            if(inherited.PriorAttempts[stage].Length!=0||lineage.PriorAttempts[stage].Length!=0)throw new InvalidOperationException("Chained preprocessing cannot carry or reset any model/downstream attempt");
        // The prior profile is preserved and hash-bound by its actual stage inputs;
        // it is never rewritten into a new profile or trusted via a copied name.
        Host.RequireReference(prior.PreprocessingProfileFile);
        var priorProfileHash=Hash(prior.PreprocessingProfileFile);
        CanvasChainedLineage.ValidateStageBoundIdentity(prior.PreprocessingProfileFile,priorProfileHash,prior.Stages.Values.SelectMany(stage=>stage.Inputs));
        _=CanvasMattingProfile.Parse(CanvasBytes(prior.PreprocessingProfileFile));
        CanvasReference(lineage.Authorization);Host.RequireReference(lineage.Authorization.Path);
        CanvasAdditionalStageGrant.Parse(CanvasBytes(lineage.Authorization.Path)).Validate(lineage);
        return inherited;
    }
}
