using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;

namespace Bubo.Schematic.Tests;

public static class PreviewTests
{
    static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    static PreviewCompiler Compiler() => new(new AssemblyDesignValidator(), new SvgSchematicRenderer());
    public static void Run()
    {
        Check.Case("schematic tolerance policy labels retain their exact values", () =>
        {
            foreach (var value in new[] { 1e-6, 1e-16, 1e-100, .025, Math.PI * 1e-9 })
                Check.True(double.Parse(SvgSchematicRenderer.FormatPolicy(value), CultureInfo.InvariantCulture) == value);
            var part = DesignExamples.Part("box", DesignExamples.V(1,1,1), DesignExamples.Box(1,1,1));
            var design = DesignExamples.Base("exact-policy", [part], [DesignExamples.Instance("body", part.Id)]);
            design = design with { Numerics = design.Numerics with { LinearTolerance = 1e-16, AngularToleranceDegrees = .025 } };
            var files = Compiler().Compile(design);
            var partSheet = XDocument.Parse(files["part-box.svg"]).Root!;
            Check.True(partSheet.Value.Contains("Tolerance: 0.0000000000000001 m / 0.025 deg", StringComparison.Ordinal));
            var bom = XDocument.Parse(files["bom.svg"]).Root!;
            Check.True(bom.Value.Contains("Linear tolerance 0.0000000000000001 m; angular tolerance 0.025 deg", StringComparison.Ordinal));
        });
        Check.Case("schematic previews include the whole vehicle, device and vessel drawing inventory", () =>
        {
            foreach (var design in new[] { DesignExamples.Vehicle(), DesignExamples.Device(), DesignExamples.Vessel() })
            {
                var files = Compiler().Compile(design);
                Check.True(PreviewCompiler.RequiredFiles(design).SetEquals(files.Keys));
                Check.True(files.Count == 10 + design.Parts.Count + design.Sections.Count);
                foreach (var (name, content) in files)
                {
                    Check.True(!name.Contains('/') && !name.Contains('\\'));
                    if (name.EndsWith(".svg", StringComparison.Ordinal)) SafeSvg(content);
                }
                foreach (var direction in new[] { "front", "right", "back", "left", "top", "bottom" })
                {
                    var root = XDocument.Parse(files[$"orthographic-{direction}.svg"]).Root!;
                    var instances = Kind(root, "part-instance").ToArray();
                    Check.True(instances.Length == design.Instances.Count);
                    Check.True(design.Instances.All(instance => instances.Any(item => (string?)item.Attribute("data-instance-id") == instance.Id && (string?)item.Attribute("data-part-id") == instance.PartId)));
                    Check.True(Kind(root, "dimension").Count() == 2);
                }
            }
        });
        Check.Case("schematic part sheets depict every supported procedural feature and explicit dimensions", () =>
        {
            var kinds = new HashSet<FeatureKind>();
            foreach (var design in new[] { DesignExamples.Vehicle(), DesignExamples.Device(), DesignExamples.Vessel() })
            {
                var files = Compiler().Compile(design);
                foreach (var part in design.Parts)
                {
                    var root = XDocument.Parse(files[$"part-{part.Id}.svg"]).Root!;
                    Check.True((string?)root.Attribute("data-material-id") == part.MaterialId);
                    var dimensions = Kind(root, "dimension").ToDictionary(item => item.Attribute("data-dimension-kind")!.Value);
                    Check.True((double?)dimensions["part-size-x"].Attribute("data-value") == part.Size.X);
                    Check.True((double?)dimensions["part-size-y"].Attribute("data-value") == part.Size.Y);
                    Check.True((double?)dimensions["part-size-z"].Attribute("data-value") == part.Size.Z);
                    foreach (var feature in part.Features)
                    {
                        kinds.Add(feature.Kind);
                        var group = Kind(root, "feature").Single(item => (string?)item.Attribute("data-feature-id") == feature.Id);
                        Check.True((string?)group.Attribute("data-feature-kind") == feature.Kind.ToString());
                        Check.True(group.Elements(Svg + "line").Any(), "A declared feature needs actual projected line geometry.");
                    }
                }
            }
            Check.True(kinds.SetEquals(Enum.GetValues<FeatureKind>()), "Fixture drawing coverage must include every supported feature family.");
        });
        Check.Case("schematic projections preserve nested rotations and independently placed repeated instances", () =>
        {
            var part = DesignExamples.Part("block", DesignExamples.V(.3, .2, .1), DesignExamples.Box(.3, .2, .1));
            var design = DesignExamples.Base("rotated-assembly", [part],
                [DesignExamples.Instance("nested", part.Id, 4, 5, 6, "angled"), DesignExamples.Instance("separate", part.Id, 1, 0, 0)]) with
            {
                Subassemblies =
                [
                    new() { Id="assembly", Name="Root", ParentId=null, Frame=new() {Origin=DesignExamples.V(10,20,30),RotationDegrees=DesignExamples.V(0,0,90)} },
                    new() { Id="angled", Name="Angled child", ParentId="assembly", Frame=new() {Origin=DesignExamples.V(1,2,3),RotationDegrees=DesignExamples.V(90,0,0)} }
                ],
                Sections=[new(){Id="middle",Axis=SectionAxis.Z,Offset=38.1}]
            };
            var files = Compiler().Compile(design);
            var root = XDocument.Parse(files["orthographic-front.svg"]).Root!;
            var nested = Kind(root, "part-instance").Single(item => (string?)item.Attribute("data-instance-id") == "nested");
            var independent = Kind(root, "part-instance").Single(item => (string?)item.Attribute("data-instance-id") == "separate");
            NearVector(nested.Attribute("data-world-origin")!.Value, 14,25,38);
            NearVector(independent.Attribute("data-world-origin")!.Value, 10,21,30);
            Check.True(nested.Elements().Any() && independent.Elements().Any());
        });
        Check.Case("schematic openings retain subtractive geometry and explicit depth in part review", () =>
        {
            var opening = new ShapeFeature
            {
                Id="through-hole",Kind=FeatureKind.Cylinder,Operation=FeatureOperation.Subtract,
                Frame=DesignExamples.F(.5,.5,0),Parameters=new(){{"radius",.1},{"length",.2},{"segments",16}},
                Profile=[],Path=[],Sections=[]
            };
            var part=DesignExamples.Part("plate",DesignExamples.V(1,1,.2),DesignExamples.Box(1,1,.2),opening);
            var design=DesignExamples.Base("plate-opening",[part],[DesignExamples.Instance("plate-instance",part.Id)]);
            var root=XDocument.Parse(Compiler().Compile(design)["part-plate.svg"]).Root!;
            var subtract=Kind(root,"feature").Single(item=>(string?)item.Attribute("data-feature-id")==opening.Id);
            Check.True((string?)subtract.Attribute("data-operation")=="Subtract");
            Check.True(subtract.Elements(Svg+"line").All(item=>(string?)item.Attribute("stroke-dasharray")=="5 4"));
            Check.True((double?)subtract.Attribute("data-parameter-length")==.2);
        });
        Check.Case("schematic exploded placement applies each independent world-space offset", () =>
        {
            var part = DesignExamples.Part("fastener", DesignExamples.V(.02,.02,.04), DesignExamples.Box(.02,.02,.04));
            var design = DesignExamples.Base("exploded-bolts", [part],
                [DesignExamples.Instance("bolt-a",part.Id) with {ExplodeOffset=DesignExamples.V(.1,0,.2)}, DesignExamples.Instance("bolt-b",part.Id,.2,0,0) with {ExplodeOffset=DesignExamples.V(-.1,.3,0)}]);
            var files = Compiler().Compile(design);
            var exploded = XDocument.Parse(files["exploded.svg"]).Root!;
            NearVector(Kind(exploded,"part-instance").Single(item=>(string?)item.Attribute("data-instance-id")=="bolt-a").Attribute("data-world-origin")!.Value,.1,0,.2);
            NearVector(Kind(exploded,"part-instance").Single(item=>(string?)item.Attribute("data-instance-id")=="bolt-b").Attribute("data-world-origin")!.Value,.1,.3,0);
            using var bom = JsonDocument.Parse(files["bom.json"]);
            var definition = bom.RootElement.GetProperty("definitions").EnumerateArray().Single();
            Check.True(definition.GetProperty("quantity").GetInt32()==2);
            Check.True(definition.GetProperty("instanceIds").GetArrayLength()==2);
            Check.True(bom.RootElement.GetProperty("instances").GetArrayLength()==2);
        });
        Check.Case("schematic section cuts select actual declared envelope crossings and label their limits", () =>
        {
            var part = DesignExamples.Part("block",DesignExamples.V(1,1,1),DesignExamples.Box(1,1,1));
            var design = DesignExamples.Base("section-selection",[part],[DesignExamples.Instance("crossing",part.Id),DesignExamples.Instance("outside",part.Id,3)]) with
            { Sections=[new(){Id="cut",Axis=SectionAxis.X,Offset=.5}] };
            var root=XDocument.Parse(Compiler().Compile(design)["section-cut.svg"]).Root!;
            Check.True((string?)root.Attribute("data-section-mode")=="declared-envelope-and-feature-intersections");
            var crossing=Kind(root,"section-instance").Single();
            Check.True((string?)crossing.Attribute("data-instance-id")=="crossing");
            Check.True(crossing.Elements(Svg+"line").Count()==4);
            Check.True(Kind(crossing,"feature").Single().Elements(Svg+"circle").Any());
            Check.True(string.Join(' ',root.Descendants(Svg+"text").Select(text=>text.Value)).Contains("boolean solid sections await construction",StringComparison.Ordinal));
        });
        Check.Case("schematic routing and joint sheets retain named terminals and actual declared endpoints", () =>
        {
            var design=DesignExamples.Device();
            var files=Compiler().Compile(design);
            var root=XDocument.Parse(files["connections.svg"]).Root!;
            Check.True(Kind(root,"joint").Count()==design.Joints.Count);
            Check.True(Kind(root,"terminal").Count()==design.Instances.Sum(instance=>design.Parts.Single(part=>part.Id==instance.PartId).Terminals.Count));
            foreach(var route in design.Routes)
            {
                var group=Kind(root,"route").Single(item=>(string?)item.Attribute("data-route-id")==route.Id);
                Check.True((string?)group.Attribute("data-instance-id")==route.InstanceId);
                Check.True((string?)group.Attribute("data-route-coordinate-space")=="world-metres");
                Check.True(group.Elements(Svg+"line").Count()==route.Points.Count-1+(route.Closed?1:0));
            }
            foreach(var connection in design.Connections)
            {
                Check.True(root.Value.Contains(connection.From.InstanceId+"."+connection.From.PortId,StringComparison.Ordinal));
                Check.True(root.Value.Contains(connection.To.InstanceId+"."+connection.To.PortId,StringComparison.Ordinal));
            }
        });
        Check.Case("schematic BOM visibly discloses every assumption and material without executing labels", () =>
        {
            var design=DesignExamples.Device() with {Name="Device <script>alert(1)</script> & label"};
            var files=Compiler().Compile(design);
            foreach(var content in files.Where(item=>item.Key.EndsWith(".svg",StringComparison.Ordinal)).Select(item=>item.Value)) SafeSvg(content);
            var bom=XDocument.Parse(files["bom.svg"]).Root!;
            Check.True(Kind(bom,"bom-instance").Count()==design.Instances.Count);
            Check.True(Kind(bom,"bom-material").Count()==design.Materials.Count);
            Check.True(Kind(bom,"design-observation").Count()==design.Observations.Count);
            Check.True(bom.Value.Contains("Linear tolerance",StringComparison.Ordinal));
            Check.True(bom.Element(Svg+"title")!.Value.Contains("<script>alert(1)</script>",StringComparison.Ordinal));
        });
        Check.Case("schematic drawing output is byte-deterministic and independent of current culture", () =>
        {
            var design=DesignExamples.Vehicle(); var before=CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR"); var first=Compiler().Compile(design);
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("en-US"); var second=Compiler().Compile(design);
                Check.True(first.Keys.SequenceEqual(second.Keys,StringComparer.Ordinal));
                Check.True(first.All(item=>second[item.Key]==item.Value));
            }
            finally {CultureInfo.CurrentCulture=before;}
        });
        Check.Case("schematic long design text clears geometry and stays inside the expanded viewport", () =>
        {
            static string LongText(string word) => string.Join(' ', Enumerable.Repeat(word, 240));
            var part=DesignExamples.Part("long-detail",DesignExamples.V(1,1,1),DesignExamples.Box(1,1,1)) with
            {Purpose=LongText("purpose"),Detail=LongText("detail")};
            var design=DesignExamples.Base("long-review",[part],[DesignExamples.Instance("component",part.Id)]) with
            {
                Observations=[new(){Id="long-assumption",Kind=ObservationKind.Assumed,Description=LongText("assumed"),Source=LongText("source")}]
            };
            var files=Compiler().Compile(design);
            var drawing=XDocument.Parse(files["part-long-detail.svg"]).Root!;
            var notes=Kind(drawing,"sheet-notes").Single();
            var content=Kind(drawing,"sheet-content").Single();
            var offset=TranslationY(content);
            var notesBottom=notes.Descendants(Svg+"text").Max(text=>(double)text.Attribute("y")!+(double)text.Attribute("font-size")!*.3);
            var contentTop=content.Descendants().SelectMany(element=>
            {
                if(element.Name==Svg+"line") return new[]{(double)element.Attribute("y1")!,(double)element.Attribute("y2")!};
                if(element.Name==Svg+"circle") return new[]{(double)element.Attribute("cy")!-(double)element.Attribute("r")!};
                if(element.Name==Svg+"text") return new[]{(double)element.Attribute("y")!-(double)element.Attribute("font-size")!};
                return Array.Empty<double>();
            }).Min()+offset;
            Check.True(contentTop>notesBottom+10,"Long design notes overlap actual diagram or schedule coordinates.");
            Check.True(contentTop>500,"Long notes must visibly move the drawing below their actual extent.");
            foreach(var name in new[]{"part-long-detail.svg","bom.svg"})
            {
                var root=XDocument.Parse(files[name]).Root!;
                var viewportHeight=double.Parse(root.Attribute("viewBox")!.Value.Split(' ')[3],CultureInfo.InvariantCulture);
                foreach(var text in root.Descendants(Svg+"text"))
                {
                    var y=(double)text.Attribute("y")!+text.Ancestors().Where(group=>(string?)group.Attribute("data-kind")=="sheet-content").Sum(TranslationY);
                    Check.True(y+(double)text.Attribute("font-size")!*.3<viewportHeight,"Review text is clipped by the viewport.");
                }
            }
        });
        Check.Case("schematic display rounds within approved tolerance while retaining exact numeric metadata", () =>
        {
            foreach(var (value,tolerance) in new[]{(.1+.2,1e-6),(.03,1e-6),(1e-6,1e-8),(123456.789123456,1e-6),(-1.23456789012345,1e-9),(1.0000000000000002,1e-18)})
            {
                var displayed=SvgSchematicRenderer.FormatDisplay(value,tolerance);
                var parsed=double.Parse(displayed,CultureInfo.InvariantCulture);
                Check.True(Math.Abs(parsed-value)<=tolerance,"Displayed rounding exceeds the declared numeric tolerance.");
            }
            Check.True(SvgSchematicRenderer.FormatDisplay(1.0000000000000002,1e-18)=="1.0000000000000002","Tight tolerances require an exact shortest round-trip fallback.");
            Check.True(SvgSchematicRenderer.FormatDisplay(-1e-17,1e-6)=="0");
            var width=.1+.2;
            var part=DesignExamples.Part("readable-dimensions",DesignExamples.V(width,.8,.03),DesignExamples.Box(width,.8,.03));
            var design=DesignExamples.Base("readable-numbers",[part],[DesignExamples.Instance("component",part.Id)]);
            var root=XDocument.Parse(Compiler().Compile(design)["part-readable-dimensions.svg"]).Root!;
            var dimension=Kind(root,"dimension").Single(item=>(string?)item.Attribute("data-dimension-kind")=="part-size-x");
            Check.True(dimension.Elements(Svg+"text").Single().Value=="X 0.3 m");
            Check.True((double)dimension.Attribute("data-value")! == width && (double)dimension.Attribute("data-value")! != .3,"Numeric metadata was rounded with the display label.");
            var feature=Kind(root,"feature").Single();
            Check.True((double)feature.Attribute("data-parameter-width")! == width);
            Check.True(!root.Value.Contains("0.30000000000000004",StringComparison.Ordinal)&&!root.Value.Contains("0.80000000000000004",StringComparison.Ordinal));
            Check.True(root.Value.Contains("Tolerance: 0.000001 m / 0.01 deg",StringComparison.Ordinal));
        });
        Check.Case("schematic projection height labels rotate within their reserved dimension column", () =>
        {
            var root=XDocument.Parse(Compiler().Compile(DesignExamples.Device())["orthographic-front.svg"]).Root!;
            var dimension=Kind(root,"dimension").Single(item=>(string?)item.Attribute("data-dimension-kind")=="projection-height");
            var label=dimension.Elements(Svg+"text").Single();
            Check.True(label.Attribute("transform")!.Value.StartsWith("rotate(-90 ",StringComparison.Ordinal));
            Check.True((string?)label.Attribute("text-anchor")=="middle");
            var x=(double)label.Attribute("x")!;
            var line=dimension.Elements(Svg+"line").Single();
            Check.True(x>900&&x<(double)line.Attribute("x1")!&&x<1010,"Height label must stay between the drawing and schedule.");
        });
        Check.Case("schematic compiler rejects omitted drawings and unsafe renderer filenames", () =>
        {
            var design=DesignExamples.Device();
            var valid=Compiler().Compile(design).ToDictionary(item=>item.Key,item=>item.Value,StringComparer.Ordinal);
            var omitted=new Dictionary<string,string>(valid); omitted.Remove("exploded.svg");
            Check.Throws<InvalidOperationException>(()=>new PreviewCompiler(new PassValidator(),new FixedRenderer(omitted)).Compile(design));
            var first=design.Parts[0];
            var unsafeDesign=design with {Parts=[first with {Id="../outside"},.. design.Parts.Skip(1)]};
            var unsafeFiles=PreviewCompiler.RequiredFiles(unsafeDesign).ToDictionary(name=>name,_=>"safe content",StringComparer.Ordinal);
            Check.Throws<InvalidOperationException>(()=>new PreviewCompiler(new PassValidator(),new FixedRenderer(unsafeFiles)).Compile(unsafeDesign));
        });
        Check.Case("schematic preview enforces cumulative bytes drawings work expansion and cancellation", () =>
        {
            var design=DesignExamples.Device(); var valid=Compiler().Compile(design);
            var exactBytes=valid.Values.Sum(value=>(long)Encoding.UTF8.GetByteCount(value));
            var shortBytes=design with {Budgets=design.Budgets with {MaxPreviewBytes=exactBytes-1}};
            Check.Throws<InvalidOperationException>(()=>new PreviewCompiler(new PassValidator(),new FixedRenderer(valid)).Compile(shortBytes));
            var lowDrawings=design with {Budgets=design.Budgets with {MaxDrawings=1}};
            Check.Throws<InvalidOperationException>(()=>new PreviewCompiler(new PassValidator(),new FixedRenderer(valid)).Compile(lowDrawings));
            var lowWork=design with {Budgets=design.Budgets with {MaxValidationChecks=1}};
            Check.Throws<InvalidOperationException>(()=>new SvgSchematicRenderer(new PassValidator()).Render(lowWork));
            var cancelled=new CancellationToken(true);
            Check.Throws<OperationCanceledException>(()=>Compiler().Compile(design,cancelled));
        });
    }
    static IEnumerable<XElement> Kind(XElement root,string kind)=>root.DescendantsAndSelf().Where(item=>(string?)item.Attribute("data-kind")==kind);
    static void SafeSvg(string content)
    {
        var root=XDocument.Parse(content).Root!;
        Check.True(root.Name==Svg+"svg");
        Check.True(!root.DescendantsAndSelf().Any(item=>item.Name.LocalName is "script" or "foreignObject" or "image"));
        Check.True(!root.DescendantsAndSelf().Attributes().Any(attribute=>attribute.Name.LocalName is "href" or "src" || attribute.Name.LocalName.StartsWith("on",StringComparison.OrdinalIgnoreCase)));
    }
    static void NearVector(string text,double x,double y,double z)
    {
        var actual=text.Split(',').Select(value=>double.Parse(value,CultureInfo.InvariantCulture)).ToArray();
        Check.True(actual.Length==3&&Math.Abs(actual[0]-x)<1e-10&&Math.Abs(actual[1]-y)<1e-10&&Math.Abs(actual[2]-z)<1e-10,"Nested world placement changed.");
    }
    static double TranslationY(XElement group)
    {
        var transform=group.Attribute("transform")?.Value;
        if(transform is null) return 0;
        return double.Parse(transform["translate(0 ".Length..^1],CultureInfo.InvariantCulture);
    }
    sealed class FixedRenderer(IReadOnlyDictionary<string,string> files):ISchematicPreviewRenderer
    {public IReadOnlyDictionary<string,string> Render(AssemblyDesign design,CancellationToken cancellation=default)=>files;}
    sealed class PassValidator:IAssemblyDesignValidator
    {
        public ValidationReport Validate(AssemblyDesign design,CancellationToken cancellationToken=default)=>new([],0);
        public void RequireValid(AssemblyDesign design,CancellationToken cancellationToken=default){}
    }
}
