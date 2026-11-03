using Bubo.Schematic.Domain;

namespace Bubo.Schematic.Tests;

/// <summary>Owned design fixtures; synthetic styles/decisions do not approve production assets.</summary>
public static class DesignExamples
{
    public static Vec3 V(double x = 0, double y = 0, double z = 0) => new() { X=x, Y=y, Z=z };
    public static LocalFrame F(double x = 0, double y = 0, double z = 0) => new() { Origin=V(x,y,z), RotationDegrees=V() };
    public static List<Vec2> Rect(double width, double depth) => [new(){X=0,Y=0},new(){X=width,Y=0},new(){X=width,Y=depth},new(){X=0,Y=depth}];
    public static ShapeFeature Box(double width, double depth, double height, string id = "solid") => new()
    {
        Id=id, Kind=FeatureKind.Box, Operation=FeatureOperation.Add, Frame=F(),
        Parameters=new(){{"width",width},{"depth",depth},{"height",height}}, Profile=[], Path=[], Sections=[]
    };
    public static PartDefinition Part(string id, Vec3 size, params ShapeFeature[] features) => new()
    {
        Id=id, Name=id, Purpose="Independently authored mechanical fixture component", Detail="Approved smooth surfaces and individually addressable hardware; explicit idealized design, no manufacturing certification",
        MaterialId="steel", Kind=PartKind.Rigid, Size=size, Features=features.ToList(), Interfaces=[Port("mount",Vec3.Zero)], Terminals=[], Flexible=null
    };
    public static AttachmentPort Port(string id, Vec3 location) => new() { Id=id, Name=id, Frame=F() with { Origin=location } };
    public static PartInstance Instance(string id, string part, double x = 0, double y = 0, double z = 0, string parent="assembly") => new()
    {
        Id=id, PartId=part, ParentId=parent, Frame=F(x,y,z), ExplodeOffset=Vec3.Zero
    };
    public static JointSpec Joint(string id, JointKind kind, string parent, string child, string parentPort="mount", string childPort="mount") => new()
    {
        Id=id, Kind=kind, ParentInstanceId=parent, ChildInstanceId=child, ParentInterfaceId=parentPort, ChildInterfaceId=childPort,
        Axis=V(0,0,1), Minimum=kind==JointKind.Fixed?0:-90, Maximum=kind==JointKind.Fixed?0:90, Rest=0, Pitch=0, Clearance=.001
    };
    public static AssemblyDesign Base(string id, List<PartDefinition> parts, List<PartInstance> instances, Dictionary<string, Vec3>? exploded = null) => new()
    {
        SchemaVersion="bubo-assembly-design/v1", Id=id, Name=id, Purpose="Owned schematic design fixture for a mechanically coherent game asset",
        StyleSha256=new('a',64), CoordinateSystem="xy-metres-z-up",
        Numerics=new(){LinearTolerance=.000001,AngularToleranceDegrees=.01,MinimumFeatureSize=.0001,MaxCoordinateMagnitude=10000},
        Budgets=new(){MaxPartDefinitions=128,MaxInstances=1024,MaxFeatures=4096,MaxRoutePoints=4096,MaxDrawings=256,MaxValidationChecks=1_000_000,MaxPreviewBytes=16_777_216,MaxExportBytes=268_435_456,MaxGeneratedTriangles=500_000,MaxMotionSamples=10000},
        Observations=[new(){Id="design-assumption",Kind=ObservationKind.Assumed,Description="Dimensions and construction are authored fixture choices; references do not determine scale",Source="Owned engineering fixture"},
            new(){Id="design-simplification",Kind=ObservationKind.Simplified,Description="Bounded demonstration assembly; unlisted commercial systems are excluded from this explicit design, not silently omitted during construction",Source="Synthetic fixture scope"}],
        Materials=[new(){Id="steel",Name="Brushed steel",Color="#708090",Roughness=.45,Metallic=.8}],
        Parts=parts, Instances=instances.Select(instance => exploded is not null && exploded.TryGetValue(instance.Id, out var offset) ? instance with { ExplodeOffset=offset } : instance).ToList(), Subassemblies=[new(){Id="assembly",Name="Complete assembly",ParentId=null,Frame=F()}],
        Joints=[], Mechanisms=[], Routes=[], Connections=[], Sections=[new(){Id="centre-section",Axis=SectionAxis.Y,Offset=.5}]
    };

