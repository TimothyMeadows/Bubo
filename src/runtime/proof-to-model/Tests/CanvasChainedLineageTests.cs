using System.Text;
using System.Text.Json.Nodes;
using Hephaera.ProofToModel.Domain;
using Hephaera.ProofToModel.Application;
using Hephaera.ProofToModel.Infrastructure;

/// <summary>Synthetic CPU metadata/inventory tests; no grant or production receipt.</summary>
static class CanvasChainedLineageTests
{
    static byte[] Bytes(JsonObject value)=>Encoding.UTF8.GetBytes(value.ToJsonString());
    static JsonObject Ref(string path)=>new(){["path"]=path,["sha256"]=new string('a',64)};
    static JsonObject Lineage(bool chained)
    {
        var old=chained?"canvas":"original";var target=chained?"cwd":"canvas";
        var attempts=new JsonObject();var directories=new JsonObject();var histories=new JsonObject();var limits=new JsonObject();
        foreach(var stage in CanvasStageLineage.Stages)
        {
            var names=stage=="classify"&&!chained?new[]{"empty","persisted"}:stage is "classify" or "prepare-proofs" or "preprocess"?new[]{"persisted"}:Array.Empty<string>();
            attempts[stage]=new JsonArray(names.Select(name=>(JsonNode?)JsonValue.Create("art/"+old+"/attempts/"+stage+"-"+name)).ToArray());directories[stage]=attempts[stage]!.DeepClone();histories[stage]=names.Length==0?0:1;limits[stage]=stage is "classify" or "prepare-proofs"?4:stage=="preprocess"?(chained?3:2):1;
        }
        var value=new JsonObject{["schemaVersion"]=chained?2:1,["kind"]="proof-to-model-stage-lineage",["decision"]="approved",["task"]="synthetic-task",["asset"]="synthetic-leader",["sourceVersion"]=old,["targetVersion"]=target,["authorization"]=Ref(chained?"task/future-synthetic-grant.json":"task/old-synthetic-grant.json"),["inputApproval"]=Ref("task/synthetic-exact-inputs.json"),["priorManifest"]=Ref("state/"+old+"/manifest.json"),["priorStateRoot"]="state/"+old,["priorStaging"]="art/"+old,["priorAttempts"]=attempts,["priorDirectories"]=directories,["priorHistoryCounts"]=histories,["stageLimits"]=limits};
        if(chained)value["inheritedLineage"]=Ref("task/actual-prior-v1.json");return value;
    }
    static JsonObject Grant(JsonObject lineage)=>new(){["schemaVersion"]=1,["kind"]="proof-to-model-additional-stage",["authorized"]=true,["actor"]="SYNTHETIC TEST ONLY: not a user or production approval",["task"]=lineage["task"]!.DeepClone(),["asset"]=lineage["asset"]!.DeepClone(),["sourceVersion"]=lineage["sourceVersion"]!.DeepClone(),["targetVersion"]=lineage["targetVersion"]!.DeepClone(),["stage"]="preprocess",["priorManifest"]=lineage["priorManifest"]!.DeepClone(),["inheritedLineage"]=lineage["inheritedLineage"]!.DeepClone(),["consumedAttempts"]=2,["maximumAttempts"]=3,["maximumChildren"]=4,["automaticRetries"]=false};
    public static async Task Run(Workspace parent,Action<bool,string> check,Func<Func<Task>,string,Task> reject)
    {
        var prior=CanvasStageLineage.Parse(Bytes(Lineage(false)));var current=CanvasStageLineage.Parse(Bytes(Lineage(true)));
        CanvasChainedLineage.ValidateInherited(current,prior);
        check(prior.InheritedLineage is null&&prior.Limit("preprocess",1)==2,"Original v1 remains strict cumulative2 and unchanged constructor behavior");
        check(current.Limit("preprocess",1)==3&&current.Limit("classify",4)==4&&current.Limit("reconstruct",1)==1,"V2 adds exactly one bounded preprocess stage, no metadata/model expansion");
        check(prior.PriorAttempts["classify"].Length+current.PriorAttempts["classify"].Length==3&&prior.PriorAttempts["prepare-proofs"].Length+current.PriorAttempts["prepare-proofs"].Length==2&&prior.PriorAttempts["preprocess"].Length+current.PriorAttempts["preprocess"].Length==2,"Own immediate plus inherited ORIGINAL counters sum3/2/2 without counting old cumulative totals twice");
        CanvasAdditionalStageGrant.Parse(Bytes(Grant(Lineage(true)))).Validate(current);check(true,"Exact synthetic additional-stage assertion binds actual lineage refs; no human authentication claimed");
        var stagePath="task/actual-prior-v1.json";var oldHash=new string('a',64);var freshHash=new string('b',64);
        var bound=new[]{new KeyValuePair<string,string>(stagePath,oldHash)};
        CanvasChainedLineage.ValidateStageBoundIdentity(stagePath,oldHash,bound);check(true,"Inherited v1 bytes agree with immutable immediate prior stage input hash");
        await reject(()=>Task.Run(()=>CanvasChainedLineage.ValidateStageBoundIdentity(stagePath,freshHash,bound)),"Editing inherited v1 at SAME path plus a fresh reference/grant cannot relabel ancestor when prior manifest stage records old hash");
        await reject(()=>Task.Run(()=>CanvasChainedLineage.ValidateStageBoundIdentity(stagePath,oldHash,Array.Empty<KeyValuePair<string,string>>())),"Missing prior stage binding is not substituted by a current caller reference");
        await reject(()=>Task.Run(()=>CanvasChainedLineage.ValidateStageBoundIdentity(stagePath,oldHash,bound.Append(new(stagePath,freshHash)))),"All recorded metadata stage bindings must agree with actual bytes");
        foreach(var mutate in new Action<JsonObject>[] {x=>x.Remove("inheritedLineage"),x=>x["priorManifest"]=null,x=>x["stageLimits"]!["preprocess"]=4,x=>x["stageLimits"]!["classify"]=5,x=>x["stageLimits"]!["finish"]=2,x=>x["priorHistoryCounts"]!["preprocess"]=0,x=>x["priorAttempts"]!["preprocess"]!.AsArray().Add("art/canvas/attempts/preprocess-second"),x=>x["extra"]=true})
        {var bad=Lineage(true);mutate(bad);await reject(()=>Task.Run(()=>CanvasStageLineage.Parse(Bytes(bad))),"V2 missing ancestor/null predecessor, expanded budget or erased history refuses");}
        var illegalV1=Lineage(false);illegalV1["stageLimits"]!["preprocess"]=3;await reject(()=>Task.Run(()=>CanvasStageLineage.Parse(Bytes(illegalV1))),"V1 cannot gain ceiling3 from additive v2 implementation");
        foreach(var mutate in new Action<JsonObject>[] {x=>x["task"]="foreign",x=>x["asset"]="foreign",x=>x["targetVersion"]="original",x=>x["inputApproval"]!["sha256"]=new string('b',64)})
        {var bad=Lineage(true);mutate(bad);await reject(()=>Task.Run(()=>CanvasChainedLineage.ValidateInherited(CanvasStageLineage.Parse(Bytes(bad)),prior)),"Foreign task/class/input scope or version cycle refuses");}
        await reject(()=>Task.Run(()=>CanvasChainedLineage.ValidateInherited(current,current)),"A v2 ancestor cannot recurse or represent an inherited donor");
        var doubleCount=Lineage(true);doubleCount["priorAttempts"]!["classify"]=Lineage(false)["priorAttempts"]!["classify"]!.DeepClone();doubleCount["priorDirectories"]!["classify"]=doubleCount["priorAttempts"]!["classify"]!.DeepClone();
        await reject(()=>Task.Run(()=>CanvasChainedLineage.ValidateInherited(CanvasStageLineage.Parse(Bytes(doubleCount)),prior)),"Copying original starts into immediate lists cannot reduce or double-count expenditure");
        foreach(var mutate in new Action<JsonObject>[] {x=>x["authorized"]=false,x=>x["actor"]=" ",x=>x["maximumAttempts"]=4,x=>x["consumedAttempts"]=1,x=>x["maximumChildren"]=5,x=>x["automaticRetries"]=true,x=>x["priorManifest"]!["sha256"]=new string('b',64),x=>x["inheritedLineage"]!["path"]="task/copied-donor.json"})
        {var bad=Grant(Lineage(true));mutate(bad);await reject(()=>Task.Run(()=>CanvasAdditionalStageGrant.Parse(Bytes(bad)).Validate(current)),"Missing authority assertion, stale grant/ref, retry or budget expansion refuses");}
        await reject(()=>Task.Run(()=>CanvasAdditionalStageGrant.Parse(Bytes(new(){["syntheticOldGrant"]=true}))),"Old grant body cannot impersonate new additional-stage contract");
        var root=parent.Resolve("chained-live-inventory-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var workspace=new Workspace(root);
        foreach(var path in prior.PriorDirectories["classify"])Directory.CreateDirectory(workspace.Resolve(path));
        var starts=new[]{new JsonObject{["event"]="stage-start",["stage"]="classify",["attempt"]=prior.PriorAttempts["classify"][1]}};
        prior.ValidatePrior("classify",CanvasAttemptInventory.Scan(workspace,"art/original",starts,"classify"),CanvasAttemptInventory.Directories(workspace,"art/original","classify"),1);
        Directory.Delete(workspace.Resolve(prior.PriorAttempts["classify"][0]));
        await reject(()=>Task.Run(()=>prior.ValidatePrior("classify",CanvasAttemptInventory.Scan(workspace,"art/original",starts,"classify"),CanvasAttemptInventory.Directories(workspace,"art/original","classify"),1)),"Live inherited EMPTY directory removal cannot be hidden by persisted history");
    }
}
