using System.Text.Json;
using System.Text.Json.Serialization;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;

namespace Bubo.Media.Application;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptanceBudgetApproval(string SchemaVersion,string SharedStateRoot,string[] Cases,int MaximumCandidates,int MaximumRevisions);

public sealed record AcceptanceClaim(string SchemaVersion,string Case,string Kind,string RequestId,string RequestSha256,string BudgetApprovalSha256,
    string HostContextSha256,string? CandidateClaimSha256,DateTimeOffset ClaimedAt);

/// <summary>Immutable candidate/revision claims; shared state is bound by an explicit policy.</summary>
public sealed class AcceptanceLedger(IWorkspace workspace)
{
    public string? Claim(AdmittedMediaRequest admitted)
    {
        if (admitted.Request.Acceptance is not { } attempt) return null;
        admitted.Request.Validate(MediaCapabilities.Require(admitted.Request.Backend));
        var host=workspace.HostContext ?? throw new InvalidOperationException("Host context required");
        if (!host.HasExplicitMediaStateRoot) throw new InvalidOperationException("Acceptance requires explicitly shared state; per-task defaults cannot reset the budget");
        host.ValidateFresh();host.RequireReference(attempt.Approval.Path,"policy");
        if (workspace.HashProjectFile(attempt.Approval.Path)!=attempt.Approval.Sha256)
            throw new InvalidOperationException("Acceptance policy changed");
        var policy=MediaJson.Read<AcceptanceBudgetApproval>(MediaAdmission.ReadBounded(host.ResolveReadPath(attempt.Approval.Path)));
        if (policy.SchemaVersion!="bubo-media-acceptance-budget/v1" || policy.SharedStateRoot!=host.MediaStateRoot
            || policy.MaximumCandidates!=1 || policy.MaximumRevisions!=1 || policy.Cases is null
            || !policy.Cases.Order(StringComparer.Ordinal).SequenceEqual(AcceptanceAttempt.Cases.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Shared six-case policy must grant exactly one candidate and at most one revision; no reset or implicit increase");
        var root=host.MediaStateRoot+"/acceptance";
        var lockPath=root+"/ledger.lock";
        host.EnsureWriteAllowed(lockPath);
        using var locked=workspace.Lock(lockPath);
        host.ValidateFresh();
        var candidate=root+"/"+attempt.Case+"/candidate.json";
        var revision=root+"/"+attempt.Case+"/revision.json";
        var selected=attempt.Kind=="candidate"?candidate:revision;
        string? priorHash=null;
        if (attempt.Kind=="revision")
        {
            if (!File.Exists(workspace.Resolve(candidate)))
                throw new InvalidOperationException("A revision cannot precede its original candidate");
            var prior=MediaJson.Read<AcceptanceClaim>(MediaAdmission.ReadBounded(host.ResolveReadPath(candidate)));
            if (prior.SchemaVersion!="bubo-media-acceptance-claim/v1" || prior.Case!=attempt.Case || prior.Kind!="candidate"
                || prior.BudgetApprovalSha256!=attempt.Approval.Sha256 || prior.RequestId==admitted.Request.RequestId)
                throw new InvalidOperationException("Candidate ancestry or shared budget approval changed");
            priorHash=workspace.HashProjectFile(candidate);
        }
        if (File.Exists(workspace.Resolve(selected)))
            throw new InvalidOperationException("This acceptance candidate/revision was already claimed; failed attempts are not reset");
        var full=workspace.ResolveWrite(selected);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        full=workspace.ResolveWrite(selected);
        var claim=new AcceptanceClaim("bubo-media-acceptance-claim/v1",attempt.Case,attempt.Kind,admitted.Request.RequestId,
            admitted.RequestSha256,attempt.Approval.Sha256,host.ContextSha256,priorHash,DateTimeOffset.UtcNow);
        using var output=new FileStream(full,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(output,claim,MediaJson.Options);output.Flush(true);
        return selected;
    }
}