    public static AssemblyDesign Vehicle()
    {
        var chassis=Part("chassis-definition",V(4.2,1.8,.45),Box(4.2,1.8,.45)) with
        {
            Interfaces=[Port("front-left",V(.7,.1,.15)),Port("front-right",V(.7,1.7,.15)),Port("rear-left",V(3.5,.1,.15)),Port("rear-right",V(3.5,1.7,.15)),Port("door-hinge",V(1.5,0,.45))]
        };
        var wheel=Part("wheel-definition",V(.6,.6,.2),new ShapeFeature
        {
            Id="turned-wheel",Kind=FeatureKind.Revolve,Operation=FeatureOperation.Add,Frame=F(.3,.3,0),
            Parameters=new(){{"angleDegrees",360},{"segments",48}}, Profile=[new(){X=.08,Y=0},new(){X=.3,Y=0},new(){X=.3,Y=.2},new(){X=.08,Y=.2}],Path=[],Sections=[]
        }) with {Interfaces=[Port("axle",V(.3,.3,.1))]};
        var door=Part("door-definition",V(1.3,.05,.8),Box(1.3,.05,.8)) with { Interfaces=[Port("hinge",Vec3.Zero)] };
        var instances=new List<PartInstance>{Instance("chassis","chassis-definition",z:.4),Instance("wheel-front-left","wheel-definition",.4,-.2,.45),Instance("wheel-front-right","wheel-definition",.4,1.4,.45),Instance("wheel-rear-left","wheel-definition",3.2,-.2,.45),Instance("wheel-rear-right","wheel-definition",3.2,1.4,.45),Instance("driver-door","door-definition",1.5,0,.85)};
        return Base("vehicle-example",[chassis,wheel,door],instances,new(){["wheel-front-left"]=V(0,-.9,0),["wheel-front-right"]=V(0,.9,0),["wheel-rear-left"]=V(0,-.9,0),["wheel-rear-right"]=V(0,.9,0),["driver-door"]=V(0,-1.5,.7)}) with
        {
            Joints=[Joint("front-left-bearing",JointKind.Rotary,"chassis","wheel-front-left","front-left","axle"),Joint("front-right-bearing",JointKind.Rotary,"chassis","wheel-front-right","front-right","axle"),Joint("rear-left-bearing",JointKind.Rotary,"chassis","wheel-rear-left","rear-left","axle"),Joint("rear-right-bearing",JointKind.Rotary,"chassis","wheel-rear-right","rear-right","axle"),Joint("driver-door-joint",JointKind.Hinge,"chassis","driver-door","door-hinge","hinge") with {Minimum=0,Maximum=75}],
            Sections=[new(){Id="vehicle-long-section",Axis=SectionAxis.Y,Offset=.9},new(){Id="vehicle-axle-section",Axis=SectionAxis.X,Offset=.7}]
        };
    }

