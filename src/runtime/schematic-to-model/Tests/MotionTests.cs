using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using static Bubo.Schematic.Tests.DesignExamples;

namespace Bubo.Schematic.Tests;

public static class MotionTests
{
    public static void Run()
    {
        var compiler = new AssemblyMotionCompiler();
        void Near(double actual, double expected, double tolerance = 1e-8) => Check.True(Math.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}");
        void Same(RigidPose actual, RigidPose expected) { Near(RigidTransforms.Length(RigidTransforms.Subtract(actual.Origin, expected.Origin)), 0); Near(RigidTransforms.AngularErrorDegrees(actual, expected), 0); }
        Check.Case("rigid double basis composition preserves frame order and tiny offsets", () =>
        {
            var frame = F(1000, 2, 3) with { RotationDegrees = V(90, 90, 90) };
            var pose = RigidTransforms.FromFrame(frame); var point = V(.001, .002, .003);
            Near(RigidTransforms.Length(RigidTransforms.Subtract(RigidTransforms.Point(pose, point), DesignGeometry.TransformPoint(frame, point))), 0, 1e-12);
            Same(RigidTransforms.Compose(pose, RigidTransforms.Inverse(pose)), RigidPose.Identity);
        });
        foreach (var kind in new[] { JointKind.Fixed, JointKind.Hinge, JointKind.Rotary, JointKind.Slider, JointKind.Screw })
            Check.Case("native " + kind + " motion respects declared rest, interface axes and instance isolation", () =>
            {
                var d = MechanismExamples.Pair(first: kind);
                var j = d.Joints[0] with { Rest = kind == JointKind.Fixed ? 0 : 10, Minimum = kind == JointKind.Fixed ? 0 : -100, Maximum = kind == JointKind.Fixed ? 0 : 400, Pitch = kind == JointKind.Screw ? .02 : 0 };
                d = d with { Joints = [j, d.Joints[1]] };
                var rest = compiler.Evaluate(d, new Dictionary<string, double>());
                var amount = kind == JointKind.Fixed ? 0 : kind == JointKind.Slider ? .25 : 90;
                var moved = compiler.Evaluate(d, new Dictionary<string, double> { [j.Id] = j.Rest + amount });
                Same(moved.Instances["second"], rest.Instances["second"]); Same(moved.Instances["unrelated-copy"], rest.Instances["unrelated-copy"]);
                if (kind is JointKind.Hinge or JointKind.Rotary or JointKind.Screw) Near(moved.Instances["first"].X.Y, 1);
                Near(moved.Instances["first"].Origin.Z, kind == JointKind.Slider ? .25 : kind == JointKind.Screw ? .005 : 0);
            });
        Check.Case("parent-interface-local axis and nested scene parent move a single rigid descendant", () =>
        {
            var d = MechanismExamples.Pair(first: JointKind.Slider);
            var ground = d.Parts[0] with { Interfaces = [d.Parts[0].Interfaces[0] with { Frame = F() with { RotationDegrees = V(0, 90, 0) } }, d.Parts[0].Interfaces[1]] };
            d = d with { Parts = [ground, d.Parts[1]], Instances = d.Instances.Select(i => i.Id == "unrelated-copy" ? i with { ParentId = "first", Frame = F(.001) } : i).ToList() };
            var pose = compiler.Evaluate(d, new Dictionary<string, double> { ["first-joint"] = .2 });
            Near(pose.Instances["first"].Origin.X, .2); Near(pose.Instances["unrelated-copy"].Origin.X, .201); Near(pose.Instances["second"].Origin.X, 1);
        });
        Check.Case("hinge rotates around its actual off-origin mating port", () =>
        {
            var d = MechanismExamples.Pair();
            d = d with
            {
                Parts = [d.Parts[0] with { Interfaces = [d.Parts[0].Interfaces[0] with { Frame = F(1) }, d.Parts[0].Interfaces[1]] }, d.Parts[1] with { Interfaces = [Port("mount", V(.1, .1, .1))] }],
                Instances = d.Instances.Select(i => i.Id == "first" ? i with { Frame = F(.9, -.1, -.1) } : i.Id == "second" ? i with { Frame = F(.9, -.1, -.1) } : i).ToList()
            };
            var pose = compiler.Evaluate(d, new Dictionary<string, double> { ["first-joint"] = 90 });
            Near(pose.Instances["first"].Origin.X, 1.1); Near(pose.Instances["first"].Origin.Y, -.1);
            Near(RigidTransforms.Length(RigidTransforms.Subtract(RigidTransforms.Point(pose.Instances["first"], V(.1, .1, .1)), V(1))), 0);
        });
        Check.Case("accepted near-unit joint axes remain rigid and preserve requested slider distance", () =>
        {
            foreach (var kind in new[] { JointKind.Rotary, JointKind.Slider })
            {
                var d = MechanismExamples.Pair(first: kind); d = d with { Joints = [d.Joints[0] with { Axis = V(0, 0, 1 - 5e-9) }, d.Joints[1]] };
                var pose = compiler.Evaluate(d, new Dictionary<string, double> { ["first-joint"] = kind == JointKind.Rotary ? 90 : .25 }).Instances["first"];
                Near(RigidTransforms.Length(pose.X), 1, 1e-12); Near(RigidTransforms.Length(pose.Y), 1, 1e-12); Near(RigidTransforms.Length(pose.Z), 1, 1e-12);
                Near(pose.Origin.Z, kind == JointKind.Slider ? .25 : 0, 1e-12);
            }
        });
        foreach (var kind in new[] { MechanismKind.SpurGears, MechanismKind.RackAndPinion, MechanismKind.PulleyBelt })
            Check.Case("native " + kind + " uses authored coupling and rejects conflicting followers", () =>
            {
                var d = MechanismExamples.Coupled(kind, kind == MechanismKind.PulleyBelt ? "crossed" : "open", 2);
                var pose = compiler.Evaluate(d, new Dictionary<string, double> { ["first-joint"] = 30 });
                Near(pose.JointValues["second-joint"], kind == MechanismKind.RackAndPinion ? Math.PI / 60 : kind == MechanismKind.PulleyBelt ? -60 : 60);
                Check.Throws<AssemblyMotionException>(() => compiler.Evaluate(d, new Dictionary<string, double> { ["first-joint"] = 30, ["second-joint"] = 1 }));
                var narrow = d with { Joints = [d.Joints[0], d.Joints[1] with { Minimum = -.001, Maximum = .001 }] };
                Check.Throws<AssemblyMotionException>(() => compiler.Evaluate(narrow, new Dictionary<string, double> { ["first-joint"] = 30 }));
                var ranged = d with { Joints = [d.Joints[0] with { Minimum = -360, Maximum = 360 }, d.Joints[1]] };
                Check.True(compiler.Compile(ranged).Clips.Any(c => !c.CompleteCycle));
                var badAxis = d with { Joints = [d.Joints[0], d.Joints[1] with { Axis = kind == MechanismKind.RackAndPinion ? V(0, 0, 1) : V(1) }] };
                Check.Throws<AssemblyMotionException>(() => compiler.Compile(badAxis));
                if (kind == MechanismKind.PulleyBelt)
                    Near(compiler.Evaluate(d with { Mechanisms = [d.Mechanisms[0] with { Branch = "open" }] }, new Dictionary<string, double> { ["first-joint"] = 30 }).JointValues["second-joint"], 60);
            });
        foreach (var kind in new[] { MechanismKind.SliderCrank, MechanismKind.FourBar })
            foreach (var branch in new[] { "open", "crossed" })
                Check.Case("native " + kind + " " + branch + " branch closes complete cycle using actual interfaces", () =>
                {
                    var d = kind == MechanismKind.SliderCrank ? MechanismExamples.SliderCrank(branch, .1) : MechanismExamples.FourBar(branch);
                    var plan = compiler.Compile(d);
                    Check.True(plan.Clips.Count == 2 && plan.Clips.Single(c => c.CompleteCycle).Samples.Count == 33);
                    Check.True(plan.Clips.SelectMany(c => c.Samples).SelectMany(s => s.Pose.Closures).All(c => c.LinearError < d.Numerics.LinearTolerance && c.AngularErrorDegrees < d.Numerics.AngularToleranceDegrees));
                    var clip = plan.Clips.Single(c => c.CompleteCycle);
                    foreach (var instance in clip.Samples[0].Pose.Instances) Same(clip.Samples[^1].Pose.Instances[instance.Key], instance.Value);
                    var bad = d with { Mechanisms = [d.Mechanisms[0] with { Branch = branch == "open" ? "crossed" : "open" }] };
                    Check.Throws<AssemblyMotionException>(() => compiler.Compile(bad));
                });
        Check.Case("mechanism input distances and axes cannot contradict authored geometry", () =>
        {
            var d = MechanismExamples.FourBar(); var parameters = new Dictionary<string, double>(d.Mechanisms[0].Parameters) { ["couplerLength"] = 2.9 };
            Check.Throws<AssemblyMotionException>(() => compiler.Compile(d with { Mechanisms = [d.Mechanisms[0] with { Parameters = parameters }] }));
            var sc = MechanismExamples.SliderCrank(); Check.Throws<AssemblyMotionException>(() => compiler.Compile(sc with { Joints = sc.Joints.Select((j, i) => i == 1 ? j with { Axis = V(1) } : j).ToList() }));
            Check.Throws<AssemblyMotionException>(() => compiler.Compile(DesignExamples.FourBar()));
            Check.Throws<AssemblyMotionException>(() => compiler.Compile(sc with { Joints = sc.Joints.Select((j, i) => i == 3 ? j with { Minimum = -.1, Maximum = .1 } : j).ToList() }));
        });
        Check.Case("linkages retain exact poses under a noncanonical rotated and translated assembly frame", () =>
        {
            foreach (var d in new[] { MechanismExamples.FourBar(), MechanismExamples.SliderCrank("crossed", .1) })
            {
                var frame = F(1000, -2000, 500) with { RotationDegrees = V(37, -22, 71) }; var transform = RigidTransforms.FromFrame(frame);
                var relocated = d with { Subassemblies = [d.Subassemblies[0] with { Frame = frame }] };
                var values = new Dictionary<string, double> { ["input-joint"] = 137 };
                var original = compiler.Evaluate(d, values); var moved = compiler.Evaluate(relocated, values);
                foreach (var instance in original.Instances) Same(moved.Instances[instance.Key], RigidTransforms.Compose(transform, instance.Value));
            }
        });
        Check.Case("fixed-only assembly produces explicit rest evidence with no invented moving clips", () =>
        {
            var plan = compiler.Compile(MechanismExamples.Pair(JointKind.Fixed, JointKind.Fixed));
            Check.True(plan.Clips.Count == 0 && plan.Sampling.TotalSamples == 1 && plan.Sampling.Drivers.Count == 0);
        });
        Check.Case("coupling cascades compile in dependency order and preserve unrelated copies", () =>
        {
            var d = MechanismExamples.Coupled(MechanismKind.SpurGears);
            var third = Joint("third-joint", JointKind.Rotary, "ground", "unrelated-copy", "second") with { Minimum = -1440, Maximum = 1440 };
            d = d with { Instances = d.Instances.Select(i => i.Id == "unrelated-copy" ? i with { Frame = F(1) } : i).ToList(), Joints = [d.Joints[0] with { Minimum = -120, Maximum = 120 }, d.Joints[1], third], Mechanisms = [d.Mechanisms[0] with { Id = "late", JointIds = ["second-joint", "third-joint"], Parameters = new() { ["ratio"] = -3 } }, d.Mechanisms[0]] };
            var pose = compiler.Evaluate(d, new Dictionary<string, double> { ["first-joint"] = 20 }); Near(pose.JointValues["third-joint"], 120);
            Check.True(compiler.Compile(d).Clips.All(c => c.DriverJointId == "first-joint"));
        });
        Check.Case("independent drivers receive separate endpoint/rest/intermediate ranges", () =>
        {
            var d = MechanismExamples.Pair(); d = d with { Joints = [d.Joints[0] with { Rest = 7 }, d.Joints[1]] };
            var plan = compiler.Compile(d);
            Check.True(plan.Clips.Count == 2 && plan.Clips.All(c => !c.CompleteCycle));
            var first = plan.Clips.Single(c => c.DriverJointId == "first-joint");
            Check.True(first.Samples.Any(s => s.Pose.JointValues["first-joint"] == 7));
            Check.True(first.Samples.All(s => s.Pose.JointValues["second-joint"] == d.Joints[1].Rest));
            Check.True(plan.Sampling.TotalSamples == 1 + plan.Clips.Sum(c => c.Samples.Count));
        });
        Check.Case("fractional gear ratio cycle waits for all rigid poses to return", () =>
        {
            var d = MechanismExamples.Coupled(MechanismKind.SpurGears, ratio: .5);
            var plan = compiler.Compile(d); var cycle = plan.Clips.Single(c => c.CompleteCycle);
            Near(cycle.Samples[^1].Pose.JointValues["first-joint"] - cycle.Samples[0].Pose.JointValues["first-joint"], 720);
        });
        Check.Case("long rational mechanism period is complete and cannot alias whole rotations", () =>
        {
            var d = MechanismExamples.Coupled(MechanismKind.SpurGears, ratio: 1d / 37);
            d = d with { Joints = [d.Joints[0] with { Minimum = 0, Maximum = 360 * 37 }, d.Joints[1]] };
            var plan = compiler.Compile(d); var cycle = plan.Clips.Single(c => c.CompleteCycle);
            Near(cycle.Samples[^1].Pose.JointValues["first-joint"] - cycle.Samples[0].Pose.JointValues["first-joint"], 360 * 37);
            Check.True(cycle.Samples.Count >= 297);
            foreach (var clip in plan.Clips)
                for (var i = 1; i < clip.Samples.Count; i++)
                    foreach (var joint in d.Joints) Check.True(Math.Abs(clip.Samples[i].Pose.JointValues[joint.Id] - clip.Samples[i - 1].Pose.JointValues[joint.Id]) <= 45 + 1e-8);
        });
        Check.Case("high coupling ratios refine angular samples or reject the explicit sample limit", () =>
        {
            var d = MechanismExamples.Coupled(MechanismKind.SpurGears, ratio: 10);
            d = d with { Joints = [d.Joints[0] with { Minimum = -90, Maximum = 90 }, d.Joints[1]] };
            var plan = compiler.Compile(d); Check.True(plan.Clips[0].Samples.Count > 9);
            Check.Throws<AssemblyMotionException>(() => compiler.Compile(d with { Budgets = d.Budgets with { MaxMotionSamples = 20 } }));
        });
        Check.Case("coaxial owning-joint chains cannot alias accumulated whole world rotations", () =>
        {
            const int count = 16;
            var ground = Part("ground-definition", V(.1, .1, .1), Box(.1, .1, .1)); var moving = Part("moving-definition", V(.1, .1, .1), Box(.1, .1, .1));
            var instances = new List<PartInstance> { Instance("ground", ground.Id) }; var joints = new List<JointSpec>(); var mechanisms = new List<MechanismSpec>();
            for (var i = 0; i < count; i++)
            {
                instances.Add(Instance("link-" + i, moving.Id));
                joints.Add(Joint("joint-" + i, JointKind.Rotary, i == 0 ? "ground" : "link-" + (i - 1), "link-" + i) with { Minimum = 0, Maximum = 180 });
                if (i > 0) mechanisms.Add(new() { Id = "coupling-" + i, Kind = MechanismKind.SpurGears, JointIds = ["joint-" + (i - 1), "joint-" + i], Parameters = new() { ["ratio"] = 1 }, Branch = "open" });
            }
            var d = Base("coaxial-chain", [ground, moving], instances) with { Joints = joints, Mechanisms = mechanisms.AsEnumerable().Reverse().ToList() };
            var clip = compiler.Compile(d).Clips.Single(); Check.True(clip.Samples.Count >= 65);
            for (var i = 1; i < clip.Samples.Count; i++)
                Check.True((clip.Samples[i].Pose.JointValues["joint-0"] - clip.Samples[i - 1].Pose.JointValues["joint-0"]) * count <= 45 + 1e-8);
        });
        Check.Case("coupling axes are rechecked when an independent carrier hinge tilts a shaft", () =>
        {
            var d = MechanismExamples.Coupled(MechanismKind.SpurGears);
            var carrier = Instance("carrier", "moving-definition", 1);
            d = d with { Instances = [.. d.Instances, carrier], Joints = [d.Joints[0], d.Joints[1] with { ParentInstanceId = "carrier", ParentInterfaceId = "mount" }, Joint("carrier-joint", JointKind.Hinge, "ground", "carrier", "second") with { Axis = V(1) }] };
            Check.Throws<AssemblyMotionException>(() => compiler.Evaluate(d, new Dictionary<string, double> { ["carrier-joint"] = 30 }));
        });
        Check.Case("linkage continuation rejects a travel excursion between delivered sample keys", () =>
        {
            var d = MechanismExamples.SliderCrank(offset: .1);
            d = d with { Joints = d.Joints.Select((j, i) => i == 3 ? j with { Maximum = .001 } : j).ToList() };
            Check.Throws<AssemblyMotionException>(() => compiler.Compile(d));
        });
        Check.Case("four-bar analytic path bounds reject a narrow impossible interval between sample keys", () =>
        {
            var d = MechanismExamples.FourBar(inputAngleDegrees: 37.123, couplerLength: 2, outputLength: 2 - 1e-9);
            compiler.Evaluate(d, new Dictionary<string, double>());
            Check.Throws<AssemblyMotionException>(() => compiler.Evaluate(d, new Dictionary<string, double> { ["input-joint"] = 150 }));
            Check.Throws<AssemblyMotionException>(() => compiler.Compile(d));
        });
        Check.Case("slider-crank exact extrema reject travel beyond a limit between continuation steps", () =>
        {
            var d = MechanismExamples.SliderCrank(offset: .1);
            d = d with { Joints = d.Joints.Select((j, i) => i == 3 ? j with { Maximum = .00167 } : j).ToList() };
            Check.Throws<AssemblyMotionException>(() => compiler.Evaluate(d, new Dictionary<string, double> { ["input-joint"] = 13 }));
        });
        Check.Case("unsupported travel, nonfinite values, owning cycles, work and sample excess fail", () =>
        {
            var d = MechanismExamples.Pair();
            Check.Throws<AssemblyMotionException>(() => compiler.Evaluate(d, new Dictionary<string, double> { ["first-joint"] = 91 }));
            Check.Throws<AssemblyMotionException>(() => compiler.Evaluate(d, new Dictionary<string, double> { ["first-joint"] = double.NaN }));
            Check.Throws<AssemblyMotionException>(() => compiler.Evaluate(d, new Dictionary<string, double> { ["missing"] = 0 }));
            Check.Throws<AssemblyMotionException>(() => compiler.Compile(d with { Budgets = d.Budgets with { MaxMotionSamples = 3 } }));
            var report = new AssemblyDesignValidator().Validate(d);
            Check.Throws<AssemblyMotionException>(() => compiler.Compile(d with { Budgets = d.Budgets with { MaxValidationChecks = report.Checks + 5 } }));
            var cyclic = d with { Joints = [d.Joints[0] with { ParentInstanceId = "second", ParentInterfaceId = "mount" }, d.Joints[1] with { ParentInstanceId = "first", ParentInterfaceId = "mount" }], Instances = d.Instances.Select(i => i.Id == "second" ? i with { Frame = F() } : i).ToList() };
            Check.Throws<AssemblyMotionException>(() => compiler.Compile(cyclic));
        });
        Check.Case("motion cancellation propagates before samples are emitted", () =>
        {
            using var source = new CancellationTokenSource(); source.Cancel();
            Check.Throws<OperationCanceledException>(() => compiler.Compile(MechanismExamples.Pair(), ct: source.Token));
            Check.Throws<OperationCanceledException>(() => compiler.Evaluate(MechanismExamples.Pair(), new Dictionary<string, double>(), source.Token));
        });
        Check.Case("diagnostic pose-record host-memory cap rejects large inventories before sample allocation", () =>
        {
            var d = MechanismExamples.Pair();
            d = d with
            {
                Instances = [.. d.Instances, .. Enumerable.Range(0, 2044).Select(i => Instance("memory-copy-" + i, "moving-definition"))],
                Budgets = d.Budgets with { MaxInstances = 4096 }
            };
            var error = Check.Throws<AssemblyMotionException>(() => compiler.Compile(d));
            Check.True(error.Message.Contains("MaxPoseRecords (32768)") && error.Message.Contains("before pose allocation"));
        });
        Check.Case("angular refinement reserves pose records before materializing additional frames", () =>
        {
            var d = MechanismExamples.Coupled(MechanismKind.SpurGears, ratio: 10);
            d = d with
            {
                Joints = [d.Joints[0] with { Minimum = -90, Maximum = 90 }, d.Joints[1]],
                Instances = [.. d.Instances, .. Enumerable.Range(0, 508).Select(i => Instance("refinement-copy-" + i, "moving-definition"))]
            };
            var error = Check.Throws<AssemblyMotionException>(() => compiler.Compile(d));
            Check.True(error.Message.Contains("MaxPoseRecords (32768)"));
        });
    }
}
