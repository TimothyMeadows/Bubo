namespace Bubo.Schematic.Domain;

/// <summary>A confined relative payload entry, or an absolute native-job input. Bytes and raw SHA are both binding.</summary>
public sealed record AssemblyExportFile(string Path, string Sha256, long Bytes);
public sealed record AssemblyExportComponent(string InstanceId, string PartId, string Node, string Kind, string? RigNode);
public sealed record AssemblyExportCoordinates(string Source, string Target, IReadOnlyList<double> SourceToGltf);
public sealed record AssemblyExportToolchainFile(string Path, string Sha256, long Bytes);
public sealed record AssemblyExportToolchain(string BlenderPath, string BlenderSha256, string ModuleRoot,
    IReadOnlyList<AssemblyExportToolchainFile> Files, string Fingerprint);
public sealed record AssemblyExportToolIdentity(string BlenderSha256, string BlenderVersion,
    IReadOnlyList<AssemblyExportToolchainFile> GltfFiles);
public sealed record AssemblyExportIdentity(AssemblyBuildIdentity Build, string ApprovalSha256,
    string AssemblyJobSha256, string AssemblyReportSha256, string AssemblyVerificationSha256,
    string AssemblySourceSha256, string MotionJobSha256, string MotionReportSha256,
    string MotionVerificationSha256, string MotionSourceSha256);
public sealed record AssemblyExportInputs(AssemblyExportFile AssemblyJob, AssemblyExportFile AssemblyReport,
    AssemblyExportFile AssemblyVerification, AssemblyExportFile MotionJob, AssemblyExportFile MotionReport,
    AssemblyExportFile MotionVerification, AssemblyExportFile SourceAssembly, AssemblyExportFile SourceMotion,
    AssemblyExportFile? Glb, AssemblyExportFile? ProducerReport, AssemblyExportFile? Package,
    AssemblyExportFile? RestVerification, AssemblyExportFile? DiagnosticVerification);
public sealed record AssemblyExportLimits(long MaxSourceBytes, long MaxJobBytes, long MaxOutputBytes,
    int MaxVertices, int MaxTriangles, int MaxNodes, int MaxAccessors, int MaxFiles, int MaxMotionSamples,
    long MaxGeometryChecks, int MaxViews, int ImageSize, int RenderSamples,
    int MaxSkinJoints = 16384, int MaxChannels = 32768, int MaxPrimitives = 16384);

/// <summary>Data-only fixed-worker boundary. Source jobs are hash-bound references, not nested copies.</summary>
public sealed record AssemblyExportJob(string SchemaVersion, string Operation, string? Representation,
    string ProjectRoot, string Output, SortedDictionary<string, string> Bundle, AssemblyExportToolchain Toolchain,
    AssemblyExportIdentity Identity, AssemblyExportLimits Limits, AssemblyExportInputs Inputs);

/// <summary>Portable payload inventory excludes package.json and external verification/review decisions.</summary>
public sealed record AssemblyExportPackage
{
    public required string SchemaVersion { get; init; }
    public required string Task { get; init; }
    public required string Asset { get; init; }
    public required string Version { get; init; }
    public required string ModelAuthor { get; init; }
    public required AssemblyExportIdentity Ancestry { get; init; }
    public required AssemblyExportCoordinates Coordinates { get; init; }
    public required NumericalPolicy Numerics { get; init; }
    public required IReadOnlyList<AssemblyExportComponent> Components { get; init; }
    public required IReadOnlyList<AssemblyExportFile> Files { get; init; }
    public required SortedDictionary<string, string> Bundle { get; init; }
    public required AssemblyExportToolIdentity Toolchain { get; init; }
}

/// <summary>Measurements from delivered GLB. Coordinates use source Z-up metres after the fixed import conversion.</summary>
public sealed record AssemblyExportInstanceMeasurement(string InstanceId, string PartId, string Node,
    string ParentId, RigidPose LocalPose, RigidPose WorldPose, string MaterialId, Vec3 MaterialColorLinear,
    double MaterialRoughness, double MaterialMetallic, IReadOnlyList<AssemblyPortMeasurement> Ports,
    string GeometryFile, string GeometrySha256, int VertexCount, int TriangleCount,
    AssemblyExportSkinMeasurement? Skin, AssemblyExportGeometryBinding Geometry);
/// <summary>Measured raw/SDK ownership of the actual surface, including importer-created mesh wrappers.</summary>
public sealed record AssemblyExportGeometryBinding(string Node, string? ParentNode, int SourceNodeIndex,
    RigidPose LocalPose, RigidPose WorldPose, string SemanticNodeKind);
public sealed record AssemblyExportSkinMeasurement(string RigNode, RigidPose LocalPose, RigidPose WorldPose,
    IReadOnlyList<string> Joints, IReadOnlyList<string?> Parents, IReadOnlyList<IReadOnlyList<double>> InverseBindMatrices,
    IReadOnlyList<AssemblyBoneMeasurement> Bones, string WeightsFile, string WeightsSha256);
public sealed record AssemblyExportChannel(string Node, string Path, IReadOnlyList<double> Times);
public sealed record AssemblyExportSample(int Index, double TimeSeconds, IReadOnlyList<AssemblyExportInstanceMeasurement> Instances);
public sealed record AssemblyExportClip(string Id, string PortableAnimationName, IReadOnlyList<AssemblyExportChannel> Channels, IReadOnlyList<AssemblyExportSample> Samples);
public sealed record AssemblyExportMeasurement(IReadOnlyList<AssemblyExportInstanceMeasurement> Instances,
    IReadOnlyList<AssemblyExportClip> Clips, int NodeCount, int AccessorCount, int PrimitiveCount);
public sealed record AssemblyExportReport
{
    public required string SchemaVersion { get; init; }
    public required string Operation { get; init; }
    public required string? Representation { get; init; }
    public required string JobSha256 { get; init; }
    public required string BlenderVersion { get; init; }
    public required AssemblyExportIdentity Identity { get; init; }
    public required AssemblyExportToolchain Toolchain { get; init; }
    public required SortedDictionary<string, string> Bundle { get; init; }
    public required AssemblyExportLimits Limits { get; init; }
    public required AssemblyExportInputs Inputs { get; init; }
    public required IReadOnlyList<AssemblyExportFile> Outputs { get; init; }
    public required AssemblyExportMeasurement? Raw { get; init; }
    public required AssemblyExportMeasurement? Imported { get; init; }
    public required bool HumanModelAccepted { get; init; }
    public required bool RuntimeAccepted { get; init; }
}
public sealed record AssemblyExportNativeVerification(string SchemaVersion, string Representation,
    string? GlbSha256, AssemblyExportReport Report, long WorkChecks, int VerifiedPoses, long VerifiedTriangles);
public sealed record AssemblyExportReviewView(string Path, string Sha256, long Bytes, string Kind,
    string? ClipId, int? SampleIndex, double? TimeSeconds, IReadOnlyList<string> InstanceIds);
public sealed record AssemblyExportReviewClip(string ClipId, string PortableAnimationName, int SampleCount, IReadOnlyList<string> FrameFiles);
public sealed record AssemblyExportReview(string SchemaVersion, IReadOnlyList<AssemblyExportReviewView> Views,
    IReadOnlyList<AssemblyExportReviewClip> ClipCoverage, bool Complete, bool HumanModelAccepted, bool RuntimeAccepted);