    public static AssemblyDesign Device()
    {
        var shell=Part("shell-definition",V(.8,.5,.2),Box(.8,.5,.2),Box(.74,.44,.2,"cavity") with {Operation=FeatureOperation.Subtract,Frame=F(.03,.03,.02)}) with {Interfaces=[Port("lid-hinge",V(0,0,.2))]};
        var lid=Part("lid-definition",V(.8,.5,.03),new ShapeFeature{Id="lid-extrusion",Kind=FeatureKind.Extrude,Operation=FeatureOperation.Add,Frame=F(),Parameters=new(){{"depth",.03}},Profile=Rect(.8,.5),Path=[],Sections=[]}) with {Interfaces=[Port("hinge",Vec3.Zero)]};
        var battery=Part("battery-definition",V(.1,.1,.05),Box(.1,.1,.05)) with {Terminals=[new(){Id="positive",Name="Positive power",Kind=TerminalKind.Power,Frame=F(.02,.02,.02)}]};
        var motor=Part("motor-definition",V(.1,.1,.08),new ShapeFeature{Id="motor-cylinder",Kind=FeatureKind.Cylinder,Operation=FeatureOperation.Add,Frame=F(.05,.05,0),Parameters=new(){{"radius",.05},{"length",.08},{"segments",32}},Profile=[],Path=[],Sections=[]}) with {Terminals=[new(){Id="power",Name="Motor power",Kind=TerminalKind.Power,Frame=F(.02,.02,.02)}]};
        var screw=Part("screw-definition",V(.012,.012,.03),new ShapeFeature{Id="screw-shaft",Kind=FeatureKind.Cylinder,Operation=FeatureOperation.Add,Frame=F(.006,.006,0),Parameters=new(){{"radius",.006},{"length",.03},{"segments",16}},Profile=[],Path=[],Sections=[]});
        var wire=Part("wire-definition",V(.23,.01,.01),new ShapeFeature{Id="wire-sweep",Kind=FeatureKind.Sweep,Operation=FeatureOperation.Add,Frame=F(),Parameters=new(){{"radius",.003},{"segments",8}},Profile=[],Path=[V(.005,.005,.005),V(.225,.005,.005)],Sections=[]}) with
        {
            Kind=PartKind.Wire,Interfaces=[],Flexible=new(){RestLength=.22,MinimumLength=.22,MaximumLength=.2222,MinimumBendRadius=.015,MaximumStretchRatio=1.01,SkinSegments=24}
        };
        var from=new Endpoint{InstanceId="battery",PortId="positive"}; var to=new Endpoint{InstanceId="motor",PortId="power"};
        return Base("device-example",[shell,lid,battery,motor,screw,wire],[Instance("shell","shell-definition"),Instance("lid","lid-definition",z:.2),Instance("battery","battery-definition",.1,.1,.05),Instance("motor","motor-definition",.32,.1,.05),Instance("screw-left","screw-definition",.015,.015,.18),Instance("screw-right","screw-definition",.77,.015,.18),Instance("power-wire","wire-definition",.115,.115,.065)],new(){["lid"]=V(0,0,.6),["battery"]=V(-.3,0,.25),["motor"]=V(.3,0,.25),["screw-left"]=V(-.15,-.2,.45),["screw-right"]=V(.15,-.2,.45),["power-wire"]=V(0,-.3,.25)}) with
        {
            Joints=[Joint("lid-joint",JointKind.Hinge,"shell","lid","lid-hinge","hinge") with {Minimum=0,Maximum=110,Axis=V(1,0,0)}],
            Routes=[new(){Id="power-route",InstanceId="power-wire",Points=[V(.12,.12,.07),V(.34,.12,.07)],Closed=false,From=from,To=to}],
            Connections=[new(){Id="motor-power-connection",Kind=ConnectionKind.Power,From=from,To=to,RouteId="power-route"}],
            Sections=[new(){Id="device-internal-section",Axis=SectionAxis.Z,Offset=.1}]
        };
    }

    public static AssemblyDesign Vessel()
    {
        List<Vec2> Ring(double width) => [new(){X=0,Y=1.5},new(){X=.5,Y=1.5-width/2},new(){X=11.5,Y=1.5-width/2},new(){X=12,Y=1.5},new(){X=11.5,Y=1.5+width/2},new(){X=.5,Y=1.5+width/2}];
        var hull=Part("hull-definition",V(12,3,2),new ShapeFeature{Id="hull-loft",Kind=FeatureKind.Loft,Operation=FeatureOperation.Add,Frame=F(),Parameters=[],Profile=[],Path=[],Sections=[new(){Frame=F(),Profile=Ring(1)},new(){Frame=F(z:1),Profile=Ring(2.5)},new(){Frame=F(z:2),Profile=Ring(3)}]}) with {Interfaces=[Port("shaft-bearing",V(10,1.5,.5)),Port("rudder-pivot",V(11.5,1.5,.5))]};
        var deck=Part("deck-definition",V(12,3,.15),new ShapeFeature{Id="deck-extrusion",Kind=FeatureKind.Extrude,Operation=FeatureOperation.Add,Frame=F(),Parameters=new(){{"depth",.15}},Profile=Ring(3),Path=[],Sections=[]});
        var shaft=Part("shaft-definition",V(.1,.1,1.5),new ShapeFeature{Id="shaft-cylinder",Kind=FeatureKind.Cylinder,Operation=FeatureOperation.Add,Frame=F(.05,.05,0),Parameters=new(){{"radius",.05},{"length",1.5},{"segments",24}},Profile=[],Path=[],Sections=[]}) with {Interfaces=[Port("bearing",Vec3.Zero)]};
        var rudder=Part("rudder-definition",V(.8,.08,.9),Box(.8,.08,.9)) with {Interfaces=[Port("pivot",Vec3.Zero)]};
        return Base("vessel-example",[hull,deck,shaft,rudder],[Instance("hull","hull-definition"),Instance("deck","deck-definition",z:2),Instance("propeller-shaft","shaft-definition",10,1.5,.5),Instance("rudder","rudder-definition",11.5,1.5,.5)],new(){["deck"]=V(0,0,2),["propeller-shaft"]=V(0,3,0),["rudder"]=V(2,0,0)}) with
        {
            Joints=[Joint("shaft-bearing-joint",JointKind.Rotary,"hull","propeller-shaft","shaft-bearing","bearing"),Joint("rudder-joint",JointKind.Hinge,"hull","rudder","rudder-pivot","pivot") with {Minimum=-35,Maximum=35}],
            Sections=[new(){Id="vessel-longitudinal-section",Axis=SectionAxis.Y,Offset=1.5},new(){Id="vessel-cross-section",Axis=SectionAxis.X,Offset=6}]
        };
    }

