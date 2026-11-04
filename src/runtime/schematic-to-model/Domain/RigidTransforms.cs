namespace Bubo.Schematic.Domain;

/// <summary>Double precision rigid pose with column basis vectors; metres, right handed, no scale.</summary>
public sealed record RigidPose(Vec3 Origin, Vec3 X, Vec3 Y, Vec3 Z)
{
    public static RigidPose Identity => new(Vec3.Zero, new() { X = 1, Y = 0, Z = 0 }, new() { X = 0, Y = 1, Z = 0 }, new() { X = 0, Y = 0, Z = 1 });
}

public static class RigidTransforms
{
    public static Vec3 Add(Vec3 a, Vec3 b) => new() { X = a.X + b.X, Y = a.Y + b.Y, Z = a.Z + b.Z };
    public static Vec3 Subtract(Vec3 a, Vec3 b) => new() { X = a.X - b.X, Y = a.Y - b.Y, Z = a.Z - b.Z };
    public static Vec3 Scale(Vec3 a, double scale) => new() { X = a.X * scale, Y = a.Y * scale, Z = a.Z * scale };
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new() { X = a.Y * b.Z - a.Z * b.Y, Y = a.Z * b.X - a.X * b.Z, Z = a.X * b.Y - a.Y * b.X };
    public static double Length(Vec3 a) => Math.Sqrt(Dot(a, a));
    public static Vec3 Normalize(Vec3 a)
    {
        var length = Length(a);
        if (!double.IsFinite(length) || length <= 0) throw new ArgumentException("A finite nonzero direction is required");
        return Scale(a, 1 / length);
    }
    public static Vec3 Direction(RigidPose pose, Vec3 value) => Add(Add(Scale(pose.X, value.X), Scale(pose.Y, value.Y)), Scale(pose.Z, value.Z));
    public static Vec3 Point(RigidPose pose, Vec3 value) => Add(pose.Origin, Direction(pose, value));
    public static RigidPose Compose(RigidPose parent, RigidPose local) => new(Point(parent, local.Origin), Direction(parent, local.X), Direction(parent, local.Y), Direction(parent, local.Z));
    public static RigidPose Inverse(RigidPose pose)
    {
        var inverse = new RigidPose(Vec3.Zero, new() { X = pose.X.X, Y = pose.Y.X, Z = pose.Z.X }, new() { X = pose.X.Y, Y = pose.Y.Y, Z = pose.Z.Y }, new() { X = pose.X.Z, Y = pose.Y.Z, Z = pose.Z.Z });
        return inverse with { Origin = Scale(Direction(inverse, pose.Origin), -1) };
    }
    public static RigidPose FromFrame(LocalFrame frame)
    {
        var rotation = frame with { Origin = Vec3.Zero };
        var identity = RigidPose.Identity;
        return new(frame.Origin, DesignGeometry.TransformPoint(rotation, identity.X), DesignGeometry.TransformPoint(rotation, identity.Y), DesignGeometry.TransformPoint(rotation, identity.Z));
    }
    public static RigidPose AxisMotion(Vec3 axis, double degrees, double metres = 0)
    {
        // Design admission permits a small normalization tolerance. Never turn that
        // tolerance into scale or a systematic translation error in a rigid basis.
        axis = Normalize(axis);
        var radians = degrees * Math.PI / 180;
        var c = Math.Cos(radians); var s = Math.Sin(radians);
        Vec3 Rotate(Vec3 v) => Add(Add(Scale(v, c), Scale(Cross(axis, v), s)), Scale(axis, Dot(axis, v) * (1 - c)));
        var identity = RigidPose.Identity;
        return new(Scale(axis, metres), Rotate(identity.X), Rotate(identity.Y), Rotate(identity.Z));
    }
    public static double AngularErrorDegrees(RigidPose a, RigidPose b)
    {
        // atan2 remains sensitive near identity, unlike acos(trace) after rounding.
        var relative = Compose(Inverse(a), b);
        var sine = Length(new() { X = relative.Y.Z - relative.Z.Y, Y = relative.Z.X - relative.X.Z, Z = relative.X.Y - relative.Y.X }) / 2;
        var cosine = Math.Clamp((relative.X.X + relative.Y.Y + relative.Z.Z - 1) / 2, -1, 1);
        return Math.Atan2(sine, cosine) * 180 / Math.PI;
    }
}
