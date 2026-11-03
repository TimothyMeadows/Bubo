using System.Text.Json.Nodes;
using Bubo.FloorPlan.Domain;

namespace Bubo.FloorPlan.Infrastructure;

internal sealed record ExportNativeFile(string Path, string Sha256, long Bytes);
internal sealed record ExportNativeInputs(ExportNativeFile? SourceBlend, ExportNativeFile? ConstructionJob,
    ExportNativeFile? ConstructionReport, ExportNativeFile? Plan, ExportNativeFile? Package,
    ExportNativeFile? Glb, ExportNativeFile? VisualVerification, ExportNativeFile? CollisionVerification);
internal sealed record ExportNativeIdentities(string PlanSha256, string DesignFingerprint, string ApprovalSha256,
    string RuntimeSha256, string ConstructionJobSha256, string ConstructionReportSha256, string SourceSha256,
    string? PackageSha256);
internal sealed record ExportNativeLimits(long MaxSourceBytes, long MaxJobBytes, long MaxOutputBytes,
    int MaxVertices, int MaxTriangles, int MaxNodes, int MaxMeshes, int MaxMaterials, int MaxAccessors,
    int MaxPrimitives, long MaxGeometryChecks, int MaxViews, int ImageSize);
internal sealed record ExportNativeJob(string SchemaVersion, string Operation, string WorkspaceRoot,
    string OutputDirectory, SortedDictionary<string, string> Bundle, GltfToolchain Toolchain,
    ExportNativeIdentities Identities, ExportNativeLimits Limits, ExportNativeInputs Inputs);

internal sealed record ExportNativeOutput(string Path, string Sha256, long Bytes);
internal sealed record ExportNativeReport
{
    public required string SchemaVersion { get; init; }
    public required string Operation { get; init; }
    public required string JobSha256 { get; init; }
    public required ExportNativeIdentities Identities { get; init; }
    public required SortedDictionary<string, string> Bundle { get; init; }
    public required GltfToolchain Toolchain { get; init; }
    public required ExportNativeInputs Inputs { get; init; }
    public required ExportNativeLimits Limits { get; init; }
    public required List<ExportNativeOutput> Outputs { get; init; }
    public required JsonObject Measured { get; init; }
    public required bool HumanModelAccepted { get; init; }
    public required bool RuntimeAccepted { get; init; }
    public required string BlenderVersion { get; init; }
    public string? Representation { get; init; }
}

internal sealed record ExportAttempt
{
    public required string Directory { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string ModelAuthor { get; init; }
    public required string ConstructionDirectory { get; init; }
    public required ExportNativeIdentities Identities { get; init; }
    public required SortedDictionary<string, string> Bundle { get; init; }
    public required GltfToolchain Toolchain { get; init; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? PackageSha256 { get; set; }
    public string? ProducerReportSha256 { get; set; }
    public string? VisualVerificationSha256 { get; set; }
    public string? CollisionVerificationSha256 { get; set; }
    public SortedDictionary<string, string> Jobs { get; init; } = new(StringComparer.Ordinal);
}

internal sealed record ExportViewAttempt
{
    public required string Directory { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string ExportDirectory { get; init; }
    public required string PackageSha256 { get; init; }
    public required string VisualVerificationSha256 { get; init; }
    public required string CollisionVerificationSha256 { get; init; }
    public required SortedDictionary<string, string> Bundle { get; init; }
    public required GltfToolchain Toolchain { get; init; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? JobSha256 { get; set; }
    public string? ViewsSha256 { get; set; }
    public List<ExportFileEntry> Artifacts { get; set; } = [];
}

internal sealed record ExportModelReviewRecord(string Kind, string Decision, string Reviewer, string Role,
    bool Human, string Evidence, string EvidenceSha256, string SourceEvidence, string PackageSha256,
    string ViewsSha256, string? IndependentReviewSha256, DateTimeOffset RecordedAt);

internal sealed record FloorPlanExportState
{
    public string SchemaVersion { get; init; } = "bubo-floor-plan-export-state/v1";
    public required string Task { get; init; }
    public required string Asset { get; init; }
    public required string Version { get; init; }
    public required string DesignManifest { get; init; }
    public required string RuntimeSha256 { get; init; }
    public List<ExportAttempt> Exports { get; init; } = [];
    public List<ExportViewAttempt> Views { get; init; } = [];
    public List<ExportModelReviewRecord> Reviews { get; init; } = [];
}
