namespace Bubo.Schematic.Domain;

/// <summary>Double-precision design-space transforms; these do not certify constructed solid geometry.</summary>
public static class DesignGeometry
{
    public static Vec3 TransformPoint(LocalFrame frame, Vec3 point)
    {
        const double factor = Math.PI / 180;
        var x = frame.RotationDegrees.X * factor; var y = frame.RotationDegrees.Y * factor; var z = frame.RotationDegrees.Z * factor;
        var sx = Math.Sin(x); var cx = Math.Cos(x); var sy = Math.Sin(y); var cy = Math.Cos(y); var sz = Math.Sin(z); var cz = Math.Cos(z);
        var ax = point.X; var ay = point.Y * cx - point.Z * sx; var az = point.Y * sx + point.Z * cx;
        var bx = ax * cy + az * sy; var by = ay; var bz = -ax * sy + az * cy;
        return new() { X = bx * cz - by * sz + frame.Origin.X, Y = bx * sz + by * cz + frame.Origin.Y, Z = bz + frame.Origin.Z };
    }

    public static Vec3 WorldPoint(AssemblyDesign design, string nodeId, Vec3 local)
        => CreateWorldPointResolver(design)(nodeId, local);

    /// <summary>One immutable lookup/cache per validated render or validation call.</summary>
    public static Func<string, Vec3, Vec3> CreateWorldPointResolver(AssemblyDesign design, Action? step = null)
    {
        var nodes = new Dictionary<string, (LocalFrame Frame, string? Parent)>(StringComparer.Ordinal);
        foreach (var group in design.Subassemblies) { step?.Invoke(); nodes.Add(group.Id,(group.Frame, group.ParentId)); }
        foreach (var instance in design.Instances) { step?.Invoke(); nodes.Add(instance.Id,(instance.Frame, instance.ParentId)); }
        var resolved = new Dictionary<string, Basis>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        Basis Resolve(string id)
        {
            step?.Invoke();
            if(resolved.TryGetValue(id,out var cached)) return cached;
            if (!active.Add(id) || active.Count > 256) throw new ArgumentException("Invalid or excessive assembly hierarchy");
            if(!nodes.TryGetValue(id,out var node)) throw new ArgumentException("Unknown assembly parent: "+id);
            var parent = node.Parent is null ? Basis.Identity : Resolve(node.Parent);
            var origin = parent.Point(node.Frame.Origin);
            Vec3 Axis(Vec3 unit)
            {
                return parent.Direction(TransformPoint(node.Frame with {Origin=Vec3.Zero},unit));
            }
            var result=new Basis(origin,Axis(new(){X=1,Y=0,Z=0}),Axis(new(){X=0,Y=1,Z=0}),Axis(new(){X=0,Y=0,Z=1}));
            active.Remove(id); resolved.Add(id,result); return result;
        }
        return (id,point) => Resolve(id).Point(point);
    }

    sealed record Basis(Vec3 Origin, Vec3 X, Vec3 Y, Vec3 Z)
    {
        public static Basis Identity => new(Vec3.Zero,new(){X=1,Y=0,Z=0},new(){X=0,Y=1,Z=0},new(){X=0,Y=0,Z=1});
        public Vec3 Direction(Vec3 point) => new(){X=X.X*point.X+Y.X*point.Y+Z.X*point.Z,Y=X.Y*point.X+Y.Y*point.Y+Z.Y*point.Z,Z=X.Z*point.X+Y.Z*point.Y+Z.Z*point.Z};
        public Vec3 Point(Vec3 point) {var d=Direction(point);return new(){X=d.X+Origin.X,Y=d.Y+Origin.Y,Z=d.Z+Origin.Z};}
    }

    public static (Vec3 Min, Vec3 Max) Bounds(AssemblyDesign design, string instanceId)
    {
        var instance = design.Instances.Single(x => x.Id == instanceId);
        var size = design.Parts.Single(x => x.Id == instance.PartId).Size;
        var corners = Corners(Vec3.Zero, size).Select(p => WorldPoint(design, instanceId, p)).ToArray();
        return Enclose(corners);
    }

    public static IEnumerable<Vec3> Corners(Vec3 min, Vec3 max)
    {
        foreach (var x in new[] { min.X, max.X }) foreach (var y in new[] { min.Y, max.Y }) foreach (var z in new[] { min.Z, max.Z })
            yield return new() { X = x, Y = y, Z = z };
    }

    public static (Vec3 Min, Vec3 Max) Enclose(IEnumerable<Vec3> points)
    {
        var list = points.ToArray();
        if (list.Length == 0) throw new ArgumentException("A declared envelope requires points");
        return (new() { X = list.Min(p => p.X), Y = list.Min(p => p.Y), Z = list.Min(p => p.Z) },
            new() { X = list.Max(p => p.X), Y = list.Max(p => p.Y), Z = list.Max(p => p.Z) });
    }

    public static double Length(Vec3 from, Vec3 to)
    {
        var x = to.X - from.X; var y = to.Y - from.Y; var z = to.Z - from.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }
}
