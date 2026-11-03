namespace Bubo.Schematic.Domain;

/// <summary>Dimension-driven mechanical asset design, not a manufacturing or circuit certification.</summary>
public sealed record AssemblyDesign
{
    public required string SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public required string StyleSha256 { get; init; }
    public required string CoordinateSystem { get; init; }
    public required NumericalPolicy Numerics { get; init; }
    public required DesignBudgets Budgets { get; init; }
    public required List<DesignObservation> Observations { get; init; }
    public required List<MaterialSpec> Materials { get; init; }
    public required List<PartDefinition> Parts { get; init; }
    public required List<Subassembly> Subassemblies { get; init; }
    public required List<PartInstance> Instances { get; init; }
    public required List<JointSpec> Joints { get; init; }
    public required List<MechanismSpec> Mechanisms { get; init; }
    public required List<RouteSpec> Routes { get; init; }
    public required List<ConnectionSpec> Connections { get; init; }
    public required List<SectionSpec> Sections { get; init; }
}

public sealed record Vec2
{
    public required double X { get; init; }
    public required double Y { get; init; }
}
public sealed record Vec3
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Z { get; init; }
    public static Vec3 Zero => new() { X = 0, Y = 0, Z = 0 };
}
/// <summary>Right-handed metres, Z up; apply X, then Y, then Z around fixed parent axes (Rz*Ry*Rx); no implicit scale.</summary>
public sealed record LocalFrame
{
    public required Vec3 Origin { get; init; }
    public required Vec3 RotationDegrees { get; init; }
    public static LocalFrame Identity => new() { Origin = Vec3.Zero, RotationDegrees = Vec3.Zero };
}
public sealed record NumericalPolicy
{
    public required double LinearTolerance { get; init; }
    public required double AngularToleranceDegrees { get; init; }
    public required double MinimumFeatureSize { get; init; }
    public required double MaxCoordinateMagnitude { get; init; }
}
public sealed record DesignBudgets
{
    public required int MaxPartDefinitions { get; init; }
    public required int MaxInstances { get; init; }
    public required int MaxFeatures { get; init; }
    public required int MaxRoutePoints { get; init; }
    public required int MaxDrawings { get; init; }
    public required long MaxValidationChecks { get; init; }
    public required long MaxPreviewBytes { get; init; }
    public required long MaxExportBytes { get; init; }
    public required int MaxGeneratedTriangles { get; init; }
    public required int MaxMotionSamples { get; init; }
}
public enum ObservationKind { Observed, Assumed, Unresolved, Simplified }
public sealed record DesignObservation
{
    public required string Id { get; init; }
    public required ObservationKind Kind { get; init; }
    public required string Description { get; init; }
    public required string Source { get; init; }
}
public sealed record MaterialSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Color { get; init; }
    public required double Roughness { get; init; }
    public required double Metallic { get; init; }
}
public enum PartKind { Rigid, Wire, Hose, Spring, Belt }
public enum FeatureKind { Box, Cylinder, Extrude, Revolve, Sweep, Loft }
public enum FeatureOperation { Add, Subtract }
public sealed record LoftSection
{
    public required LocalFrame Frame { get; init; }
    public required List<Vec2> Profile { get; init; }
}
/// <summary>Closed parameter sets per kind; data only, never executable expressions.</summary>
public sealed record ShapeFeature
{
    public required string Id { get; init; }
    public required FeatureKind Kind { get; init; }
    public required FeatureOperation Operation { get; init; }
    public required LocalFrame Frame { get; init; }
    public required Dictionary<string, double> Parameters { get; init; }
    public required List<Vec2> Profile { get; init; }
    public required List<Vec3> Path { get; init; }
    public required List<LoftSection> Sections { get; init; }
}
public sealed record AttachmentPort
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required LocalFrame Frame { get; init; }
}
public enum TerminalKind { Power, Signal, Ground }
public sealed record TerminalSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required TerminalKind Kind { get; init; }
    public required LocalFrame Frame { get; init; }
}
public sealed record FlexibleLimits
{
    public required double RestLength { get; init; }
    public required double MinimumLength { get; init; }
    public required double MaximumLength { get; init; }
    public required double MinimumBendRadius { get; init; }
    public required double MaximumStretchRatio { get; init; }
    public required int SkinSegments { get; init; }
}
public sealed record PartDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public required string Detail { get; init; }
    public required string MaterialId { get; init; }
    public required PartKind Kind { get; init; }
    /// <summary>Declared local envelope from zero to Size, metres; checked against actual geometry later.</summary>
    public required Vec3 Size { get; init; }
    public required List<ShapeFeature> Features { get; init; }
    public required List<AttachmentPort> Interfaces { get; init; }
    public required List<TerminalSpec> Terminals { get; init; }
    public required FlexibleLimits? Flexible { get; init; }
}
public sealed record Subassembly
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string? ParentId { get; init; }
    public required LocalFrame Frame { get; init; }
}
public sealed record PartInstance
{
    public required string Id { get; init; }
    public required string PartId { get; init; }
    public required string ParentId { get; init; }
    public required LocalFrame Frame { get; init; }
    public required Vec3 ExplodeOffset { get; init; }
}
public enum JointKind { Fixed, Hinge, Rotary, Slider, Screw }
public sealed record JointSpec
{
    public required string Id { get; init; }
    public required JointKind Kind { get; init; }
    public required string ParentInstanceId { get; init; }
    public required string ChildInstanceId { get; init; }
    public required string ParentInterfaceId { get; init; }
    public required string ChildInterfaceId { get; init; }
    public required Vec3 Axis { get; init; }
    public required double Minimum { get; init; }
    public required double Maximum { get; init; }
    public required double Rest { get; init; }
    public required double Pitch { get; init; }
    public required double Clearance { get; init; }
}
public enum MechanismKind { SpurGears, RackAndPinion, PulleyBelt, SliderCrank, FourBar }
public sealed record MechanismSpec
{
    public required string Id { get; init; }
    public required MechanismKind Kind { get; init; }
    /// <summary>Ordered driver then followers; solver inputs are fixed per mechanism family.</summary>
    public required List<string> JointIds { get; init; }
    public required Dictionary<string, double> Parameters { get; init; }
    public required string Branch { get; init; }
}
public sealed record Endpoint
{
    public required string InstanceId { get; init; }
    public required string PortId { get; init; }
}
public sealed record RouteSpec
{
    public required string Id { get; init; }
    public required string InstanceId { get; init; }
    public required List<Vec3> Points { get; init; }
    public required bool Closed { get; init; }
    public required Endpoint? From { get; init; }
    public required Endpoint? To { get; init; }
}
public enum ConnectionKind { Power, Signal, Ground, Mechanical }
public sealed record ConnectionSpec
{
    public required string Id { get; init; }
    public required ConnectionKind Kind { get; init; }
    public required Endpoint From { get; init; }
    public required Endpoint To { get; init; }
    public required string RouteId { get; init; }
}
public enum SectionAxis { X, Y, Z }
public sealed record SectionSpec
{
    public required string Id { get; init; }
    public required SectionAxis Axis { get; init; }
    public required double Offset { get; init; }
}
public sealed record ValidationIssue(string Code, string Path, string Message);
public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues, long Checks)
{
    public bool IsValid => Issues.Count == 0;
}
public interface IAssemblyDesignValidator
{
    ValidationReport Validate(AssemblyDesign design, CancellationToken cancellationToken = default);
    void RequireValid(AssemblyDesign design, CancellationToken cancellationToken = default);
}
public sealed class DesignValidationException(ValidationReport report)
    : ArgumentException($"Assembly design has {report.Issues.Count} validation issue(s).")
{
    public ValidationReport Report { get; } = report;
}
