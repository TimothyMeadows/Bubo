using Bubo.Splats.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Splats.Infrastructure;

public sealed record SplatAttemptClaim
{
    public required string SchemaVersion { get; init; }
    public required string SourceSha256 { get; init; }
    public required string Kind { get; init; }
    public required string Task { get; init; }
    public required string RunId { get; init; }
    public required string RequestSha256 { get; init; }
    public required string ApprovalSha256 { get; init; }
    public required string HostContextSha256 { get; init; }
    public required string? CandidateClaimSha256 { get; init; }
    public required DateTimeOffset ClaimedAt { get; init; }
}
/// <summary>Claims are never removed or rewritten, including failed, interrupted and uncertain launches.</summary>
public sealed class SplatLedger(Workspace workspace)
{
    public string Root
    {
        get
        {
            var host=workspace.HostContext??throw new InvalidOperationException("Host context required");
            if(!host.HasExplicitMediaStateRoot)throw new InvalidOperationException("Explicit durable project-shared generative-media state is required; per-task state cannot reset splat attempts");
            return host.MediaStateRoot+"/image-to-splat/ledger";
        }
    }
    public SplatGenerationApproval Admit(SplatGenerationRequest request,string catalog)
    {
        var approval=SplatJson.Read<SplatGenerationApproval>(workspace,request.Authorization.Path,"rights");
        if(workspace.HashProjectFile(request.Authorization.Path)!=request.Authorization.Sha256)throw new InvalidOperationException("Generation approval identity changed");
        SplatGenerationRules.ValidateApproval(approval,request,catalog,Root);
        var rights=SplatJson.Read<SplatSourceRights>(workspace,approval.SourceRights.Path,"rights");
        if(workspace.HashProjectFile(approval.SourceRights.Path)!=approval.SourceRights.Sha256 || rights.SchemaVersion!="bubo-splat-source-rights/v1"
            || rights.SourceSha256!=request.Request.Input.Sha256 || rights.Scope!="local-generation" || rights.RightsBasis is not ("owned" or "licensed")
            || string.IsNullOrWhiteSpace(rights.ApprovedBy) || rights.ApprovedBy.Length>200)
            throw new InvalidOperationException("Exact source-image rights must separately permit local generation");
        return approval;
    }
    public string Claim(SplatGenerationRequest request,string requestSha256,string catalog)
    {
        var host=workspace.HostContext!;host.ValidateFresh();_=Admit(request,catalog);
        var source=Root+"/"+request.Request.Input.Sha256;
        using var locked=workspace.Lock(source+"/ledger.lock");
        host.ValidateFresh();_=Admit(request,catalog);
        var selected=source+"/"+request.AttemptKind+".json";
        if(File.Exists(workspace.Resolve(selected)) || Directory.Exists(workspace.Resolve(selected)))
            throw new InvalidOperationException("This source candidate/revision was already claimed; no reset or automatic retry");
        string? priorHash=null;
        if(request.AttemptKind=="revision")
        {
            var candidate=source+"/candidate.json";
            var prior=SplatJson.ReadFile<SplatAttemptClaim>(workspace.Resolve(candidate),65536);
            if(prior.SchemaVersion!="bubo-splat-attempt-claim/v1" || prior.SourceSha256!=request.Request.Input.Sha256
                || prior.Kind!="candidate" || prior.ApprovalSha256!=request.Authorization.Sha256 || prior.RunId==request.RunId)
                throw new InvalidOperationException("Revision requires the original source-bound candidate and unchanged budget approval");
            priorHash=workspace.HashProjectFile(candidate);
        }
        SplatJson.WriteNew(workspace,selected,new SplatAttemptClaim {SchemaVersion="bubo-splat-attempt-claim/v1",
            SourceSha256=request.Request.Input.Sha256,Kind=request.AttemptKind,Task=request.Task,RunId=request.RunId,
            RequestSha256=requestSha256,ApprovalSha256=request.Authorization.Sha256,HostContextSha256=host.ContextSha256,
            CandidateClaimSha256=priorHash,ClaimedAt=DateTimeOffset.UtcNow});
        return selected;
    }
}
