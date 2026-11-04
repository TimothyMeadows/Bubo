using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;

namespace Bubo.Schematic.Infrastructure;

/// <summary>
/// Dimensioned design wireframes and declared-envelope sections. These are not measured construction
/// geometry, hidden-line CAD drawings, boolean solid sections or animation-readiness evidence.
/// </summary>
public sealed class SvgSchematicRenderer : ISchematicPreviewRenderer
{
    static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    readonly IAssemblyDesignValidator validator;
    public SvgSchematicRenderer() : this(new AssemblyDesignValidator()) { }
    public SvgSchematicRenderer(IAssemblyDesignValidator validator) => this.validator = validator ?? throw new ArgumentNullException(nameof(validator));

    public IReadOnlyDictionary<string, string> Render(AssemblyDesign design, CancellationToken cancellation = default)
    {
        validator.RequireValid(design, cancellation);
        var required = PreviewCompiler.RequiredFiles(design);
        if (required.Count - 1 > design.Budgets.MaxDrawings)
            throw new InvalidOperationException("Complete schematic inventory exceeds MaxDrawings.");
        var budget = new PreviewBudget(design.Budgets.MaxPreviewBytes, design.Budgets.MaxValidationChecks, cancellation);
        Preflight(design, cancellation);
        var world = DesignGeometry.CreateWorldPointResolver(design, budget.Step);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var parts = design.Parts.ToDictionary(part => part.Id, StringComparer.Ordinal);
        var instances = design.Instances.OrderBy(instance => instance.Id, StringComparer.Ordinal).ToArray();
        void Add(string name, Sheet sheet) => AddText(name, sheet.Finish());
        void AddText(string name, string content)
        {
            budget.Commit(content);
            files.Add(name, content);
        }
        foreach (var view in Views)
        {
            cancellation.ThrowIfCancellationRequested();
            Add("orthographic-" + view.Name + ".svg", AssemblySheet(design, parts, instances, view, false, budget, world));
        }
        Add("exploded.svg", AssemblySheet(design, parts, instances, Isometric, true, budget, world));
        foreach (var section in design.Sections.OrderBy(section => section.Id, StringComparer.Ordinal))
            Add("section-" + section.Id + ".svg", SectionSheet(design, parts, instances, section, budget, world));
        foreach (var part in design.Parts.OrderBy(part => part.Id, StringComparer.Ordinal))
            Add("part-" + part.Id + ".svg", PartSheet(design, part, budget));
        Add("connections.svg", ConnectionSheet(design, parts, budget, world));
        Add("bom.svg", BomSheet(design, parts, budget));
        AddText("bom.json", BomJson(design, world));
        if (design.AssemblyProfile is not null)
        {
            Add("assembly-details.svg", AssemblyPolicySheet(design, budget));
            Add("flex-rigs.svg", FlexiblePolicySheet(design, budget));
        }
        return files;
    }

    static readonly View[] Views =
    [
        new("front", p => new(p.X, p.Z), "+X right; +Z up; looking along +Y"),
        new("right", p => new(-p.Y, p.Z), "-Y right; +Z up; looking along -X"),
        new("back", p => new(-p.X, p.Z), "-X right; +Z up; looking along -Y"),
        new("left", p => new(p.Y, p.Z), "+Y right; +Z up; looking along +X"),
        new("top", p => new(p.X, p.Y), "+X right; +Y up; looking along -Z"),
        new("bottom", p => new(p.X, -p.Y), "+X right; -Y up; looking along +Z")
    ];
    static readonly View Isometric = new("exploded", p => new((p.X - p.Y) / Math.Sqrt(2), (p.X + p.Y) / Math.Sqrt(6) + p.Z * Math.Sqrt(2.0 / 3)), "Isometric; explode offsets are world-space metres");
    sealed record View(string Name, Func<Vec3, P2> Project, string Description);
    readonly record struct P2(double X, double Y);
    readonly record struct Edge(Vec3 A, Vec3 B);

