namespace Bubo.Schematic.Domain;

public sealed record AssemblyBuildIdentity(string DesignSha256, string DesignFingerprint, string RuntimeBuildSha256,
    string ToolSha256, string PartsJobSha256, string PartsReportSha256, string PartsVerificationSha256);
public sealed record AssemblyBuildLimits(int MaxVertices, int MaxTriangles, long MaxOutputBytes, int MaxJobBytes,
    int MaxFiles, int MaxMotionSamples, long MaxWorkChecks);
public sealed record AssemblyBuildJob
{
    public required string SchemaVersion { get; init; }
    public required string Operation { get; init; }
    public required string ProjectRoot { get; init; }
    public required string Output { get; init; }
    public required AssemblyDesign Design { get; init; }
    public required string PartsDirectory { get; init; }
    public required string PartsReportSha256 { get; init; }
    public required string PartsVerificationSha256 { get; init; }
    public required PartsReport Parts { get; init; }
    public required AssemblyBuildIdentity Identity { get; init; }
    public required SortedDictionary<string, string> Bundle { get; init; }
    public required AssemblyBuildLimits Limits { get; init; }
    public required DiagnosticPlan Plan { get; init; }
    public required IReadOnlyList<FlexibleRigProgram> Rigs { get; init; }
    public required string? SourceAssembly { get; init; }
    public required string? SourceAssemblySha256 { get; init; }
}
public sealed record AssemblyPortMeasurement(string PortId, string Kind, string ObjectName, RigidPose LocalPose, RigidPose WorldPose);
public sealed record AssemblyBoneMeasurement(string Name, string? ParentName, RigidPose RestPose, RigidPose Pose, double Length);
public sealed record AssemblyRigMeasurement(string ArmatureName, RigidPose ArmatureLocalPose, RigidPose ArmatureWorldPose,
    IReadOnlyList<AssemblyBoneMeasurement> Bones, IReadOnlyList<IReadOnlyList<int>> RingVertexIndices, string WeightsSha256,
    IReadOnlyList<Vec3> RingCenters, IReadOnlyList<double> SegmentLengths, IReadOnlyList<double> BendAnglesDegrees);
public sealed record AssemblyInstanceMeasurement
{
    public required string InstanceId { get; init; }
    public required string PartId { get; init; }
    public required string ObjectName { get; init; }
    public required string ParentId { get; init; }
    public required RigidPose LocalPose { get; init; }
    public required RigidPose WorldPose { get; init; }
    public required string MaterialId { get; init; }
    public required Vec3 MaterialColorLinear { get; init; }
    public required double MaterialRoughness { get; init; }
    public required double MaterialMetallic { get; init; }
    public required IReadOnlyList<AssemblyPortMeasurement> Ports { get; init; }
    public required string PrototypeBlendSha256 { get; init; }
    public required string PrototypeGeometrySha256 { get; init; }
    public required string GeometryFile { get; init; }
    public required string GeometrySha256 { get; init; }
    public required int VertexCount { get; init; }
    public required int TriangleCount { get; init; }
    public required AssemblyRigMeasurement? Rig { get; init; }
}
public sealed record AssemblyActionMeasurement(string OwnerId, string OwnerName, string ActionName, int FcurveCount, int KeyframeCount);
public sealed record AssemblySampleMeasurement(int Index, double TimeSeconds, IReadOnlyList<AssemblyInstanceMeasurement> Instances);
public sealed record AssemblyClipMeasurement(string Id, IReadOnlyList<AssemblyActionMeasurement> Actions, IReadOnlyList<AssemblySampleMeasurement> Samples);
public sealed record AssemblyBuildReport
{
    public required string SchemaVersion { get; init; }
    public required string JobSha256 { get; init; }
    public required AssemblyBuildIdentity Identity { get; init; }
    public required string BlenderVersion { get; init; }
    public required string Operation { get; init; }
    public required string SourceFile { get; init; }
    public required string SourceSha256 { get; init; }
    public required IReadOnlyList<AssemblyInstanceMeasurement> Instances { get; init; }
    public required IReadOnlyList<AssemblyClipMeasurement> Clips { get; init; }
    public required long TotalVertices { get; init; }
    public required long TotalTriangles { get; init; }
    public required long TotalBytes { get; init; }
}
public sealed record AssemblyFlexibleMeasurement(string InstanceId, double ArcLength, double MinimumSegmentRatio,
    double MaximumSegmentRatio, double? MinimumBendRadius, double? EndpointDistance);
public sealed record AssemblyPoseVerification(string ClipId, int SampleIndex, ClearanceReport Clearance,
    IReadOnlyList<AssemblyFlexibleMeasurement> Flexible);
public sealed record AssemblyNativeVerification(AssemblyBuildReport Report, IReadOnlyList<AssemblyPoseVerification> Poses, long WorkChecks);
