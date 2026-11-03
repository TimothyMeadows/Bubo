using Bubo.Schematic.Domain;

namespace Bubo.Schematic.Tests;

public static class DomainTests
{
    public static void Run()
    {
        Check.Case("documented frame composition applies fixed X then Y then Z axes", () =>
        {
            var frame = DesignExamples.F() with { RotationDegrees = DesignExamples.V(90,90,90) };
            var transformed = DesignGeometry.TransformPoint(frame, DesignExamples.V(1,2,3));
            Check.True(Math.Abs(transformed.X-3)<1e-12 && Math.Abs(transformed.Y-2)<1e-12 && Math.Abs(transformed.Z+1)<1e-12);
        });
        var validator=new AssemblyDesignValidator();
        void Invalid(AssemblyDesign d,string code) { var report=validator.Validate(d); Check.True(!report.IsValid && report.Issues.Any(i=>i.Code==code),"Expected "+code+": "+string.Join("; ",report.Issues)); }
        Check.Case("three owned assembly designs pass declaration validation",()=>
        {
            foreach(var design in new[]{DesignExamples.Vehicle(),DesignExamples.Device(),DesignExamples.Vessel()})
            {
                var result=validator.Validate(design); Check.True(result.IsValid,string.Join("; ",result.Issues));
            }
        });
        Check.Case("shared definition retains independent physical occurrences",()=>
        {
            var d=DesignExamples.Vehicle(); Check.True(d.Instances.Count(i=>i.PartId=="wheel-definition")==4,"Four separate wheels");
            var first=DesignGeometry.Bounds(d,"wheel-front-left"); var second=DesignGeometry.Bounds(d,"wheel-front-right"); Check.True(first.Min.Y!=second.Min.Y,"Independent world transforms");
        });
        Check.Case("double precision rotation preserves local hardware offset",()=>
        {
            var point=DesignGeometry.TransformPoint(new(){Origin=DesignExamples.V(1000,0,0),RotationDegrees=DesignExamples.V(0,0,90)},DesignExamples.V(.001,0,0));
            Check.True(Math.Abs(point.X-1000)<1e-10 && Math.Abs(point.Y-.001)<1e-10,"Rotation and tiny offset remain precise");
        });
        Check.Case("unknown schema and nonfinite dimensions fail",()=>
        {
            var d=DesignExamples.Vehicle(); Invalid(d with {SchemaVersion="other"},"schema");
            Invalid(d with {Parts=[d.Parts[0] with {Size=DesignExamples.V(double.NaN,1,1)},..d.Parts.Skip(1)]},"coordinate");
            Invalid(d with {Numerics=d.Numerics with {LinearTolerance=.01}},"numerics");
        });
        Check.Case("missing collections and null members report validation failures",()=>
        {
            var d=DesignExamples.Vehicle(); Invalid(d with {Parts=null!},"collection"); Invalid(d with {Instances=[null!,..d.Instances]},"null");
            Invalid(d with {Parts=[d.Parts[0] with {Interfaces=null!},..d.Parts.Skip(1)]},"collection");
            var device=DesignExamples.Device(); Invalid(device with {Parts=[..device.Parts.Take(2),device.Parts[2] with {Terminals=null!},..device.Parts.Skip(3)]},"collection");
        });
        Check.Case("case collisions, missing definitions and hierarchy cycles fail",()=>
        {
            var d=DesignExamples.Vehicle(); Invalid(d with {Instances=[..d.Instances, d.Instances[0] with {Id="CHASSIS"}]},"duplicate");
            Invalid(d with {Instances=[d.Instances[0] with {PartId="absent"},..d.Instances.Skip(1)]},"reference");
            Invalid(d with {Instances=[d.Instances[0] with {ParentId=d.Instances[0].Id},..d.Instances.Skip(1)]},"hierarchy");
            Check.Throws<ArgumentException>(()=>DesignGeometry.WorldPoint(d with {Subassemblies=[d.Subassemblies[0] with {ParentId="assembly"}]},"chassis",Vec3.Zero));
        });
        Check.Case("closed feature parameter sets and local envelopes enforced",()=>
        {
            var d=DesignExamples.Vehicle(); var p=d.Parts[0]; var f=p.Features[0];
            Invalid(d with {Parts=[p with {Features=[f with {Parameters=new(f.Parameters){{"script",1}}}]},..d.Parts.Skip(1)]},"parameters");
            Invalid(d with {Parts=[p with {Size=DesignExamples.V(.1,.1,.1)},..d.Parts.Skip(1)]},"envelope");
            Invalid(d with {Parts=[p with {Features=[f with {Operation=(FeatureOperation)99}]},..d.Parts.Skip(1)]},"enum");
        });
        Check.Case("self-intersecting and reversed profiles fail",()=>
        {
            var d=DesignExamples.Device(); var p=d.Parts[1]; var f=p.Features[0];
            Invalid(d with {Parts=[d.Parts[0],p with {Features=[f with {Profile=[new(){X=0,Y=0},new(){X=.8,Y=.5},new(){X=0,Y=.5},new(){X=.8,Y=0}]}]},..d.Parts.Skip(2)]},"profile");
            Invalid(d with {Parts=[d.Parts[0],p with {Features=[f with {Profile=f.Profile.AsEnumerable().Reverse().ToList()}]},..d.Parts.Skip(2)]},"profile");
        });
        Check.Case("joint axis, rest travel and port ownership enforced",()=>
        {
            var d=DesignExamples.Vehicle(); Invalid(d with {Joints=[d.Joints[0] with {Axis=DesignExamples.V(.5,0,0)},..d.Joints.Skip(1)]},"joint");
            Invalid(d with {Joints=[d.Joints[0] with {Rest=999},..d.Joints.Skip(1)]},"joint");
            Invalid(d with {Joints=[d.Joints[0] with {ParentInterfaceId="missing"},..d.Joints.Skip(1)]},"endpoint");
            Invalid(d with {Instances=d.Instances.Select(i=>i.Id=="wheel-front-left"?i with {Frame=DesignExamples.F(9,0,0)}:i).ToList()},"joint");
        });
        Check.Case("typed mechanisms reject wrong joint types and parameters",()=>
        {
            var d=DesignExamples.Vehicle(); var m=new MechanismSpec{Id="wheel-gear-declaration",Kind=MechanismKind.SpurGears,JointIds=[d.Joints[0].Id,d.Joints[1].Id],Parameters=new(){{"ratio",-1}},Branch="open"};
            validator.RequireValid(d with {Mechanisms=[m]});
            Invalid(d with {Mechanisms=[m with {Parameters=new(){{"ratio",0}}}]},"mechanism");
            Invalid(d with {Mechanisms=[m with {Kind=MechanismKind.RackAndPinion,Parameters=new(){{"pitchRadius",.1}}}]},"mechanism");
        });
        Check.Case("explicit four-joint linkage closures remain separate from scene hierarchy",()=>
        {
            validator.RequireValid(DesignExamples.FourBar()); validator.RequireValid(DesignExamples.SliderCrank());
            var d=DesignExamples.FourBar();
            Invalid(d with {Mechanisms=[]},"joint");
            Invalid(d with {Mechanisms=[d.Mechanisms[0] with {JointIds=["input-joint","coupler-joint","closing-joint","output-joint"]}]},"mechanism");
            Invalid(d with {Mechanisms=[..d.Mechanisms,d.Mechanisms[0] with {Id="second-owner"}]},"mechanism");
            var slider=DesignExamples.SliderCrank(); Invalid(slider with {Mechanisms=[slider.Mechanisms[0] with {JointIds=slider.Mechanisms[0].JointIds.Take(3).ToList()}]},"mechanism");
        });
        Check.Case("coupling dependency cycles fail while cascaded drivers remain valid",()=>
        {
            var d=DesignExamples.Vehicle();
            MechanismSpec Gear(string id,int driver,int follower) => new(){Id=id,Kind=MechanismKind.SpurGears,JointIds=[d.Joints[driver].Id,d.Joints[follower].Id],Parameters=new(){{"ratio",-1}},Branch="open"};
            Invalid(d with {Mechanisms=[Gear("forward-coupling",0,1),Gear("reciprocal-coupling",1,0)]},"mechanism");
            Invalid(d with {Mechanisms=[Gear("cycle-first",0,1),Gear("cycle-second",1,2),Gear("cycle-third",2,0)]},"mechanism");
            validator.RequireValid(d with {Mechanisms=[Gear("chain-first",0,1),Gear("chain-second",1,2),Gear("chain-third",2,3)]});
        });
        Check.Case("routes retain physical owners, lengths and exact terminal bindings",()=>
        {
            var d=DesignExamples.Device(); Invalid(d with {Routes=[d.Routes[0] with {Points=[DesignExamples.V(),DesignExamples.V(2,0,0)]}]},"route");
            Invalid(d with {Routes=[]},"route");
            Invalid(d with {Routes=[d.Routes[0] with {Points=[DesignExamples.V(.13,.12,.07),DesignExamples.V(.35,.12,.07)]}]},"route");
            Invalid(d with {Connections=[]},"connection");
            Invalid(d with {Connections=[d.Connections[0] with {Kind=ConnectionKind.Signal}]},"connection");
            Invalid(d with {Connections=[d.Connections[0] with {From=new(){InstanceId="battery",PortId="unknown"}}]},"endpoint");
        });
        Check.Case("declared work and drawing budgets stop validation",()=>
        {
            var d=DesignExamples.Vehicle(); Invalid(d with {Budgets=d.Budgets with {MaxValidationChecks=1}},"budget");
            Invalid(d with {Budgets=d.Budgets with {MaxDrawings=1}},"budget");
            Invalid(d with {Budgets=d.Budgets with {MaxInstances=1}},"budget");
            Invalid(d with {Budgets=d.Budgets with {MaxPreviewBytes=long.MaxValue}},"budget");
        });
        Check.Case("validation cancellation propagates without approval",()=>
        {
            using var source=new CancellationTokenSource(); source.Cancel(); Check.Throws<OperationCanceledException>(()=>validator.Validate(DesignExamples.Device(),source.Token));
        });
    }
}
