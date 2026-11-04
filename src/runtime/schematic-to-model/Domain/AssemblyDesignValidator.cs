using System.Text.RegularExpressions;

namespace Bubo.Schematic.Domain;

/// <summary>Bounded declaration checks. Solid interference and sampled motion require later native stages.</summary>
public sealed class AssemblyDesignValidator : IAssemblyDesignValidator
{
    public ValidationReport Validate(AssemblyDesign design, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>(); var invalidCodes = new HashSet<string>(StringComparer.Ordinal); long checks = 0; long maximumChecks = 10_000_000;
        void Check() { cancellationToken.ThrowIfCancellationRequested(); if (++checks > maximumChecks) throw new WorkLimitException(); }
        void Error(string code, string path, string message) { invalidCodes.Add(code); if (issues.Count < 512) issues.Add(new(code, path, message)); }
        bool Test(bool condition, string code, string path, string message) { Check(); if (!condition) Error(code, path, message); return condition; }
        bool Text(string? value, string path) => Test(value is not null && value.Length is >= 1 and <= 2048 && !value.Any(char.IsControl), "text", path, "Explicit bounded printable text is required");
        bool Identifier(string? value, string path) => Test(value is not null && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$", RegexOptions.CultureInvariant) && value != "..", "identity", path, "Use a stable filesystem-safe identity");
        bool Finite(double value, string path) => Test(double.IsFinite(value), "finite", path, "A finite numeric value is required");
        bool Positive(double value, string path) => Test(double.IsFinite(value) && value > 0, "dimension", path, "A finite positive dimension is required");
        bool EnumValue<T>(T value, string path) where T : struct, Enum => Test(Enum.IsDefined(value), "enum", path, "Unknown enum value");
        List<T> List<T>(List<T>? value, string path, int cap)
        {
            if (!Test(value is not null && value.Count <= cap, "collection", path, "Missing or excessive collection")) return [];
            if (!Test(value!.All(x => x is not null), "null", path, "Null collection members are not permitted")) return value!.Where(x => x is not null).ToList();
            return value!;
        }
        bool Point(Vec3? point, string path, double bound)
        {
            if (!Test(point is not null, "point", path, "Explicit coordinates are required")) return false;
            return Test(double.IsFinite(point!.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z) && Math.Max(Math.Abs(point.X), Math.Max(Math.Abs(point.Y), Math.Abs(point.Z))) <= bound,
                "coordinate", path, "Coordinates must be finite and inside the declared numerical range");
        }
        bool Frame(LocalFrame? frame, string path, double bound)
        {
            if (!Test(frame is not null, "frame", path, "An explicit local frame is required")) return false;
            return Point(frame!.Origin, path + ".origin", bound) & Point(frame.RotationDegrees, path + ".rotationDegrees", 360_000);
        }
        try
        {
            Check();
            if (design is null) { Error("null", "$", "An assembly design is required"); return new(issues, checks); }
            Test(design.SchemaVersion is "bubo-assembly-design/v1" or "bubo-assembly-design/v2", "schema", "schemaVersion", "Expected bubo-assembly-design/v1 or explicitly authored v2");
            Identifier(design.Id, "id"); Text(design.Name, "name"); Text(design.Purpose, "purpose");
            Test(design.StyleSha256 is not null && Regex.IsMatch(design.StyleSha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant), "style", "styleSha256", "Exact selected host style SHA-256 is required");
            Test(design.CoordinateSystem == "xy-metres-z-up", "coordinates", "coordinateSystem", "Use shared right-handed XY metres and Z up");
            if (!Test(design.Numerics is not null && design.Budgets is not null, "required", "$", "Numerics and budgets are required")) return new(issues, checks);
            var n = design.Numerics!; var b = design.Budgets!;
            Positive(n.LinearTolerance, "numerics.linearTolerance"); Positive(n.AngularToleranceDegrees, "numerics.angularToleranceDegrees");
            Positive(n.MinimumFeatureSize, "numerics.minimumFeatureSize"); Positive(n.MaxCoordinateMagnitude, "numerics.maxCoordinateMagnitude");
            Test(n.LinearTolerance < n.MinimumFeatureSize && n.MinimumFeatureSize <= n.MaxCoordinateMagnitude && n.MaxCoordinateMagnitude <= 1_000_000 && n.AngularToleranceDegrees <= 1,
                "numerics", "numerics", "Tolerance must resolve the minimum feature within bounded coordinates; angular tolerance is at most one degree");
            var bound = double.IsFinite(n.MaxCoordinateMagnitude) && n.MaxCoordinateMagnitude > 0 ? Math.Min(n.MaxCoordinateMagnitude, 1_000_000) : 1;
            var tolerance = double.IsFinite(n.LinearTolerance) && n.LinearTolerance > 0 ? n.LinearTolerance : 1e-9;
            Test(b.MaxPartDefinitions is >= 1 and <= 4096 && b.MaxInstances is >= 1 and <= 32768 && b.MaxFeatures is >= 1 and <= 131072 && b.MaxRoutePoints is >= 2 and <= 262144
                && b.MaxDrawings is >= 1 and <= 8192 && b.MaxValidationChecks is >= 1 and <= 10_000_000 && b.MaxPreviewBytes is >= 1 and <= 67_108_864
                && b.MaxExportBytes is >= 1 and <= 17_179_869_184 && b.MaxGeneratedTriangles is >= 1 and <= 50_000_000 && b.MaxMotionSamples is >= 2 and <= 1_000_000,
                "budget", "budgets", "Every budget must be positive and within the native hard admission bounds");
            maximumChecks = Math.Clamp(b.MaxValidationChecks, 1, 10_000_000);
            var observations = List(design.Observations, "observations", 4096); var materials = List(design.Materials, "materials", 4096);
            var parts = List(design.Parts, "parts", 4096); var groups = List(design.Subassemblies, "subassemblies", 32768); var instances = List(design.Instances, "instances", 32768);
            var joints = List(design.Joints, "joints", 32768); var mechanisms = List(design.Mechanisms, "mechanisms", 32768);
            var routes = List(design.Routes, "routes", 32768); var connections = List(design.Connections, "connections", 32768); var sections = List(design.Sections, "sections", 128);
            Test(parts.Count > 0 && parts.Count <= b.MaxPartDefinitions && instances.Count > 0 && instances.Count <= b.MaxInstances, "budget", "parts", "Nonempty part inventory must fit its declared budgets");
            Test(materials.Count > 0 && sections.Count > 0 && observations.Count > 0, "required", "$", "Materials, internal sections and explicit design observations are required");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Id(string? id, string path) { if (Identifier(id, path)) Test(ids.Add(id!), "duplicate", path, "IDs must be globally unique, including case-insensitive filesystems"); }
            foreach (var o in observations) { Id(o.Id, "observations.id"); EnumValue(o.Kind, "observations.kind"); Text(o.Description, "observations.description"); Text(o.Source, "observations.source"); }
            foreach (var m in materials)
            {
                Id(m.Id, "materials.id"); Text(m.Name, "materials.name");
                Test(m.Color is not null && Regex.IsMatch(m.Color, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant), "material", m.Id + ".color", "Use explicit RGB color");
                Test(double.IsFinite(m.Roughness) && m.Roughness is >= 0 and <= 1 && double.IsFinite(m.Metallic) && m.Metallic is >= 0 and <= 1, "material", m.Id, "Roughness and metallic must be in [0,1]");
            }
            var materialIds = materials.Select(x => x.Id).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
            long featureCount = 0;
            foreach (var p in parts)
            {
                var path = "parts." + p.Id; Id(p.Id, path); Text(p.Name, path + ".name"); Text(p.Purpose, path + ".purpose"); Text(p.Detail, path + ".detail"); EnumValue(p.Kind, path + ".kind");
                Test(materialIds.Contains(p.MaterialId ?? ""), "reference", path + ".materialId", "Unknown material");
                var sizeValid = Point(p.Size, path + ".size", bound) && Test(p.Size!.X >= n.MinimumFeatureSize && p.Size.Y >= n.MinimumFeatureSize && p.Size.Z >= n.MinimumFeatureSize, "dimension", path + ".size", "Every declared envelope axis must resolve the minimum feature");
                var features = List(p.Features, path + ".features", 131072); featureCount += features.Count;
                Test(features.Count > 0 && features.Any(f => f.Operation == FeatureOperation.Add), "geometry", path + ".features", "A part requires independently authored positive geometry");
                var featureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in features)
                {
                    var fp = path + ".features." + f.Id; Identifier(f.Id, fp); Test(f.Id is not null && featureIds.Add(f.Id), "duplicate", fp, "Feature IDs are unique within a part");
                    EnumValue(f.Kind, fp + ".kind"); EnumValue(f.Operation, fp + ".operation"); var frameValid = Frame(f.Frame, fp + ".frame", bound);
                    var profile = List(f.Profile, fp + ".profile", 4096); var featurePath = List(f.Path, fp + ".path", 262144); var loft = List(f.Sections, fp + ".sections", 1024);
                    var parameters = f.Parameters ?? [];
                    Test(f.Parameters is not null && parameters.Count <= 16, "parameters", fp, "A bounded explicit parameter map is required");
                    foreach (var parameter in parameters) Finite(parameter.Value, fp + ".parameters." + parameter.Key);
                    var keys = f.Kind switch
                    {
                        FeatureKind.Box => new[] { "width", "depth", "height" }, FeatureKind.Cylinder => ["radius", "length", "segments"],
                        FeatureKind.Extrude => ["depth"], FeatureKind.Revolve => ["angleDegrees", "segments"], FeatureKind.Sweep => ["radius", "segments"], FeatureKind.Loft => [], _ => []
                    };
                    var parametersValid = Test(parameters.Keys.Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)), "parameters", fp, "Feature parameters must exactly match their declared family");
                    foreach (var key in keys)
                    {
                        if (!parameters.TryGetValue(key, out var v)) continue;
                        Positive(v, fp + "." + key);
                        if (key == "segments") Test(v == Math.Truncate(v) && v is >= 3 and <= 4096, "resolution", fp, "Segments must be an integer in [3,4096]");
                        else if (key == "angleDegrees") Test(v <= 360, "dimension", fp, "A revolution sweep is at most 360 degrees");
                        else Test(v >= n.MinimumFeatureSize && v <= bound, "dimension", fp, "Feature dimension is outside the declared numerical policy");
                    }
                    var profileValid = true;
                    if (f.Kind is FeatureKind.Extrude or FeatureKind.Revolve) profileValid = Polygon(profile, fp + ".profile");
                    else Test(profile.Count == 0, "geometry", fp + ".profile", "This feature family has no standalone profile");
                    if (f.Kind == FeatureKind.Sweep) Path(featurePath, fp + ".path", false); else Test(featurePath.Count == 0, "geometry", fp + ".path", "Only sweeps carry paths");
                    if (f.Kind == FeatureKind.Loft)
                    {
                        Test(loft.Count >= 2, "geometry", fp + ".sections", "A loft requires at least two framed sections");
                        foreach (var s in loft) { Frame(s.Frame, fp + ".sections.frame", bound); Polygon(List(s.Profile, fp + ".sections.profile", 4096), fp + ".sections.profile"); }
                        Test(loft.Select(s => s.Profile?.Count ?? 0).Distinct().Count() == 1, "geometry", fp, "Loft profiles require matching point counts");
                        for (var index = 1; index < loft.Count; index++) if (loft[index].Frame?.Origin is not null && loft[index-1].Frame?.Origin is not null)
                            Test(DesignGeometry.Length(loft[index-1].Frame.Origin, loft[index].Frame.Origin) >= n.MinimumFeatureSize, "geometry", fp, "Adjacent loft frames must be distinct");
                    }
                    else Test(loft.Count == 0, "geometry", fp + ".sections", "Only lofts carry section profiles");
                    if (f.Kind == FeatureKind.Revolve) foreach (var v in profile) Test(v.X >= 0, "geometry", fp + ".profile", "Revolve profile X is a nonnegative radial distance");
                    if (sizeValid && frameValid && parametersValid && profileValid && issues.Count == 0)
                    {
                        var local = FeatureBounds(f);
                        foreach (var v in DesignGeometry.Corners(local.Min, local.Max).Select(v => DesignGeometry.TransformPoint(f.Frame, v)))
                        {
                            Point(v, fp + ".envelope", bound);
                            if (f.Operation == FeatureOperation.Add) Test(v.X >= -tolerance && v.Y >= -tolerance && v.Z >= -tolerance && v.X <= p.Size.X + tolerance && v.Y <= p.Size.Y + tolerance && v.Z <= p.Size.Z + tolerance,
                                "envelope", fp, "Positive feature declaration exceeds its part-local envelope");
                        }
                    }
                }
                var ports = List(p.Interfaces, path + ".interfaces", 4096); var terminals = List(p.Terminals, path + ".terminals", 4096); var portIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var port in ports) { Port(port.Id, port.Name, port.Frame, path + ".interfaces"); }
                foreach (var port in terminals) { Port(port.Id, port.Name, port.Frame, path + ".terminals"); EnumValue(port.Kind, path + ".terminals.kind"); }
                void Port(string id, string name, LocalFrame frame, string pp)
                {
                    Identifier(id, pp + ".id"); Test(id is not null && portIds.Add(id), "duplicate", pp, "Interface and terminal IDs are unique within a part"); Text(name, pp + ".name");
                    if (Frame(frame, pp + ".frame", bound) && sizeValid) Test(frame.Origin.X >= -tolerance && frame.Origin.Y >= -tolerance && frame.Origin.Z >= -tolerance && frame.Origin.X <= p.Size.X + tolerance && frame.Origin.Y <= p.Size.Y + tolerance && frame.Origin.Z <= p.Size.Z + tolerance,
                        "interface", pp, "Connection ports must lie within the declared part envelope");
                }
                if (p.Kind == PartKind.Rigid) Test(p.Flexible is null, "flexible", path, "Rigid definitions cannot carry a flexible rig contract");
                else if (Test(p.Flexible is not null, "flexible", path, "Flexible parts require explicit deformation limits"))
                {
                    var f = p.Flexible!; Positive(f.RestLength, path + ".restLength"); Positive(f.MinimumLength, path + ".minimumLength"); Positive(f.MaximumLength, path + ".maximumLength"); Positive(f.MinimumBendRadius, path + ".minimumBendRadius");
                    Test(f.MinimumLength <= f.RestLength && f.RestLength <= f.MaximumLength && f.MaximumLength <= bound && double.IsFinite(f.MaximumStretchRatio) && f.MaximumStretchRatio is >= 1 and <= 100 && f.SkinSegments is >= 2 and <= 4096,
                        "flexible", path, "Rest/travel/stretch/skin declarations are inconsistent or unbounded");
                }
            }
            Test(featureCount <= b.MaxFeatures, "budget", "parts.features", "Total feature budget exceeded");
            foreach (var g in groups) { Id(g.Id, "subassemblies.id"); Text(g.Name, g.Id + ".name"); Frame(g.Frame, g.Id + ".frame", bound); }
            foreach (var i in instances) { Id(i.Id, "instances.id"); Frame(i.Frame, i.Id + ".frame", bound); Point(i.ExplodeOffset, i.Id + ".explodeOffset", bound); }
            var partMap = parts.Where(p => p.Id is not null).DistinctBy(p => p.Id).ToDictionary(p => p.Id, StringComparer.Ordinal);
            var groupMap = groups.Where(g => g.Id is not null).DistinctBy(g => g.Id).ToDictionary(g => g.Id, StringComparer.Ordinal);
            var instanceMap = instances.Where(i => i.Id is not null).DistinctBy(i => i.Id).ToDictionary(i => i.Id, StringComparer.Ordinal);
            var parents = new Dictionary<string,string?>(StringComparer.Ordinal);
            foreach (var g in groups) if (g.Id is not null) parents.TryAdd(g.Id, g.ParentId);
            foreach (var i in instances) if (i.Id is not null) parents.TryAdd(i.Id, i.ParentId);
            Test(groups.Count(g => g.ParentId is null) == 1, "hierarchy", "subassemblies", "Exactly one root subassembly is required");
            foreach (var i in instances) Test(partMap.ContainsKey(i.PartId ?? "") && i.ParentId is not null, "reference", i.Id, "Each physical instance requires a known part and parent");
            var usedParts = instances.Select(i => i.PartId).ToHashSet(StringComparer.Ordinal);
            foreach (var p in parts) Test(usedParts.Contains(p.Id), "inventory", p.Id, "Every approved definition must have a physical occurrence");
            foreach (var (id, parent) in parents)
            {
                Test(parent is null || parents.ContainsKey(parent), "reference", id + ".parentId", "Unknown parent");
                var seen = new HashSet<string>(StringComparer.Ordinal); string? current = id;
                while (current is not null && parents.TryGetValue(current, out var next))
                {
                    Check();
                    if (!seen.Add(current) || seen.Count > 256) { Error("hierarchy", id, "Cycle or hierarchy depth exceeds 256"); break; }
                    current = next;
                }
            }
            Func<string,Vec3,Vec3>? worldPoint = null;
            if (!invalidCodes.Overlaps(["hierarchy","reference","duplicate","point","coordinate","frame","null","collection","dimension"]))
            {
                worldPoint = DesignGeometry.CreateWorldPointResolver(design, Check);
                foreach (var i in instances) foreach (var v in DesignGeometry.Corners(Vec3.Zero, partMap[i.PartId].Size)) Point(worldPoint(i.Id, v), i.Id + ".worldEnvelope", bound);
            }
            foreach (var j in joints)
            {
                Id(j.Id, "joints.id"); EnumValue(j.Kind, j.Id + ".kind");
                var from = EndpointPart(new() { InstanceId = j.ParentInstanceId, PortId = j.ParentInterfaceId }, j.Id + ".parent", false);
                var to = EndpointPart(new() { InstanceId = j.ChildInstanceId, PortId = j.ChildInterfaceId }, j.Id + ".child", false);
                Test(j.ParentInstanceId != j.ChildInstanceId, "joint", j.Id, "A joint connects distinct physical instances");
                if (Point(j.Axis, j.Id + ".axis", 1)) Test(Math.Abs(DesignGeometry.Length(Vec3.Zero, j.Axis) - 1) <= 1e-8, "joint", j.Id, "Joint axis must be normalized");
                Test(double.IsFinite(j.Minimum) && double.IsFinite(j.Maximum) && double.IsFinite(j.Rest) && j.Minimum <= j.Rest && j.Rest <= j.Maximum && Math.Max(Math.Abs(j.Minimum), Math.Abs(j.Maximum)) <= (j.Kind is JointKind.Slider or JointKind.Fixed ? bound : 360_000), "joint", j.Id, "Finite ordered joint travel and rest are required");
                Test(double.IsFinite(j.Clearance) && j.Clearance >= 0 && j.Clearance <= bound, "joint", j.Id + ".clearance", "Clearance must be finite and nonnegative");
                if (j.Kind == JointKind.Fixed) Test(j.Minimum == 0 && j.Maximum == 0 && j.Rest == 0, "joint", j.Id, "Fixed connections have zero travel");
                else Test(j.Minimum < j.Maximum, "joint", j.Id, "Moving joints require nonempty travel");
                Test(double.IsFinite(j.Pitch) && (j.Kind == JointKind.Screw ? Math.Abs(j.Pitch) >= n.MinimumFeatureSize && Math.Abs(j.Pitch) <= bound : j.Pitch == 0), "joint", j.Id + ".pitch", "Only screw joints carry a nonzero finite pitch");
                if (worldPoint is not null && from?.Interfaces?.SingleOrDefault(p=>p.Id==j.ParentInterfaceId) is { } fromPort && to?.Interfaces?.SingleOrDefault(p=>p.Id==j.ChildInterfaceId) is { } toPort)
                    Test(DesignGeometry.Length(worldPoint(j.ParentInstanceId,fromPort.Frame.Origin),worldPoint(j.ChildInstanceId,toPort.Frame.Origin)) <= tolerance, "joint", j.Id, "Mating interface origins must coincide in the declared rest assembly");
            }
            var declaredClosures=mechanisms.Where(m=>m.Kind is MechanismKind.FourBar or MechanismKind.SliderCrank && m.JointIds?.Count==4).Select(m=>m.JointIds[^1]).ToHashSet(StringComparer.Ordinal);
            var owningJoints=joints.Where(j=>!declaredClosures.Contains(j.Id)).ToArray();
            Test(owningJoints.Select(j => j.ChildInstanceId).Distinct(StringComparer.Ordinal).Count() == owningJoints.Length, "joint", "joints", "Each physical instance has one owning joint apart from an explicitly declared closing constraint");
            var jointMap = joints.Where(j => j.Id is not null).DistinctBy(j => j.Id).ToDictionary(j => j.Id, StringComparer.Ordinal);
            foreach (var m in mechanisms)
            {
                Id(m.Id, "mechanisms.id"); EnumValue(m.Kind, m.Id + ".kind");
                var js = List(m.JointIds, m.Id + ".jointIds", 4);
                Test(js.Distinct(StringComparer.Ordinal).Count() == js.Count && js.All(id => id is not null && jointMap.ContainsKey(id)), "mechanism", m.Id, "Every ordered driver/follower joint must exist and be distinct");
                var keys = m.Kind switch { MechanismKind.SpurGears => new[] { "ratio" }, MechanismKind.RackAndPinion => ["pitchRadius"], MechanismKind.PulleyBelt => ["ratio"], MechanismKind.SliderCrank => ["crankLength", "rodLength", "offset"], MechanismKind.FourBar => ["groundLength", "inputLength", "couplerLength", "outputLength"], _ => [] };
                var ps = m.Parameters ?? [];
                Test(m.Parameters is not null && ps.Count <= 8 && ps.Keys.Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)), "mechanism", m.Id, "Use the exact bounded parameters for this mechanism family");
                foreach (var p in ps) { Finite(p.Value, m.Id + "." + p.Key); if (p.Key == "ratio") Test(Math.Abs(p.Value) is >= 1e-6 and <= 1_000_000, "mechanism", m.Id, "Ratio must be finite, nonzero and bounded"); else if (p.Key == "offset") Test(Math.Abs(p.Value) <= bound, "mechanism", m.Id, "Offset exceeds numerical policy"); else Test(p.Value >= n.MinimumFeatureSize && p.Value <= bound, "mechanism", m.Id, "Mechanism lengths must resolve the minimum feature"); }
                Test(js.Count == (m.Kind is MechanismKind.SliderCrank or MechanismKind.FourBar ? 4 : 2), "mechanism", m.Id, "Mechanism joint count does not match its family");
                Test(m.Branch is "open" or "crossed" && (m.Kind is MechanismKind.FourBar or MechanismKind.SliderCrank or MechanismKind.PulleyBelt || m.Branch == "open"), "mechanism", m.Id, "Declare a supported linkage branch");
                if (js.All(id => id is not null && jointMap.ContainsKey(id)) && js.Count >= 2)
                {
                    var types = js.Select(id => jointMap[id].Kind).ToArray();
                    bool Rotary(JointKind k) => k is JointKind.Hinge or JointKind.Rotary;
                    Test(m.Kind switch { MechanismKind.SpurGears or MechanismKind.PulleyBelt or MechanismKind.FourBar => types.All(Rotary), MechanismKind.RackAndPinion => Rotary(types[0]) && types[1] == JointKind.Slider, MechanismKind.SliderCrank => types.Length==4 && Rotary(types[0]) && Rotary(types[1]) && Rotary(types[2]) && types[3] == JointKind.Slider, _ => false }, "mechanism", m.Id, "Mechanism joints have incompatible motion types");
                    if(m.Kind is MechanismKind.FourBar or MechanismKind.SliderCrank && js.Count==4)
                    {
                        var ordered=js.Select(id=>jointMap[id]).ToArray();
                        Test(ordered[0].ChildInstanceId==ordered[1].ParentInstanceId && ordered[1].ChildInstanceId==ordered[2].ParentInstanceId
                            && ordered[0].ParentInstanceId==ordered[3].ParentInstanceId && ordered[2].ChildInstanceId==ordered[3].ChildInstanceId
                            && new[]{ordered[0].ParentInstanceId,ordered[0].ChildInstanceId,ordered[1].ChildInstanceId,ordered[2].ChildInstanceId}.Distinct(StringComparer.Ordinal).Count()==4,
                            "mechanism",m.Id,"Ordered linkage topology must be ground-to-input, input-to-coupler, coupler-to-output, ground-to-output closing constraint");
                    }
                    if (m.Kind == MechanismKind.SliderCrank && ps.TryGetValue("rodLength", out var rod) && ps.TryGetValue("crankLength", out var crank) && ps.TryGetValue("offset", out var offset)) Test(rod > crank + Math.Abs(offset) + tolerance, "mechanism", m.Id, "Slider-crank requires a declared nonsingular full-cycle length relation");
                    if (m.Kind == MechanismKind.FourBar && keys.All(ps.ContainsKey)) Test(ps.Values.Max() < ps.Values.Sum() - ps.Values.Max() - tolerance, "mechanism", m.Id, "Declared four-bar lengths cannot close");
                }
            }
            var followers=mechanisms.SelectMany(m=>(m.JointIds ?? []).Skip(1)).ToArray();
            Test(followers.Distinct(StringComparer.Ordinal).Count()==followers.Length,"mechanism","mechanisms","A follower or closing joint cannot be independently controlled by multiple mechanisms");
            // The physical linkage closure is not a driver dependency. Ordered mechanisms
            // drive their followers, and a graph cycle has no independently controllable input.
            var driverEdges=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
            var incomingDrivers=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach(var m in mechanisms)
            {
                Check();
                var ordered=m.JointIds;
                if(ordered is null || ordered.Count is < 2 or > 4 || ordered.Any(id=>id is null || !jointMap.ContainsKey(id))) continue;
                foreach(var id in ordered)
                {
                    Check();
                    driverEdges.TryAdd(id,new(StringComparer.Ordinal)); incomingDrivers.TryAdd(id,0);
                }
                foreach(var follower in ordered.Skip(1))
                {
                    Check();
                    if(driverEdges[ordered[0]].Add(follower)) incomingDrivers[follower]++;
                }
            }
            var independentDrivers=new Queue<string>(incomingDrivers.Where(entry=>entry.Value==0).Select(entry=>entry.Key));
            var reachableJoints=0;
            while(independentDrivers.TryDequeue(out var driver))
            {
                Check(); reachableJoints++;
                foreach(var follower in driverEdges[driver])
                {
                    Check(); if(--incomingDrivers[follower]==0) independentDrivers.Enqueue(follower);
                }
            }
            Test(reachableJoints==incomingDrivers.Count,"mechanism","mechanisms","Directed driver-to-follower coupling cycles are unsupported; each dependency chain requires an independent input");
            long routePointCount = 0;
            foreach (var r in routes)
            {
                Id(r.Id, "routes.id"); var points = List(r.Points, r.Id + ".points", 262144); routePointCount += points.Count; Path(points, r.Id, r.Closed);
                var owner = instanceMap.TryGetValue(r.InstanceId ?? "", out var instance) && partMap.TryGetValue(instance.PartId ?? "", out var part) ? part : null;
                Test(owner is not null && owner.Kind != PartKind.Rigid, "route", r.Id, "Each route owns one flexible physical instance");
                if (r.Closed) Test(owner?.Kind == PartKind.Belt && r.From is null && r.To is null, "route", r.Id, "Closed belts have no fictitious terminal endpoints");
                else
                {
                    Test(owner?.Kind != PartKind.Belt, "route", r.Id, "A belt requires a closed loop route");
                    var start = EndpointPart(r.From, r.Id + ".from", null); var end = EndpointPart(r.To, r.Id + ".to", null);
                    if (worldPoint is not null && points.Count >= 2)
                    {
                        Vec3? PortPoint(PartDefinition? definition, Endpoint? endpoint)
                        {
                            if (definition is null || endpoint is null) return null;
                            var frame = definition.Interfaces?.FirstOrDefault(p=>p is not null && p.Id==endpoint.PortId)?.Frame ?? definition.Terminals?.FirstOrDefault(p=>p is not null && p.Id==endpoint.PortId)?.Frame;
                            return frame?.Origin is null ? null : worldPoint(endpoint.InstanceId,frame.Origin);
                        }
                        if (PortPoint(start,r.From) is { } from) Test(DesignGeometry.Length(points[0],from) <= tolerance, "route", r.Id, "First route point must bind the actual world endpoint");
                        if (PortPoint(end,r.To) is { } to) Test(DesignGeometry.Length(points[^1],to) <= tolerance, "route", r.Id, "Last route point must bind the actual world endpoint");
                    }
                }
                if (owner?.Flexible is { } flex && points.Count >= 2)
                {
                    double length = 0; for (var index = 1; index < points.Count; index++) length += DesignGeometry.Length(points[index - 1], points[index]);
                    if (r.Closed) length += DesignGeometry.Length(points[^1], points[0]);
                    Test(Math.Abs(length - flex.RestLength) <= tolerance && length >= flex.MinimumLength - tolerance && length <= flex.MaximumLength + tolerance, "route", r.Id, "Route length must match declared rest length and travel");
                }
            }
            Test(routePointCount <= b.MaxRoutePoints, "budget", "routes", "Total routed point budget exceeded");
            Test(routes.Select(r => r.InstanceId).Distinct(StringComparer.Ordinal).Count() == routes.Count, "route", "routes", "One physical flexible component cannot own multiple independent routes");
            var routeCounts=routes.GroupBy(r=>r.InstanceId ?? "",StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.Ordinal);
            foreach (var i in instances) if (partMap.TryGetValue(i.PartId ?? "", out var part) && part.Kind != PartKind.Rigid) Test(routeCounts.GetValueOrDefault(i.Id ?? "") == 1, "route", i.Id ?? "instances", "Every flexible occurrence requires its own route");
            var routeMap = routes.Where(r => r.Id is not null).DistinctBy(r => r.Id).ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach (var c in connections)
            {
                Id(c.Id, "connections.id"); EnumValue(c.Kind, c.Id + ".kind");
                var electrical = c.Kind != ConnectionKind.Mechanical;
                var startPart = EndpointPart(c.From, c.Id + ".from", electrical); var endPart = EndpointPart(c.To, c.Id + ".to", electrical);
                Test(routeMap.TryGetValue(c.RouteId ?? "", out var route) && !route.Closed && route.From == c.From && route.To == c.To, "connection", c.Id, "Connection must match an owned open route and exact endpoints");
                Test(c.From is not null && c.To is not null && c.From != c.To, "connection", c.Id, "A connection requires distinct endpoints");
                if (electrical && route is not null && instanceMap.TryGetValue(route.InstanceId ?? "",out var carrier) && partMap.TryGetValue(carrier.PartId ?? "",out var carrierPart)) Test(carrierPart.Kind==PartKind.Wire,"connection",c.Id,"An electrical connection must own an actual wire occurrence");
                if (electrical && startPart is not null && endPart is not null)
                {
                    var expected = c.Kind switch { ConnectionKind.Power => TerminalKind.Power, ConnectionKind.Signal => TerminalKind.Signal, _ => TerminalKind.Ground };
                    Test(startPart.Terminals?.Any(t => t is not null && t.Id == c.From!.PortId && t.Kind == expected) == true && endPart.Terminals?.Any(t => t is not null && t.Id == c.To!.PortId && t.Kind == expected) == true, "connection", c.Id, "Terminal kinds must match the declared electrical connection");
                }
            }
            var connectionCounts=connections.Where(c=>c.Kind!=ConnectionKind.Mechanical).GroupBy(c=>c.RouteId ?? "",StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.Ordinal);
            foreach(var r in routes) if(instanceMap.TryGetValue(r.InstanceId ?? "",out var carrier) && partMap.TryGetValue(carrier.PartId ?? "",out var carrierPart) && carrierPart.Kind==PartKind.Wire) Test(connectionCounts.GetValueOrDefault(r.Id ?? "")==1,"connection",r.Id ?? "routes","Each wire route requires one explicit electrical connection");
            foreach (var s in sections) { Id(s.Id, "sections.id"); EnumValue(s.Axis, s.Id + ".axis"); Test(double.IsFinite(s.Offset) && Math.Abs(s.Offset) <= bound, "section", s.Id, "Section offset exceeds declared coordinates"); }
            Test(6L + sections.Count + parts.Count + 3 + (design.AssemblyProfile is null ? 0 : 2) <= b.MaxDrawings, "budget", "budgets.maxDrawings", "Budget must cover six orthographic views, sections, independent part sheets, exploded, BOM, connections and applicable assembly/rig policy sheets");
            if (issues.Count == 0)
            {
                var profile = new AssemblyProfileValidator().Validate(design, cancellationToken);
                foreach (var issue in profile.Issues) Error(issue.Code, issue.Path, issue.Message);
                checks = checked(checks + profile.Checks);
            }
            return new(issues, checks);

            PartDefinition? EndpointPart(Endpoint? endpoint, string path, bool? electrical)
            {
                if (!Test(endpoint is not null, "endpoint", path, "An explicit owned endpoint is required")) return null;
                if (!instanceMap.TryGetValue(endpoint!.InstanceId ?? "", out var instance) || !partMap.TryGetValue(instance.PartId ?? "", out var part)) { Error("reference", path, "Unknown endpoint instance"); return null; }
                Test(electrical switch { true => part.Terminals?.Any(t => t is not null && t.Id == endpoint.PortId) == true, false => part.Interfaces?.Any(t => t is not null && t.Id == endpoint.PortId) == true, _ => part.Terminals?.Any(t => t is not null && t.Id == endpoint.PortId) == true || part.Interfaces?.Any(t => t is not null && t.Id == endpoint.PortId) == true }, "endpoint", path, "Unknown or incompatible endpoint port");
                return part;
            }
            void Path(IReadOnlyList<Vec3> points, string path, bool closed)
            {
                Test(points.Count >= (closed ? 3 : 2), "path", path, "Route requires explicit nondegenerate points");
                for (var index = 0; index < points.Count; index++)
                {
                    Point(points[index], path, bound);
                    if (index > 0) Test(DesignGeometry.Length(points[index-1], points[index]) >= n.MinimumFeatureSize, "path", path, "Consecutive path points must resolve the minimum feature");
                }
                if (closed && points.Count > 1) Test(DesignGeometry.Length(points[^1], points[0]) >= n.MinimumFeatureSize, "path", path, "Do not repeat the implicit closing point");
            }
            bool Polygon(IReadOnlyList<Vec2> polygon, string path)
            {
                var valid = Test(polygon.Count >= 3, "profile", path, "Profile requires at least three distinct vertices");
                double area = 0;
                for (var index = 0; index < polygon.Count; index++)
                {
                    var a = polygon[index]; var z = polygon[(index + 1) % polygon.Count];
                    valid &= Test(double.IsFinite(a.X) && double.IsFinite(a.Y) && Math.Abs(a.X) <= bound && Math.Abs(a.Y) <= bound, "profile", path, "Profile coordinates must be finite and bounded");
                    var dx = a.X - z.X; var dy = a.Y - z.Y;
                    valid &= Test(dx * dx + dy * dy >= n.MinimumFeatureSize * n.MinimumFeatureSize, "profile", path, "Profile edges must resolve minimum feature without a repeated closing vertex");
                    area += a.X * z.Y - z.X * a.Y;
                    for (var j = index + 2; j < polygon.Count; j++)
                    {
                        if (index == 0 && j == polygon.Count - 1) continue;
                        Check(); if (Intersects(a, z, polygon[j], polygon[(j+1)%polygon.Count], tolerance)) { valid = false; Error("profile", path, "Profile edges intersect"); }
                    }
                }
                return Test(double.IsFinite(area) && area > tolerance * tolerance, "profile", path, "Profile must be counter-clockwise with positive area") && valid;
            }
        }
        catch (WorkLimitException) { Error("budget", "$", "Declared validation work budget exhausted; no geometry correctness claim is available"); return new(issues, checks); }
    }

    public void RequireValid(AssemblyDesign design, CancellationToken cancellationToken = default)
    {
        var report = Validate(design, cancellationToken); if (!report.IsValid) throw new DesignValidationException(report);
    }
    sealed class WorkLimitException : Exception;
    static bool Intersects(Vec2 a, Vec2 b, Vec2 c, Vec2 d, double tolerance)
    {
        static double Cross(Vec2 p, Vec2 q, Vec2 r) => (q.X-p.X)*(r.Y-p.Y)-(q.Y-p.Y)*(r.X-p.X);
        var ac = Cross(a,b,c); var ad = Cross(a,b,d); var ca = Cross(c,d,a); var cb = Cross(c,d,b);
        return Math.Max(Math.Min(a.X,b.X),Math.Min(c.X,d.X)) <= Math.Min(Math.Max(a.X,b.X),Math.Max(c.X,d.X))+tolerance
            && Math.Max(Math.Min(a.Y,b.Y),Math.Min(c.Y,d.Y)) <= Math.Min(Math.Max(a.Y,b.Y),Math.Max(c.Y,d.Y))+tolerance
            && ac*ad <= 0 && ca*cb <= 0;
    }

    internal static (Vec3 Min, Vec3 Max) FeatureBounds(ShapeFeature f)
    {
        static Vec3 V(double x,double y,double z) => new() { X=x,Y=y,Z=z };
        var p = f.Parameters;
        return f.Kind switch
        {
            FeatureKind.Box => (Vec3.Zero,V(p["width"],p["depth"],p["height"])),
            FeatureKind.Cylinder => (V(-p["radius"],-p["radius"],0),V(p["radius"],p["radius"],p["length"])),
            FeatureKind.Extrude => DesignGeometry.Enclose(f.Profile.SelectMany(q => new[] { V(q.X,q.Y,0),V(q.X,q.Y,p["depth"]) })),
            FeatureKind.Revolve => (V(-f.Profile.Max(q=>q.X),-f.Profile.Max(q=>q.X),f.Profile.Min(q=>q.Y)),V(f.Profile.Max(q=>q.X),f.Profile.Max(q=>q.X),f.Profile.Max(q=>q.Y))),
            FeatureKind.Sweep => DesignGeometry.Enclose(f.Path.SelectMany(q => new[] { V(q.X-p["radius"],q.Y-p["radius"],q.Z-p["radius"]),V(q.X+p["radius"],q.Y+p["radius"],q.Z+p["radius"]) })),
            FeatureKind.Loft => DesignGeometry.Enclose(f.Sections.SelectMany(s=>s.Profile.Select(q=>DesignGeometry.TransformPoint(s.Frame,V(q.X,q.Y,0))))),
            _ => throw new ArgumentException("Unsupported feature family")
        };
    }
}