    public static AssemblyDesign FourBar()
    {
        var ground=Part("ground-definition",V(2,.5,.1),Box(2,.5,.1)) with {Interfaces=[Port("input-base",V(.2,.2,.1)),Port("output-base",V(1.8,.2,.1))]};
        var link=Part("link-definition",V(.1,1,.1),Box(.1,1,.1)) with {Interfaces=[Port("base",V(.05,0,.05)),Port("tip",V(.05,1,.05))]};
        var coupler=Part("coupler-definition",V(1.6,.1,.1),Box(1.6,.1,.1)) with {Interfaces=[Port("input",V(0,.05,.05)),Port("output",V(1.6,.05,.05))]};
        return Base("four-bar-design-fixture",[ground,link,coupler],[Instance("ground","ground-definition"),Instance("input","link-definition",.15,.2,.05),Instance("coupler","coupler-definition",.2,1.15,.05),Instance("output","link-definition",1.75,.2,.05)]) with
        {
            Joints=[Joint("input-joint",JointKind.Rotary,"ground","input","input-base","base"),Joint("coupler-joint",JointKind.Rotary,"input","coupler","tip","input"),Joint("output-joint",JointKind.Rotary,"coupler","output","output","tip"),Joint("closing-joint",JointKind.Rotary,"ground","output","output-base","base")],
            Mechanisms=[new(){Id="four-bar-linkage",Kind=MechanismKind.FourBar,JointIds=["input-joint","coupler-joint","output-joint","closing-joint"],Parameters=new(){{"groundLength",1.6},{"inputLength",1},{"couplerLength",1.6},{"outputLength",1}},Branch="open"}]
        };
    }

    public static AssemblyDesign SliderCrank()
    {
        var ground=Part("ground-definition",V(2,.5,.1),Box(2,.5,.1)) with {Interfaces=[Port("crank-base",V(.2,.2,.1)),Port("slider-axis",V(1.7,.2,.1))]};
        var crank=Part("crank-definition",V(.5,.1,.1),Box(.5,.1,.1)) with {Interfaces=[Port("base",V(0,.05,.05)),Port("tip",V(.5,.05,.05))]};
        var rod=Part("rod-definition",V(1,.1,.1),Box(1,.1,.1)) with {Interfaces=[Port("input",V(0,.05,.05)),Port("output",V(1,.05,.05))]};
        var slider=Part("slider-definition",V(.1,.1,.1),Box(.1,.1,.1)) with {Interfaces=[Port("pin",V(.05,.05,.05))]};
        return Base("slider-crank-design-fixture",[ground,crank,rod,slider],[Instance("ground","ground-definition"),Instance("crank","crank-definition",.2,.15,.05),Instance("rod","rod-definition",.7,.15,.05),Instance("slider","slider-definition",1.65,.15,.05)]) with
        {
            Joints=[Joint("crank-joint",JointKind.Rotary,"ground","crank","crank-base","base"),Joint("rod-joint",JointKind.Rotary,"crank","rod","tip","input"),Joint("pin-joint",JointKind.Rotary,"rod","slider","output","pin"),Joint("slide-joint",JointKind.Slider,"ground","slider","slider-axis","pin") with {Axis=V(1,0,0),Minimum=-.4,Maximum=.4}],
            Mechanisms=[new(){Id="slider-crank-linkage",Kind=MechanismKind.SliderCrank,JointIds=["crank-joint","rod-joint","pin-joint","slide-joint"],Parameters=new(){{"crankLength",.5},{"rodLength",1},{"offset",0}},Branch="open"}]
        };
    }
}
