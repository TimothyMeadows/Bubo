namespace Bubo.Splats.Domain;

public sealed record SplatGenerationSettings
{
    public required int Steps { get; init; }
    public required double Guidance { get; init; }
    public required double Shift { get; init; }
    public required int ErodeRadius { get; init; }
}
public sealed record SplatGenerationLimits
{
    public required long MaxInputPixels { get; init; }
    public required int MaxInputEdge { get; init; }
    public required double MaxAspectRatio { get; init; }
    public required long MaxOutputBytes { get; init; }
    public required long MaxGpuAllocatedBytes { get; init; }
    public required long MinGpuFreeBytes { get; init; }
}
public sealed record SplatGenerationRequest
{
    public required string SchemaVersion { get; init; }
    public required string Task { get; init; }
    public required string RunId { get; init; }
    public required string AttemptKind { get; init; }
    public required SplatRequest Request { get; init; }
    public required SplatSource Installation { get; init; }
    public required SplatSource Authorization { get; init; }
    public required SplatGenerationSettings Settings { get; init; }
    public required SplatGenerationLimits Limits { get; init; }
}
public sealed record SplatGenerationApproval
{
    public required string SchemaVersion { get; init; }
    public required string Task { get; init; }
    public required string LedgerRoot { get; init; }
    public required string SourceSha256 { get; init; }
    public required string StyleSha256 { get; init; }
    public required string CatalogSha256 { get; init; }
    public required int MaximumCandidates { get; init; }
    public required int MaximumRevisions { get; init; }
    public required bool GenerationApproved { get; init; }
    public required bool GpuAvailabilityConfirmed { get; init; }
    public required string ApprovedBy { get; init; }
    public required SplatSource SourceRights { get; init; }
}
public sealed record SplatSourceRights
{
    public required string SchemaVersion {get;init;}
    public required string SourceSha256 {get;init;}
    public required string Scope {get;init;}
    public required string RightsBasis {get;init;}
    public required string ApprovedBy {get;init;}
}
public static class SplatGenerationRules
{
    public static void Validate(SplatGenerationRequest value)
    {
        if(value is null || value.SchemaVersion!="bubo-splat-generation/v1" || value.Request is null || value.Settings is null || value.Limits is null)
            throw new InvalidOperationException("Complete explicit splat generation request required");
        SplatRules.ValidateRequest(value.Request);SplatRules.ValidateSource(value.Installation);SplatRules.ValidateSource(value.Authorization);
        Id(value.Task);Id(value.RunId);
        if(value.AttemptKind is not ("candidate" or "revision"))throw new InvalidOperationException("Explicit candidate/revision required");
        var settings=value.Settings;
        if(settings.Steps is <10 or >20 || !double.IsFinite(settings.Guidance) || settings.Guidance is <1 or >10
            || !double.IsFinite(settings.Shift) || settings.Shift is <1 or >5 || settings.ErodeRadius is <0 or >8)
            throw new InvalidOperationException("Unsupported TripoSplat sampling settings");
        var limits=value.Limits;
        if(limits.MaxInputPixels is <1048576 or >16777216 || limits.MaxInputEdge is <1024 or >8192
            || !double.IsFinite(limits.MaxAspectRatio) || limits.MaxAspectRatio is <1 or >4
            || limits.MaxOutputBytes is <1024 or >134217728 || limits.MaxGpuAllocatedBytes is <1073741824 or >68719476736
            || limits.MinGpuFreeBytes is <268435456 or >68719476736
            || value.Request.GaussianCounts.Sum(x=>(long)x*68+65536)>limits.MaxOutputBytes)
            throw new InvalidOperationException("Generation limits cannot admit the selected bounded profiles");
        if(value.Request.Budgets.MaxProcessMemoryBytes>68719476736)
            throw new InvalidOperationException("Native owned-job committed-memory ceiling supports at most64GiB");
    }
    public static void ValidateApproval(SplatGenerationApproval approval,SplatGenerationRequest request,string catalog,string ledger)
    {
        if(approval is not null)SplatRules.ValidateSource(approval.SourceRights);
        if(approval is null || approval.SchemaVersion!="bubo-splat-generation-approval/v1" || approval.Task!=request.Task
            || approval.LedgerRoot!=ledger || approval.SourceSha256!=request.Request.Input.Sha256
            || approval.StyleSha256!=request.Request.StyleSha256 || approval.CatalogSha256!=catalog
            || approval.MaximumCandidates!=1 || approval.MaximumRevisions!=1 || !approval.GenerationApproved
            || !approval.GpuAvailabilityConfirmed || string.IsNullOrWhiteSpace(approval.ApprovedBy) || approval.ApprovedBy.Length>200)
            throw new InvalidOperationException("Exact source/style/catalog approval, shared ledger and confirmed GPU availability required");
    }
    static void Id(string value)
    {
        if(string.IsNullOrEmpty(value) || value.Length>80 || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(x=>!char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_' or '.')) || value is "." or "..")
            throw new InvalidOperationException("Bounded task/run identity required");
    }
}