    static void Preflight(AssemblyDesign design, CancellationToken cancellation)
    {
        // Bound feature expansion before allocating rings, XML or full sheet strings. Every feature
        // is repeated across seven assembly views and the declared cut planes, then drawn alone.
        var edges = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var part in design.Parts)
        {
            cancellation.ThrowIfCancellationRequested();
            long total = 0;
            foreach (var feature in part.Features)
            {
                long count = feature.Kind switch
                {
                    FeatureKind.Box => 12,
                    FeatureKind.Cylinder => checked(3 * (long)feature.Parameters["segments"]),
                    FeatureKind.Extrude => checked(3L * feature.Profile.Count),
                    FeatureKind.Revolve => checked(feature.Profile.Count * (2 * (long)feature.Parameters["segments"] + 1)),
                    FeatureKind.Sweep => checked((long)feature.Parameters["segments"] * (2L * feature.Path.Count - 1)),
                    FeatureKind.Loft => checked(feature.Sections.Sum(section => (long)section.Profile.Count) + feature.Sections.Skip(1).Sum(section => (long)section.Profile.Count)),
                    _ => throw new InvalidOperationException("Unsupported schematic feature expansion.")
                };
                total = checked(total + count);
            }
            edges.Add(part.Id, total);
        }
        var instanceEdges = design.Instances.Sum(instance => edges[instance.PartId]);
        var estimate = checked(4L * (7 + design.Sections.Count) * instanceEdges + 4L * edges.Values.Sum()
            + 160L * design.Instances.Count + 10L * design.Routes.Sum(route => (long)route.Points.Count) * (8 + design.Sections.Count));
        if (estimate > Math.Min(design.Budgets.MaxValidationChecks, 4_000_000))
            throw new InvalidOperationException("Schematic preview feature expansion exceeds its bounded work allowance before drawing.");
    }

    static Sheet AssemblySheet(AssemblyDesign design, Dictionary<string, PartDefinition> parts, PartInstance[] instances, View view, bool exploded, PreviewBudget budget, Func<string, Vec3, Vec3> world)
    {
        var sheet = new Sheet(design, view.Name, exploded ? "Exploded assembly" : "Orthographic " + view.Name, budget);
        sheet.Note("Dimensioned feature wireframes; dashed envelopes are declared sizes. Hidden surfaces remain visible.");
        sheet.Note(view.Description);
        var projected = instances.SelectMany(instance => Envelope(parts[instance.PartId].Size)
            .Select(point => view.Project(World(world, instance, point, exploded)))).ToArray();
        var map = new Map(projected);
        foreach (var instance in instances)
        {
            budget.Step();
            var part = parts[instance.PartId];
            var group = sheet.Group("part-instance", ("data-instance-id", instance.Id), ("data-part-id", part.Id), ("data-material-id", part.MaterialId), ("data-declared-size", Vector(part.Size)));
            var origin = World(world, instance, Vec3.Zero, exploded);
            group.SetAttributeValue("data-world-origin", Vector(origin));
            group.SetAttributeValue("data-explode-offset", Vector(exploded ? instance.ExplodeOffset : Vec3.Zero));
            foreach (var edge in BoxEdges(part.Size))
                sheet.Line(map.At(view.Project(World(world, instance, edge.A, exploded))), map.At(view.Project(World(world, instance, edge.B, exploded))), "#8b9299", group, true);
            foreach (var feature in part.Features.OrderBy(feature => feature.Id, StringComparer.Ordinal))
            {
                var featureGroup = FeatureGroup(sheet, group, feature);
                foreach (var edge in FeatureEdges(feature, budget))
                {
                    var a = World(world, instance, DesignGeometry.TransformPoint(feature.Frame, edge.A), exploded);
                    var b = World(world, instance, DesignGeometry.TransformPoint(feature.Frame, edge.B), exploded);
                    sheet.Line(map.At(view.Project(a)), map.At(view.Project(b)), feature.Operation == FeatureOperation.Subtract ? "#a52d3b" : "#234e5b", featureGroup, feature.Operation == FeatureOperation.Subtract);
                }
            }
            var center = Center(Envelope(part.Size).Select(point => World(world, instance, point, exploded)));
            sheet.Label(map.At(view.Project(center)), instance.Id, group);
            sheet.Row(instance.Id + " / " + part.Id + " / " + part.MaterialId + " / " + sheet.DisplayVector(part.Size) + " m");
            if (exploded)
            {
                sheet.Line(map.At(view.Project(world(instance.Id, Vec3.Zero))), map.At(view.Project(origin)), "#a3a8ad", group, true);
                sheet.Row("  parent " + instance.ParentId + "; world offset " + sheet.DisplayVector(instance.ExplodeOffset) + " m");
            }
        }
        DrawRoutes(sheet, design, view, map, budget, exploded);
        sheet.OverallDimensions(map, projected);
        return sheet;
    }

    static XElement FeatureGroup(Sheet sheet, XElement parent, ShapeFeature feature)
    {
        var group = sheet.Group("feature", parent, ("data-feature-id", feature.Id), ("data-feature-kind", feature.Kind.ToString()), ("data-operation", feature.Operation.ToString()));
        foreach (var parameter in feature.Parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal))
            group.SetAttributeValue("data-parameter-" + parameter.Key, N(parameter.Value));
        return group;
    }

    static Sheet PartSheet(AssemblyDesign design, PartDefinition part, PreviewBudget budget)
    {
        var sheet = new Sheet(design, "part-detail", part.Id + " — independent part definition", budget);
        sheet.Root.SetAttributeValue("data-part-id", part.Id);
        sheet.Root.SetAttributeValue("data-material-id", part.MaterialId);
        sheet.Note(part.Name + ": " + part.Purpose);
        sheet.Note("Detail decision: " + part.Detail);
        var projected = Envelope(part.Size).Select(Isometric.Project).ToArray();
        var map = new Map(projected);
        var group = sheet.Group("part-definition", ("data-part-id", part.Id), ("data-size", Vector(part.Size)));
        foreach (var edge in BoxEdges(part.Size)) sheet.Line(map.At(Isometric.Project(edge.A)), map.At(Isometric.Project(edge.B)), "#8b9299", group, true);
        foreach (var feature in part.Features.OrderBy(feature => feature.Id, StringComparer.Ordinal))
        {
            var featureGroup = FeatureGroup(sheet, group, feature);
            foreach (var edge in FeatureEdges(feature, budget))
            {
                var a = DesignGeometry.TransformPoint(feature.Frame, edge.A); var b = DesignGeometry.TransformPoint(feature.Frame, edge.B);
                sheet.Line(map.At(Isometric.Project(a)), map.At(Isometric.Project(b)), feature.Operation == FeatureOperation.Subtract ? "#a52d3b" : "#234e5b", featureGroup, feature.Operation == FeatureOperation.Subtract);
            }
            sheet.Row(feature.Id + ": " + feature.Kind + " " + feature.Operation);
            sheet.Row("  frame " + sheet.DisplayVector(feature.Frame.Origin) + " m; XYZ " + sheet.DisplayVector(feature.Frame.RotationDegrees, true) + " deg");
            foreach (var parameter in feature.Parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal))
                sheet.Row("  " + parameter.Key + " = " + sheet.Scalar(parameter.Value));
            for (var i = 0; i < feature.Profile.Count; i++) sheet.Row("  profile " + i + ": " + sheet.Length(feature.Profile[i].X) + ", " + sheet.Length(feature.Profile[i].Y) + " m");
            for (var i = 0; i < feature.Path.Count; i++) sheet.Row("  path " + i + ": " + sheet.DisplayVector(feature.Path[i]) + " m");
            for (var i = 0; i < feature.Sections.Count; i++)
            {
                sheet.Row("  loft section " + i + ": frame " + sheet.DisplayVector(feature.Sections[i].Frame.Origin) + "; XYZ " + sheet.DisplayVector(feature.Sections[i].Frame.RotationDegrees, true) + " deg");
                foreach (var point in feature.Sections[i].Profile) sheet.Row("    " + sheet.Length(point.X) + ", " + sheet.Length(point.Y) + " m");
            }
        }
        sheet.Row("Declared local envelope: X " + sheet.Length(part.Size.X) + "; Y " + sheet.Length(part.Size.Y) + "; Z " + sheet.Length(part.Size.Z) + " m");
        var corner = map.At(Isometric.Project(Vec3.Zero));
        sheet.Dimension(corner, map.At(Isometric.Project(V(part.Size.X, 0, 0))), "X " + sheet.Length(part.Size.X) + " m", "part-size-x", part.Size.X);
        sheet.Dimension(corner, map.At(Isometric.Project(V(0, part.Size.Y, 0))), "Y " + sheet.Length(part.Size.Y) + " m", "part-size-y", part.Size.Y);
        sheet.Dimension(corner, map.At(Isometric.Project(V(0, 0, part.Size.Z))), "Z " + sheet.Length(part.Size.Z) + " m", "part-size-z", part.Size.Z);
        foreach (var port in part.Interfaces.OrderBy(port => port.Id, StringComparer.Ordinal))
        {
            var p = map.At(Isometric.Project(port.Frame.Origin)); sheet.Circle(p, 5, "#7d4396", group); sheet.Label(p, port.Id, group);
            sheet.Row("Interface " + port.Id + " / " + port.Name + ": " + sheet.DisplayVector(port.Frame.Origin) + " m; XYZ " + sheet.DisplayVector(port.Frame.RotationDegrees, true) + " deg");
        }
        foreach (var terminal in part.Terminals.OrderBy(terminal => terminal.Id, StringComparer.Ordinal))
        {
            var p = map.At(Isometric.Project(terminal.Frame.Origin)); sheet.Circle(p, 4, "#b66b00", group); sheet.Label(p, terminal.Id, group);
            sheet.Row("Terminal " + terminal.Id + " / " + terminal.Kind + " / " + terminal.Name + ": " + sheet.DisplayVector(terminal.Frame.Origin) + " m");
        }
        if (part.Flexible is { } flexible)
        {
            sheet.Row("Flexible " + part.Kind + ": rest " + sheet.Length(flexible.RestLength) + " m; length " + sheet.Length(flexible.MinimumLength) + ".." + sheet.Length(flexible.MaximumLength) + " m");
            sheet.Row("Bend radius >= " + sheet.Length(flexible.MinimumBendRadius) + " m; stretch <= " + sheet.Scalar(flexible.MaximumStretchRatio) + "; skin segments " + flexible.SkinSegments);
            sheet.Note("Flexible geometry here is a design rest shape. Portable deformation remains unverified.");
        }
        return sheet;
    }

    static Sheet SectionSheet(AssemblyDesign design, Dictionary<string, PartDefinition> parts, PartInstance[] instances, SectionSpec section, PreviewBudget budget, Func<string, Vec3, Vec3> world)
    {
        var axis = section.Axis;
        var view = axis switch
        {
            SectionAxis.X => new View("section", point => new(point.Y, point.Z), "+Y right; +Z up"),
            SectionAxis.Y => new View("section", point => new(point.X, point.Z), "+X right; +Z up"),
            _ => new View("section", point => new(point.X, point.Y), "+X right; +Y up")
        };
        var sheet = new Sheet(design, "section", "Section " + section.Id + ": " + axis + " = " + FormatDisplay(section.Offset, design.Numerics.LinearTolerance) + " m", budget);
        sheet.Root.SetAttributeValue("data-section-id", section.Id);
        sheet.Root.SetAttributeValue("data-section-axis", axis.ToString());
        sheet.Root.SetAttributeValue("data-section-offset", N(section.Offset));
        sheet.Root.SetAttributeValue("data-section-mode", "declared-envelope-and-feature-intersections");
        sheet.Note("Schematic cut: dashed declared envelope intersections and feature edge intersections; boolean solid sections await construction.");
        sheet.Note(view.Description + "; all internal components crossing this plane are listed.");
        var projected = instances.SelectMany(instance => Envelope(parts[instance.PartId].Size).Select(point => view.Project(world(instance.Id, point)))).ToArray();
        var map = new Map(projected);
        foreach (var instance in instances)
        {
            budget.Step();
            var part = parts[instance.PartId];
            var points = Intersections(BoxEdges(part.Size).Select(edge => new Edge(world(instance.Id, edge.A), world(instance.Id, edge.B))), axis, section.Offset, design.Numerics.LinearTolerance, budget).ToArray();
            if (points.Length == 0) continue;
            var group = sheet.Group("section-instance", ("data-instance-id", instance.Id), ("data-part-id", part.Id));
            var polygon = ConvexHull(points.Select(view.Project));
            if (polygon.Count > 1)
                for (var i = 0; i < polygon.Count; i++) sheet.Line(map.At(polygon[i]), map.At(polygon[(i + 1) % polygon.Count]), "#8b9299", group, true);
            sheet.Label(map.At(view.Project(Center(points))), instance.Id, group);
            foreach (var feature in part.Features.OrderBy(feature => feature.Id, StringComparer.Ordinal))
            {
                var edges = FeatureEdges(feature, budget).Select(edge => new Edge(
                    world(instance.Id, DesignGeometry.TransformPoint(feature.Frame, edge.A)),
                    world(instance.Id, DesignGeometry.TransformPoint(feature.Frame, edge.B))));
                var featureGroup = FeatureGroup(sheet, group, feature);
                foreach (var point in Intersections(edges, axis, section.Offset, design.Numerics.LinearTolerance, budget))
                    sheet.Circle(map.At(view.Project(point)), 2.5, feature.Operation == FeatureOperation.Subtract ? "#a52d3b" : "#234e5b", featureGroup);
            }
            sheet.Row(instance.Id + " / " + part.Id + " / " + part.MaterialId + ": declared envelope crosses plane");
        }
        DrawRoutes(sheet, design, view, map, budget);
        sheet.OverallDimensions(map, projected);
        return sheet;
    }

    static Sheet ConnectionSheet(AssemblyDesign design, Dictionary<string, PartDefinition> parts, PreviewBudget budget, Func<string, Vec3, Vec3> world)
    {
        var sheet = new Sheet(design, "connections", "Mechanical interfaces and electrical routing", budget);
        sheet.Note("Named endpoints and declared rest routes. Circuit function, movement, contact and routing clearances remain unverified.");
        var projected = design.Instances.SelectMany(instance => Envelope(parts[instance.PartId].Size).Select(point => Isometric.Project(world(instance.Id, point)))).ToArray();
        var map = new Map(projected);
        var instanceMap = design.Instances.ToDictionary(instance => instance.Id, StringComparer.Ordinal);
        foreach (var instance in design.Instances.OrderBy(instance => instance.Id, StringComparer.Ordinal))
        {
            budget.Step();
            var part = parts[instance.PartId]; var group = sheet.Group("connection-instance", ("data-instance-id", instance.Id), ("data-part-id", part.Id));
            foreach (var edge in BoxEdges(part.Size)) sheet.Line(map.At(Isometric.Project(world(instance.Id, edge.A))), map.At(Isometric.Project(world(instance.Id, edge.B))), "#bdc5ca", group, true);
            sheet.Label(map.At(Isometric.Project(world(instance.Id, Vec3.Zero))), instance.Id, group);
            foreach (var port in part.Interfaces) Port(port.Id, port.Frame.Origin, "#7d4396", "interface");
            foreach (var port in part.Terminals) Port(port.Id, port.Frame.Origin, "#b66b00", "terminal");
            void Port(string id, Vec3 origin, string color, string kind)
            {
                var position = world(instance.Id, origin); var p = map.At(Isometric.Project(position));
                var portGroup = sheet.Group(kind, group, ("data-port-id", id), ("data-world-position", Vector(position)));
                sheet.Circle(p, 4, color, portGroup); sheet.Label(p, id, portGroup);
            }
        }
        DrawRoutes(sheet, design, Isometric, map, budget);
        foreach (var joint in design.Joints.OrderBy(joint => joint.Id, StringComparer.Ordinal))
        {
            var parent = instanceMap[joint.ParentInstanceId];
            var child = instanceMap[joint.ChildInstanceId];
            var a = world(parent.Id, parts[parent.PartId].Interfaces.Single(port => port.Id == joint.ParentInterfaceId).Frame.Origin);
            var b = world(child.Id, parts[child.PartId].Interfaces.Single(port => port.Id == joint.ChildInterfaceId).Frame.Origin);
            var group = sheet.Group("joint", ("data-joint-id", joint.Id), ("data-joint-kind", joint.Kind.ToString()), ("data-parent", parent.Id), ("data-child", child.Id));
            sheet.Line(map.At(Isometric.Project(a)), map.At(Isometric.Project(b)), "#7d4396", group, true);
            sheet.Circle(map.At(Isometric.Project(a)), 7, "#7d4396", group); sheet.Label(map.At(Isometric.Project(a)), joint.Id, group);
            sheet.Row(joint.Id + " " + joint.Kind + ": " + parent.Id + "." + joint.ParentInterfaceId + " -> " + child.Id + "." + joint.ChildInterfaceId);
            var travelTolerance = joint.Kind == JointKind.Slider ? design.Numerics.LinearTolerance : design.Numerics.AngularToleranceDegrees;
            sheet.Row("  axis " + sheet.DisplayVector(joint.Axis) + "; limits " + FormatDisplay(joint.Minimum, travelTolerance) + ".." + FormatDisplay(joint.Maximum, travelTolerance) + "; rest " + FormatDisplay(joint.Rest, travelTolerance));
            sheet.Row("  pitch " + sheet.Length(joint.Pitch) + " m/rev; clearance " + sheet.Length(joint.Clearance) + " m");
        }
        foreach (var mechanism in design.Mechanisms.OrderBy(mechanism => mechanism.Id, StringComparer.Ordinal))
        {
            sheet.Row(mechanism.Id + " " + mechanism.Kind + ": " + string.Join(" -> ", mechanism.JointIds) + "; branch " + mechanism.Branch);
            foreach (var parameter in mechanism.Parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal)) sheet.Row("  " + parameter.Key + " = " + sheet.Scalar(parameter.Value));
        }
        foreach (var connection in design.Connections.OrderBy(connection => connection.Id, StringComparer.Ordinal))
            sheet.Row(connection.Id + " " + connection.Kind + ": " + EndpointText(connection.From) + " -> " + EndpointText(connection.To) + "; route " + connection.RouteId);
        return sheet;
    }

    static void DrawRoutes(Sheet sheet, AssemblyDesign design, View view, Map map, PreviewBudget budget, bool exploded = false)
    {
        var offsets = exploded ? design.Instances.ToDictionary(instance => instance.Id, instance => instance.ExplodeOffset, StringComparer.Ordinal) : null;
        foreach (var route in design.Routes.OrderBy(route => route.Id, StringComparer.Ordinal))
        {
            var offset = offsets is not null ? offsets[route.InstanceId] : Vec3.Zero;
            var group = sheet.Group("route", ("data-route-id", route.Id), ("data-instance-id", route.InstanceId), ("data-closed", route.Closed.ToString().ToLowerInvariant()));
            group.SetAttributeValue("data-route-coordinate-space", "world-metres");
            for (var i = 1; i < route.Points.Count; i++)
            {
                budget.Step(); sheet.Line(map.At(view.Project(Add(route.Points[i - 1], offset))), map.At(view.Project(Add(route.Points[i], offset))), "#b66b00", group);
            }
            if (route.Closed) sheet.Line(map.At(view.Project(Add(route.Points[^1], offset))), map.At(view.Project(Add(route.Points[0], offset))), "#b66b00", group);
            if (route.Points.Count > 0) sheet.Label(map.At(view.Project(Add(route.Points[0], offset))), route.Id, group);
            sheet.Row(route.Id + " / owner " + route.InstanceId + ": " + (route.Closed ? "closed" : EndpointText(route.From!) + " -> " + EndpointText(route.To!)));
        }
    }

    static Sheet BomSheet(AssemblyDesign design, Dictionary<string, PartDefinition> parts, PreviewBudget budget)
    {
        var sheet = new Sheet(design, "bom", "Hierarchical bill of materials", budget, true);
        sheet.Note("Every physical occurrence has its own identity. Repeated part definitions share geometry only.");
        var quantities = design.Instances.GroupBy(instance => instance.PartId).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var assembly in design.Subassemblies.OrderBy(assembly => assembly.Id, StringComparer.Ordinal))
            sheet.TableRow("subassembly", assembly.Id + " / " + assembly.Name + "; parent " + (assembly.ParentId ?? "<root>") + "; origin " + sheet.DisplayVector(assembly.Frame.Origin) + " m; XYZ " + sheet.DisplayVector(assembly.Frame.RotationDegrees, true) + " deg", ("data-subassembly-id", assembly.Id), ("data-parent", assembly.ParentId ?? ""));
        foreach (var instance in design.Instances.OrderBy(instance => instance.Id, StringComparer.Ordinal))
        {
            var part = parts[instance.PartId];
            sheet.TableRow("bom-instance", instance.Id + " / " + part.Id + " / " + part.Name + "; parent " + instance.ParentId + "; " + part.MaterialId + "; size " + sheet.DisplayVector(part.Size) + " m", ("data-instance-id", instance.Id), ("data-part-id", part.Id), ("data-parent", instance.ParentId), ("data-material-id", part.MaterialId));
        }
        foreach (var part in design.Parts.OrderBy(part => part.Id, StringComparer.Ordinal))
        {
            var quantity = quantities.GetValueOrDefault(part.Id);
            sheet.TableRow("bom-definition", part.Id + ": quantity " + quantity + "; " + part.Kind + "; " + part.Purpose + "; detail " + part.Detail, ("data-part-id", part.Id), ("data-quantity", quantity.ToString(CultureInfo.InvariantCulture)));
        }
        foreach (var material in design.Materials.OrderBy(material => material.Id, StringComparer.Ordinal))
            sheet.TableRow("bom-material", material.Id + " / " + material.Name + "; color " + material.Color + "; roughness " + sheet.Scalar(material.Roughness) + "; metallic " + sheet.Scalar(material.Metallic), ("data-material-id", material.Id));
        foreach (var observation in design.Observations.OrderBy(observation => observation.Id, StringComparer.Ordinal))
            sheet.TableRow("design-observation", observation.Id + " " + observation.Kind + ": " + observation.Description + "; source " + observation.Source, ("data-observation-id", observation.Id), ("data-observation-kind", observation.Kind.ToString()));
        sheet.TableRow("design-tolerances", "Linear tolerance " + FormatPolicy(design.Numerics.LinearTolerance) + " m; angular tolerance " + FormatPolicy(design.Numerics.AngularToleranceDegrees) + " deg; minimum feature " + FormatPolicy(design.Numerics.MinimumFeatureSize) + " m");
        return sheet;
    }

    static Sheet AssemblyPolicySheet(AssemblyDesign design, PreviewBudget budget)
    {
        var profile = design.AssemblyProfile!;
        var sheet = new Sheet(design, "assembly-policy", "Assembly fit, permitted contact and diagnostic coverage", budget, true);
        sheet.Note("These explicit v2 choices are reviewed with the whole design. Declared fits do not establish manufacturing or physics certification.");
        sheet.TableRow("diagnostic-policy", "Minimum samples per range " + profile.SamplesPerRange + "; per cycle " + profile.SamplesPerCycle + "; total sample budget " + design.Budgets.MaxMotionSamples + "; geometry checks " + profile.MaxGeometryChecks);
        sheet.TableRow("separation-policy", "Minimum surface separation " + sheet.Length(profile.MinimumSeparation) + " m. Applicable joint clearances also apply; only the listed local contact boxes permit material contact.");
        foreach (var zone in profile.Contacts.OrderBy(z => z.Id, StringComparer.Ordinal))
            sheet.TableRow("contact-zone", zone.Id + ": " + zone.FirstInstanceId + " / " + zone.SecondInstanceId + "; FIRST-instance local origin " + sheet.DisplayVector(zone.Frame.Origin) + " m; XYZ " + sheet.DisplayVector(zone.Frame.RotationDegrees, true) + " deg; size " + sheet.DisplayVector(zone.Size) + " m; reason: " + zone.Reason,
                ("data-contact-id", zone.Id), ("data-first-instance", zone.FirstInstanceId), ("data-second-instance", zone.SecondInstanceId));
        if (profile.Contacts.Count == 0) sheet.TableRow("contact-zone-none", "No material-interference exemptions are declared.");
        foreach (var fit in profile.CouplingFits.OrderBy(f => f.MechanismId, StringComparer.Ordinal))
            sheet.TableRow("coupling-fit", fit.MechanismId + ": driver pitch radius " + sheet.Length(fit.DriverPitchRadius) + " m; follower " + (fit.FollowerPitchRadius is { } radius ? sheet.Length(radius) + " m" : "not applicable")
                + "; driver teeth " + (fit.DriverToothCount?.ToString(CultureInfo.InvariantCulture) ?? "not applicable") + "; follower teeth " + (fit.FollowerToothCount?.ToString(CultureInfo.InvariantCulture) ?? "not applicable"), ("data-mechanism-id", fit.MechanismId));
        sheet.TableRow("diagnostic-limits", "Coverage is finite sampled readiness, with refined keys where required. Rigid pose-return cycles and belt material travel are separate observations. Complete review and finished animation remain later operations.");
        return sheet;
    }
    static Sheet FlexiblePolicySheet(AssemblyDesign design, PreviewBudget budget)
    {
        var sheet = new Sheet(design, "flexible-policy", "Owned flexible geometry, rigging and travel", budget, true);
        sheet.Note("Each original source ring receives an owned skin bone. Rigging does not replace or resample the approved component geometry.");
        var parts = design.Parts.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var instances = design.Instances.ToDictionary(i => i.Id, StringComparer.Ordinal);
        foreach (var policy in design.AssemblyProfile!.FlexibleRigs.OrderBy(p => p.RouteId, StringComparer.Ordinal))
        {
            var route = design.Routes.Single(r => r.Id == policy.RouteId); var part = parts[instances[route.InstanceId].PartId]; var limits = part.Flexible!;
            sheet.TableRow("flexible-rig", route.Id + " / " + route.InstanceId + " / " + part.Kind + ": " + limits.SkinSegments + " original source segments; " + (route.Closed ? "closed" : "open") + "; rest arc " + sheet.Length(limits.RestLength) + " m; arc limits " + sheet.Length(limits.MinimumLength) + ".." + sheet.Length(limits.MaximumLength) + " m", ("data-route-id", route.Id), ("data-instance-id", route.InstanceId));
            sheet.TableRow("flexible-limits", "Minimum segment ratio " + FormatPolicy(policy.MinimumSegmentRatio) + "; maximum stretch " + FormatPolicy(limits.MaximumStretchRatio) + "; minimum bend radius " + sheet.Length(limits.MinimumBendRadius) + " m; endpoint distance "
                + (policy.MinimumEndpointDistance is { } minimum && policy.MaximumEndpointDistance is { } maximum ? sheet.Length(minimum) + ".." + sheet.Length(maximum) + " m" : "not applicable to a closed belt"));
            sheet.TableRow("flexible-ownership", route.Closed ? "Material phase driver " + policy.DriverJointId + "; travel " + FormatPolicy(policy.TravelMetresPerDegree!.Value) + " metres per degree" : "Owned attachments: " + EndpointText(route.From!) + " -> " + EndpointText(route.To!));
        }
        if (design.AssemblyProfile.FlexibleRigs.Count == 0) sheet.TableRow("flexible-none", "This design declares no flexible physical components.");
        return sheet;
    }
    static string BomJson(AssemblyDesign design, Func<string, Vec3, Vec3> world)
    {
        var instancesByPart = design.Instances.GroupBy(instance => instance.PartId).ToDictionary(group => group.Key, group => group.Select(instance => instance.Id).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
        schemaVersion = "bubo-schematic-bom/v1", designId = design.Id,
        coordinateSystem = design.CoordinateSystem, numerics = design.Numerics, budgets = design.Budgets,
        subassemblies = design.Subassemblies.OrderBy(item => item.Id, StringComparer.Ordinal),
        instances = design.Instances.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => new
        {
            item.Id, item.PartId, item.ParentId, item.Frame, item.ExplodeOffset,
            worldOrigin = world(item.Id, Vec3.Zero)
        }),
        definitions = design.Parts.OrderBy(part => part.Id, StringComparer.Ordinal).Select(part => new
        {
            part.Id, part.Name, part.Purpose, part.Detail, part.MaterialId, kind = part.Kind.ToString(), part.Size,
            quantity = instancesByPart.GetValueOrDefault(part.Id)?.Length ?? 0,
            instanceIds = instancesByPart.GetValueOrDefault(part.Id) ?? [],
            featureIds = part.Features.Select(feature => feature.Id).Order(StringComparer.Ordinal)
        }),
        materials = design.Materials.OrderBy(material => material.Id, StringComparer.Ordinal),
        observations = design.Observations.OrderBy(observation => observation.Id, StringComparer.Ordinal).Select(observation => new { observation.Id, kind = observation.Kind.ToString(), observation.Description, observation.Source }),
        verification = "Declared design inventory only; construction, motion and engineering function remain unverified."
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n" });
    }

    static IEnumerable<Edge> FeatureEdges(ShapeFeature feature, PreviewBudget budget)
    {
        double P(string name) => feature.Parameters[name];
        List<Vec3[]> rings = [];
        switch (feature.Kind)
        {
            case FeatureKind.Box:
                foreach (var edge in BoxEdges(V(P("width"), P("depth"), P("height")))) { budget.Step(); yield return edge; }
                yield break;
            case FeatureKind.Cylinder:
            {
                var segments = checked((int)P("segments")); var radius = P("radius");
                for (var end = 0; end < 2; end++) rings.Add(Enumerable.Range(0, segments).Select(index => V(radius * Math.Cos(2 * Math.PI * index / segments), radius * Math.Sin(2 * Math.PI * index / segments), end * P("length"))).ToArray());
                break;
            }
            case FeatureKind.Extrude:
                rings.Add(feature.Profile.Select(point => V(point.X, point.Y, 0)).ToArray());
                rings.Add(feature.Profile.Select(point => V(point.X, point.Y, P("depth"))).ToArray());
                break;
            case FeatureKind.Revolve:
            {
                var segments = checked((int)P("segments")); var angle = P("angleDegrees") * Math.PI / 180;
                for (var index = 0; index <= segments; index++)
                {
                    budget.Step();
                    var theta = angle * index / segments;
                    rings.Add(feature.Profile.Select(point => V(point.X * Math.Cos(theta), point.X * Math.Sin(theta), point.Y)).ToArray());
                }
                break;
            }
            case FeatureKind.Sweep:
            {
                var segments = checked((int)P("segments")); var radius = P("radius");
                for (var index = 0; index < feature.Path.Count; index++)
                {
                    budget.Step();
                    var tangent = Unit(Sub(feature.Path[Math.Min(index + 1, feature.Path.Count - 1)], feature.Path[Math.Max(index - 1, 0)]));
                    var basis = Unit(Cross(tangent, Math.Abs(tangent.Z) < .9 ? V(0, 0, 1) : V(0, 1, 0)));
                    var other = Cross(tangent, basis); var center = feature.Path[index];
                    rings.Add(Enumerable.Range(0, segments).Select(i => Add(center, Add(Mul(basis, radius * Math.Cos(2 * Math.PI * i / segments)), Mul(other, radius * Math.Sin(2 * Math.PI * i / segments))))).ToArray());
                }
                break;
            }
            case FeatureKind.Loft:
                foreach (var section in feature.Sections) rings.Add(section.Profile.Select(point => DesignGeometry.TransformPoint(section.Frame, V(point.X, point.Y, 0))).ToArray());
                break;
            default: throw new InvalidOperationException("Unsupported schematic feature.");
        }
        for (var ring = 0; ring < rings.Count; ring++)
        {
            for (var i = 0; i < rings[ring].Length; i++)
            {
                budget.Step(); yield return new(rings[ring][i], rings[ring][(i + 1) % rings[ring].Length]);
                if (ring > 0)
                {
                    if (rings[ring - 1].Length != rings[ring].Length) throw new InvalidOperationException("Loft sections require corresponding profile vertices.");
                    budget.Step(); yield return new(rings[ring - 1][i], rings[ring][i]);
                }
            }
        }
    }

    static IEnumerable<Vec3> Intersections(IEnumerable<Edge> edges, SectionAxis axis, double offset, double tolerance, PreviewBudget budget)
    {
        static double Coordinate(Vec3 point, SectionAxis axis) => axis == SectionAxis.X ? point.X : axis == SectionAxis.Y ? point.Y : point.Z;
        foreach (var edge in edges)
        {
            budget.Step();
            var a = Coordinate(edge.A, axis) - offset; var b = Coordinate(edge.B, axis) - offset;
            if (Math.Abs(a) <= tolerance) yield return edge.A;
            if (Math.Abs(b) <= tolerance) yield return edge.B;
            if ((a < -tolerance && b > tolerance) || (a > tolerance && b < -tolerance)) yield return Add(edge.A, Mul(Sub(edge.B, edge.A), -a / (b - a)));
        }
    }
    static List<P2> ConvexHull(IEnumerable<P2> source)
    {
        var points = source.Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        if (points.Length <= 2) return [.. points];
        static double Turn(P2 a, P2 b, P2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var lower = new List<P2>(); var upper = new List<P2>();
        foreach (var point in points) { while (lower.Count >= 2 && Turn(lower[^2], lower[^1], point) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(point); }
        foreach (var point in points.Reverse()) { while (upper.Count >= 2 && Turn(upper[^2], upper[^1], point) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(point); }
        lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1); return [.. lower, .. upper];
    }
    static Vec3[] Envelope(Vec3 size) => [V(0, 0, 0), V(size.X, 0, 0), V(size.X, size.Y, 0), V(0, size.Y, 0), V(0, 0, size.Z), V(size.X, 0, size.Z), V(size.X, size.Y, size.Z), V(0, size.Y, size.Z)];
    static IEnumerable<Edge> BoxEdges(Vec3 size)
    {
        var p = Envelope(size);
        foreach (var (a, b) in new[] { (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7) }) yield return new(p[a], p[b]);
    }
    static Vec3 World(Func<string, Vec3, Vec3> world, PartInstance instance, Vec3 local, bool exploded) => Add(world(instance.Id, local), exploded ? instance.ExplodeOffset : Vec3.Zero);
    static string EndpointText(Endpoint endpoint) => endpoint.InstanceId + "." + endpoint.PortId;
    static Vec3 V(double x, double y, double z) => new() { X = x, Y = y, Z = z };
    static Vec3 Add(Vec3 a, Vec3 b) => V(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    static Vec3 Sub(Vec3 a, Vec3 b) => V(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    static Vec3 Mul(Vec3 a, double scalar) => V(a.X * scalar, a.Y * scalar, a.Z * scalar);
    static Vec3 Cross(Vec3 a, Vec3 b) => V(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static Vec3 Unit(Vec3 value) { var length = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z); if (length <= 0) throw new InvalidOperationException("Degenerate schematic path tangent."); return Mul(value, 1 / length); }
    static Vec3 Center(IEnumerable<Vec3> points) { var array = points.ToArray(); return V(array.Average(point => point.X), array.Average(point => point.Y), array.Average(point => point.Z)); }
    static string Vector(Vec3 point) => N(point.X) + ", " + N(point.Y) + ", " + N(point.Z);
    static string N(double value) => double.IsFinite(value) ? value.ToString("G17", CultureInfo.InvariantCulture) : throw new InvalidOperationException("Nonfinite schematic geometry.");
    internal static string FormatPolicy(double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("Finite schematic policy value required.");
        var plain = value.ToString("0.###############################", CultureInfo.InvariantCulture);
        return double.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed == value
            ? plain : value.ToString("R", CultureInfo.InvariantCulture);
    }
    internal static string FormatDisplay(double value, double tolerance)
    {
        if (!double.IsFinite(value) || !double.IsFinite(tolerance) || tolerance <= 0)
            throw new InvalidOperationException("Schematic display values require finite values and a positive tolerance.");
        var digits = (int)Math.Clamp(Math.Ceiling(-Math.Log10(tolerance)), 0, 15);
        var rounded = Math.Round(value, digits, MidpointRounding.ToEven);
        var candidate = rounded == 0 ? "0" : rounded.ToString(digits == 0 ? "0" : "0." + new string('#', digits), CultureInfo.InvariantCulture);
        if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) && Math.Abs(parsed - value) <= tolerance)
            return candidate;
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
    static string Pixel(double value) => double.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : throw new InvalidOperationException("Nonfinite drawing coordinate.");

    sealed class Map
    {
        public readonly double MinX, MinY, MaxX, MaxY, Scale;
        public Map(IEnumerable<P2> source)
        {
            var points = source.ToArray();
            if (points.Length == 0) throw new InvalidOperationException("No schematic geometry.");
            MinX = points.Min(point => point.X); MinY = points.Min(point => point.Y); MaxX = points.Max(point => point.X); MaxY = points.Max(point => point.Y);
            Scale = Math.Min(800 / Math.Max(MaxX - MinX, .001), 560 / Math.Max(MaxY - MinY, .001));
        }
        public P2 At(P2 point) => new(100 + (800 - (MaxX - MinX) * Scale) / 2 + (point.X - MinX) * Scale, 220 + (560 - (MaxY - MinY) * Scale) / 2 + (MaxY - point.Y) * Scale);
    }

    sealed class PreviewBudget(long maxBytes, long maxWork, CancellationToken cancellation)
    {
        long estimated, committed, work;
        public void Step()
        {
            cancellation.ThrowIfCancellationRequested();
            if (++work > Math.Min(maxWork, 4_000_000)) throw new InvalidOperationException("Schematic preview exceeds its bounded work allowance.");
        }
        public void Element(XElement element)
        {
            Step();
            estimated = checked(estimated + Encoding.UTF8.GetByteCount(element.ToString(SaveOptions.DisableFormatting)));
            if (estimated > maxBytes) throw new InvalidOperationException("Schematic preview exceeds MaxPreviewBytes while drawing.");
        }
        public void Commit(string content)
        {
            Step();
            committed = checked(committed + Encoding.UTF8.GetByteCount(content));
            if (committed > maxBytes) throw new InvalidOperationException("Schematic preview exceeds MaxPreviewBytes.");
        }
    }

    sealed class Sheet
    {
        public XElement Root { get; }
        readonly PreviewBudget budget;
        readonly bool table;
        readonly double linearTolerance, angularTolerance;
        readonly XElement header, notesGroup, content;
        double headerBottom;
        int notes, rows, tables;
        public Sheet(AssemblyDesign design, string kind, string title, PreviewBudget budget, bool table = false)
        {
            this.budget = budget; this.table = table;
            linearTolerance = design.Numerics.LinearTolerance; angularTolerance = design.Numerics.AngularToleranceDegrees;
            Root = new XElement(Svg + "svg", new XAttribute("width", "1500"), new XAttribute("height", "1000"), new XAttribute("viewBox", "0 0 1500 1000"),
                new XAttribute("role", "img"), new XAttribute("data-sheet-kind", kind), new XAttribute("data-design-id", design.Id), new XAttribute("data-style-sha256", design.StyleSha256),
                new XElement(Svg + "title", design.Name + " — " + title), new XElement(Svg + "desc", "Dimension-driven schematic. Metres, right-handed Z-up. Design declarations are not measured constructed geometry or physical-function evidence."));
            header = Group("sheet-header", Root);
            notesGroup = Group("sheet-notes", Root);
            content = Group("sheet-content", Root);
            headerBottom = 22;
            Header(design.Name + " — " + title, 23, 58, 30);
            Header(design.Id + " | " + design.CoordinateSystem + " | dimensions in metres", 14, 90, 23);
            Header("Tolerance: " + FormatPolicy(design.Numerics.LinearTolerance) + " m / " + FormatPolicy(design.Numerics.AngularToleranceDegrees) + " deg; style " + design.StyleSha256, 12, 115, 21);
        }
        public string Length(double value) => FormatDisplay(value, linearTolerance);
        public string Angle(double value) => FormatDisplay(value, angularTolerance);
        public string Scalar(double value) => FormatDisplay(value, Math.Min(1e-9, Math.Min(linearTolerance, angularTolerance)));
        public string DisplayVector(Vec3 point, bool angular = false) => angular
            ? Angle(point.X) + ", " + Angle(point.Y) + ", " + Angle(point.Z)
            : Length(point.X) + ", " + Length(point.Y) + ", " + Length(point.Z);
        void Header(string text, int size, int width, int lineHeight)
        {
            foreach (var line in Wrap(text, width))
            {
                headerBottom += lineHeight;
                Text(60, headerBottom, line, size, header);
            }
        }
        void Add(XElement parent, XElement element) { budget.Element(element); parent.Add(element); }
        public XElement Group(string kind, params (string Name, string Value)[] attributes) => Group(kind, content, attributes);
        public XElement Group(string kind, XElement parent, params (string Name, string Value)[] attributes)
        {
            var group = new XElement(Svg + "g", new XAttribute("data-kind", kind));
            foreach (var (name, value) in attributes) group.SetAttributeValue(name, value);
            Add(parent, group); return group;
        }
        public void Note(string text) { foreach (var line in Wrap(text, 115)) Text(60, headerBottom + 25 + 21 * notes++, line, 12, notesGroup); }
        public void Row(string text) { foreach (var line in Wrap(text, 46)) Text(1010, 225 + 20 * rows++, line, 10, content); }
        public void TableRow(string kind, string text, params (string Name, string Value)[] attributes)
        {
            var group = Group(kind, attributes);
            foreach (var line in Wrap(text, 115)) Text(60, 220 + 25 * tables++, line, 12, group);
        }
        static IEnumerable<string> Wrap(string text, int width)
        {
            if (text.Length == 0) { yield return ""; yield break; }
            var offset = 0;
            while (offset < text.Length)
            {
                var count = Math.Min(width, text.Length - offset);
                if (offset + count < text.Length)
                {
                    var space = text.LastIndexOf(' ', offset + count - 1, count);
                    if (space > offset) count = space - offset;
                    if (char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) count--;
                }
                yield return text.Substring(offset, count);
                offset += count;
                while (offset < text.Length && text[offset] == ' ') offset++;
            }
        }
        public void Line(P2 a, P2 b, string color, XElement parent, bool dashed = false)
        {
            var line = new XElement(Svg + "line", new XAttribute("x1", Pixel(a.X)), new XAttribute("y1", Pixel(a.Y)), new XAttribute("x2", Pixel(b.X)), new XAttribute("y2", Pixel(b.Y)), new XAttribute("stroke", color), new XAttribute("stroke-width", "1.5"));
            if (dashed) line.SetAttributeValue("stroke-dasharray", "5 4"); Add(parent, line);
        }
        public void Circle(P2 point, double radius, string color, XElement parent) => Add(parent, new XElement(Svg + "circle", new XAttribute("cx", Pixel(point.X)), new XAttribute("cy", Pixel(point.Y)), new XAttribute("r", Pixel(radius)), new XAttribute("fill", "none"), new XAttribute("stroke", color), new XAttribute("stroke-width", "1.5")));
        public void Label(P2 point, string text, XElement parent) => Text(point.X + 6, point.Y - 6, text, 11, parent);
        void Text(double x, double y, string text, int size, XElement parent) => Add(parent, new XElement(Svg + "text", new XAttribute("x", Pixel(x)), new XAttribute("y", Pixel(y)), new XAttribute("font-family", "sans-serif"), new XAttribute("font-size", size), new XAttribute("fill", "#17323a"), text));
        public void Dimension(P2 a, P2 b, string label, string kind, double value, bool verticalLabel = false)
        {
            var group = Group("dimension", ("data-dimension-kind", kind), ("data-value", N(value)));
            Line(a, b, "#52646c", group); Circle(a, 2, "#52646c", group); Circle(b, 2, "#52646c", group);
            var center = new P2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            if (verticalLabel)
            {
                var x = center.X - 10;
                Add(group, new XElement(Svg + "text", new XAttribute("x", Pixel(x)), new XAttribute("y", Pixel(center.Y)),
                    new XAttribute("font-family", "sans-serif"), new XAttribute("font-size", "11"), new XAttribute("fill", "#17323a"),
                    new XAttribute("text-anchor", "middle"), new XAttribute("transform", "rotate(-90 " + Pixel(x) + " " + Pixel(center.Y) + ")"), label));
            }
            else Label(center, label, group);
        }
        public void OverallDimensions(Map map, P2[] projected)
        {
            _ = projected;
            Dimension(new(100, 850), new(900, 850), "Projection width " + Length(map.MaxX - map.MinX) + " m", "projection-width", map.MaxX - map.MinX);
            Dimension(new(940, 220), new(940, 780), "Height " + Length(map.MaxY - map.MinY) + " m", "projection-height", map.MaxY - map.MinY, true);
        }
        public string Finish()
        {
            budget.Step();
            // Notes can be added after drawing (for example flexible-part limitations). Lay out
            // the entire content region only after the final wrapped header/note count is known.
            var notesBottom = notes == 0 ? headerBottom : headerBottom + 25 + 21 * (notes - 1) + 12;
            var displacement = Math.Max(0, notesBottom + 35 - 200);
            content.SetAttributeValue("transform", "translate(0 " + Pixel(displacement) + ")");
            var contentBottom = table ? 260 + tables * 25 : Math.Max(900, 280 + rows * 20);
            var height = Math.Max(1000, Math.Ceiling(Math.Max(notesBottom, contentBottom + displacement) + 45));
            Root.SetAttributeValue("height", height.ToString(CultureInfo.InvariantCulture)); Root.SetAttributeValue("viewBox", "0 0 1500 " + height.ToString(CultureInfo.InvariantCulture));
            return Root.ToString(SaveOptions.DisableFormatting);
        }
    }
}
