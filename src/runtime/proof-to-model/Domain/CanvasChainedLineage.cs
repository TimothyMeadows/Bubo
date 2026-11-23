using System.Text;
using System.Text.Json.Nodes;

namespace Hephaera.ProofToModel.Domain;

/// <summary>One inherited v1 only; immediate prior lists remain its OWN starts.
/// No authorization assertion here authenticates a human approval.</summary>
public static class CanvasChainedLineage
{
    public static void ValidateStageBoundIdentity(string path,string currentHash,IEnumerable<KeyValuePair<string,string>> stageInputs)
    {
        var inputs=stageInputs.Where(pair=>pair.Key==path).ToArray();
        if(inputs.Length==0||inputs.Any(pair=>pair.Value!=currentHash))throw new InvalidOperationException("Immediate prior immutable stage inputs must bind these exact current metadata bytes");
    }
    internal static CanvasStageLineage Parse(JsonObject root)
    {
        CanvasJson.Fields(root,"schemaVersion","kind","decision","task","asset","sourceVersion","targetVersion","authorization","inputApproval","priorManifest","priorStateRoot","priorStaging","priorAttempts","priorDirectories","priorHistoryCounts","stageLimits","inheritedLineage");
        var inherited=CanvasJson.Reference(root["inheritedLineage"]!,false);
        if(root["priorManifest"] is null || root["stageLimits"]?["preprocess"]?.GetValue<int>()!=3)throw new InvalidOperationException("Chained recovery requires actual immediate predecessor and ceiling3");
        // Reuse every v1 structural check with its original allowance. V1 itself
        // remains strict; this copy is not an approval or persisted document.
        var structural=root.DeepClone().AsObject();structural.Remove("inheritedLineage");structural["schemaVersion"]=1;structural["stageLimits"]!["preprocess"]=2;
        var value=CanvasStageLineage.Parse(Encoding.UTF8.GetBytes(structural.ToJsonString()));
        if(value.PriorAttempts["preprocess"].Length!=1||value.PriorDirectories["preprocess"].Length!=1||value.PriorHistoryCounts["preprocess"]!=1||value.PriorAttempts["reconstruct"].Length!=0)throw new InvalidOperationException("Exactly one immediate prior preprocessing start and no model start required");
        var limits=value.StageLimits.ToDictionary(pair=>pair.Key,pair=>pair.Value);limits["preprocess"]=3;
        return value with {InheritedLineage=inherited,StageLimits=limits};
    }
    public static void ValidateInherited(CanvasStageLineage current,CanvasStageLineage inherited)
    {
        if(current.InheritedLineage is null||inherited.InheritedLineage is not null||inherited.PriorManifest is null||current.PriorManifest is null||current.Task!=inherited.Task||current.Asset!=inherited.Asset||current.SourceVersion!=inherited.TargetVersion||current.TargetVersion.Equals(inherited.SourceVersion,StringComparison.OrdinalIgnoreCase)||current.InputApproval!=inherited.InputApproval||inherited.StageLimits["preprocess"]!=2||inherited.PriorAttempts["preprocess"].Length!=1||inherited.PriorDirectories["preprocess"].Length!=1||inherited.PriorHistoryCounts["preprocess"]!=1||current.PriorAttempts["preprocess"].Length!=1||current.PriorAttempts["reconstruct"].Length!=0||inherited.PriorAttempts["reconstruct"].Length!=0)throw new InvalidOperationException("Exactly one same-task/class v1 ancestor with two spent preprocess starts/model0 required");
        foreach(var stage in CanvasStageLineage.Stages)
        {
            var paths=current.PriorAttempts[stage].Concat(inherited.PriorAttempts[stage]).ToArray();
            if(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length||paths.Length>current.StageLimits[stage])throw new InvalidOperationException("Inherited and immediate attempts must be distinct and remain within original stage ceilings");
            if((stage is "reconstruct" or "finish" or "guided" or "review") && paths.Length!=0)throw new InvalidOperationException("No inherited model/downstream expenditure can be reset by preprocessing recovery");
        }
    }
}

/// <summary>Bounded additional-stage assertion, not authentication of its actor.</summary>
public sealed record CanvasAdditionalStageGrant(string Task,string Asset,string SourceVersion,string TargetVersion,CanvasFile PriorManifest,CanvasFile InheritedLineage)
{
    public static CanvasAdditionalStageGrant Parse(byte[] bytes)
    {
        var root=CanvasJson.Parse(bytes);
        CanvasJson.Fields(root,"schemaVersion","kind","authorized","actor","task","asset","sourceVersion","targetVersion","stage","priorManifest","inheritedLineage","consumedAttempts","maximumAttempts","maximumChildren","automaticRetries");
        if(root["schemaVersion"]!.GetValue<int>()!=1||root["kind"]!.GetValue<string>()!="proof-to-model-additional-stage"||root["authorized"]!.GetValue<bool>()!=true||string.IsNullOrWhiteSpace(root["actor"]!.GetValue<string>())||root["actor"]!.GetValue<string>().Length>256||root["stage"]!.GetValue<string>()!="preprocess"||root["consumedAttempts"]!.GetValue<int>()!=2||root["maximumAttempts"]!.GetValue<int>()!=3||root["maximumChildren"]!.GetValue<int>()!=4||root["automaticRetries"]!.GetValue<bool>()!=false)throw new InvalidOperationException("Exact one-additional-stage/max4/no-retry assertion required; project must pin genuine authority");
        return new(CanvasJson.Id(root,"task"),CanvasJson.Id(root,"asset"),CanvasJson.Id(root,"sourceVersion"),CanvasJson.Id(root,"targetVersion"),CanvasJson.Reference(root["priorManifest"]!,false),CanvasJson.Reference(root["inheritedLineage"]!,false));
    }
    public void Validate(CanvasStageLineage lineage)
    {
        if(lineage.InheritedLineage is null||Task!=lineage.Task||Asset!=lineage.Asset||SourceVersion!=lineage.SourceVersion||TargetVersion!=lineage.TargetVersion||PriorManifest!=lineage.PriorManifest||InheritedLineage!=lineage.InheritedLineage)throw new InvalidOperationException("Additional-stage grant must bind exact current predecessor/inherited metadata");
    }
}
