namespace Bubo.Schematic.Domain;

/// <summary>Explicit v2 construction/readiness choices; every field is part of whole-design approval.</summary>
public sealed record AssemblyProfile
{
    public required string SchemaVersion { get; init; }
    public required int SamplesPerRange { get; init; }
    public required int SamplesPerCycle { get; init; }
    public required long MaxGeometryChecks { get; init; }
    public required double MinimumSeparation { get; init; }
    public required List<ContactZone> Contacts { get; init; }
    public required List<FlexibleRigPolicy> FlexibleRigs { get; init; }
    public required List<CouplingFit> CouplingFits { get; init; }
}
/// <summary>Only contact within this explicitly approved first-instance-local closed box is permitted.</summary>
public sealed record ContactZone
{
    public required string Id { get; init; }
    public required string FirstInstanceId { get; init; }
    public required string SecondInstanceId { get; init; }
    public required LocalFrame Frame { get; init; }
    public required Vec3 Size { get; init; }
    public required string Reason { get; init; }
}
public sealed record FlexibleRigPolicy
{
    public required string RouteId { get; init; }
    public required double MinimumSegmentRatio { get; init; }
    public required double? MinimumEndpointDistance { get; init; }
    public required double? MaximumEndpointDistance { get; init; }
    /// <summary>Closed belts name a rotary driver and explicit positive metres per driver degree; open routes use owned endpoints.</summary>
    public required string? DriverJointId { get; init; }
    public required double? TravelMetresPerDegree { get; init; }
}
/// <summary>Declared pitch geometry validates fit/ratio. Teeth detail and sampled solid clearance remain separate evidence.</summary>
public sealed record CouplingFit
{
    public required string MechanismId { get; init; }
    public required double DriverPitchRadius { get; init; }
    public required double? FollowerPitchRadius { get; init; }
    public required int? DriverToothCount { get; init; }
    public required int? FollowerToothCount { get; init; }
}
