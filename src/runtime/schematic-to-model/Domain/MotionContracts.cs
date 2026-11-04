namespace Bubo.Schematic.Domain;

/// <summary>Measured residual against the joint's rest-relative interface transform.</summary>
public sealed record JointClosureMetric(string JointId, double LinearError, double AngularErrorDegrees);
public sealed record AssemblyPose(IReadOnlyDictionary<string, double> JointValues, IReadOnlyDictionary<string, RigidPose> Instances, IReadOnlyList<JointClosureMetric> Closures);
public sealed record DiagnosticSample(double TimeSeconds, AssemblyPose Pose);
/// <summary>Normalized one-second diagnostic travel, with no speed, physics or finished-animation claim.</summary>
public sealed record DiagnosticClip(string Id, string DriverJointId, string? MechanismId, bool CompleteCycle, IReadOnlyList<DiagnosticSample> Samples);
public sealed record MotionDriverCoverage(string DriverJointId, bool CycleApplicable, bool CompleteCycleSampled, string CycleStatus);
/// <summary>Requested counts are minima. Actual angular samples also bound each joint-coordinate step to 45 degrees.</summary>
public sealed record MotionSamplingCoverage(int SamplesPerRange, int SamplesPerCycle, int TotalSamples, IReadOnlyList<MotionDriverCoverage> Drivers);
public sealed record DiagnosticPlan(string SchemaVersion, AssemblyPose Rest, IReadOnlyList<DiagnosticClip> Clips, MotionSamplingCoverage Sampling, long WorkChecks);

/// <summary>Rejected authored kinematics, travel, closure or finite work bounds.</summary>
public sealed class AssemblyMotionException(string message) : ArgumentException(message);
