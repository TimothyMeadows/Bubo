using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using System.Security.Cryptography;

namespace Bubo.Schematic.Tests;

public static class ExportContractTests
{
    static readonly string Sha = new('a', 64);
    public static AssemblyExportIdentity Identity() => new(new(Sha,Sha,Sha,Sha,Sha,Sha,Sha),Sha,Sha,Sha,Sha,Sha,Sha,Sha,Sha,Sha);
    public static AssemblyExportPackage Package(AssemblyDesign design)
    {
        var names = new[] { "assembly.blend", "assembly.glb", "assembly-design.json", "assembly-metadata.json", "bom.json", "drawings/front.svg", "provenance/construction.json",
            "diagnostic-motion/diagnostic.blend", "diagnostic-motion/diagnostic.glb", "diagnostic-motion/diagnostic-motion.json" };
        return new()
        {
            SchemaVersion = "bubo-schematic-export-package/v1", Task = "synthetic-test", Asset = "test", Version = "v1", ModelAuthor = "engineering-fixture",
            Ancestry = Identity(), Numerics = design.Numerics,
            Coordinates = new("right-handed-metres-z-up", "gltf-right-handed-metres-y-up", [1,0,0,0,0,0,-1,0,0,1,0,0,0,0,0,1]),
            Components = design.Instances.Select((instance,index) => new AssemblyExportComponent(instance.Id,instance.PartId,"bubo.instance." + index.ToString("D6"),
                design.Parts.Single(p => p.Id == instance.PartId).Kind == PartKind.Rigid ? "rigid" : "flexible",
                design.Parts.Single(p => p.Id == instance.PartId).Kind == PartKind.Rigid ? null : "bubo.rig." + index.ToString("D6"))).ToArray(),
            Files = names.Select(n => new AssemblyExportFile(n,Sha,100)).ToArray(), Bundle = new() { ["export_author.py"] = Sha },
            Toolchain = new(Sha,"4.5.10 LTS",[new("__init__.py",Sha,100)])
        };
    }
    public static void Run()
    {
        Check.Case("export package preserves every repeated physical occurrence", () =>
        { var design = AssemblyExamples.Vehicle(); ExportPackageValidator.RequireValid(Package(design),design); });
        Check.Case("export package rejects a fused repeated occurrence", () =>
        {
            var design = AssemblyExamples.Vehicle(); var package = Package(design); var components = package.Components.ToArray(); components[1] = components[1] with { Node = components[0].Node };
            Check.Throws<InvalidOperationException>(() => ExportPackageValidator.RequireValid(package with { Components = components },design));
        });
        Check.Case("export package rejects missing physical hardware", () =>
        {
            var design = AssemblyExamples.Device(); var package = Package(design);
            Check.Throws<InvalidOperationException>(() => ExportPackageValidator.RequireValid(package with { Components = package.Components.Skip(1).ToArray() },design));
        });
        Check.Case("export package requires an independently owned flexible rig", () =>
        {
            var design = AssemblyExamples.Device(); var package = Package(design); var components = package.Components.Select(c => c.Kind == "flexible" ? c with { RigNode = null } : c).ToArray();
            Check.Throws<InvalidOperationException>(() => ExportPackageValidator.RequireValid(package with { Components = components },design));
        });
        Check.Case("export package rejects path traversal and case aliases", () =>
        {
            var design = AssemblyExamples.Vehicle(); var package = Package(design);
            Check.Throws<InvalidOperationException>(() => ExportPackageValidator.RequireValid(package with { Files = [..package.Files,new("../escape",Sha,1)] },design));
            Check.Throws<InvalidOperationException>(() => ExportPackageValidator.RequireValid(package with { Files = [..package.Files,new("ASSEMBLY.glb",Sha,1)] },design));
        });
        Check.Case("export package cannot inventory its own hash or inherit decisions", () =>
        {
            var design = AssemblyExamples.Vehicle(); var package = Package(design);
            Check.Throws<InvalidOperationException>(() => ExportPackageValidator.RequireValid(package with { Files = [..package.Files,new("package.json",Sha,1)] },design));
        });
        Check.Case("export package rejects lost diagnostic package and ancestry", () =>
        {
            var design = AssemblyExamples.Vehicle(); var package = Package(design);
            Check.Throws<InvalidOperationException>(() => ExportPackageValidator.RequireValid(package with { Files = package.Files.Where(f => !f.Path.EndsWith("diagnostic.glb",StringComparison.Ordinal)).ToArray() },design));
            Check.Throws<InvalidOperationException>(() => ExportPackageValidator.RequireValid(package with { Ancestry = package.Ancestry with { MotionVerificationSha256 = "" } },design));
        });
        Check.Case("export package rejects modified approved numerics", () =>
        {
            var design = AssemblyExamples.Vessel(); var package = Package(design);
            Check.Throws<InvalidOperationException>(() => ExportPackageValidator.RequireValid(package with { Numerics = design.Numerics with { LinearTolerance = .01 } },design));
        });
        Check.Case("export native job rejects unbounded limits before geometry", () =>
        {
            var limits = new AssemblyExportLimits(1_000_000,StrictDesignJson.MaximumInputBytes,1_000_000,1000,1000,1000,1000,1000,100,100000,100,128,1);
            AssemblyExportOutputVerifier.RequireLimits(limits);
            Check.Throws<InvalidOperationException>(() => AssemblyExportOutputVerifier.RequireLimits(limits with { MaxGeometryChecks = GeometryWork.HardMaximum + 1 }));
            Check.Throws<InvalidOperationException>(() => AssemblyExportOutputVerifier.RequireLimits(limits with { MaxVertices = int.MaxValue }));
            Check.Throws<InvalidOperationException>(() => AssemblyExportOutputVerifier.RequireLimits(limits with { MaxChannels = int.MaxValue }));
        });
        Check.Case("export toolchain revalidation rejects refreshed recorded inventory with an unchanged fingerprint", () =>
        {
            var directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,".bubo/export-contract-tests",Guid.NewGuid().ToString("N")));
            var module = Path.Combine(directory,"4.5/scripts/addons_core/io_scene_gltf2"); Directory.CreateDirectory(module);
            var executable = Path.Combine(directory,OperatingSystem.IsWindows() ? "blender.exe" : "blender");
            File.WriteAllText(executable,"SIMULATED TOOL IDENTITY FIXTURE; NEVER EXECUTED");
            File.WriteAllText(Path.Combine(module,"__init__.py"),"# SIMULATED GLTF INVENTORY; NEVER IMPORTED\n");
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)));
            var actual = AssemblyGltfToolchain.Capture(executable,hash,CancellationToken.None); AssemblyGltfToolchain.RequireCurrent(actual,CancellationToken.None);
            Check.Throws<InvalidOperationException>(() => AssemblyGltfToolchain.RequireCurrent(actual with { ModuleRoot = directory },CancellationToken.None));
            Check.Throws<InvalidOperationException>(() => AssemblyGltfToolchain.RequireCurrent(actual with { Files = [actual.Files[0] with { Sha256 = new string('b',64) }] },CancellationToken.None));
            File.AppendAllText(Path.Combine(module,"__init__.py"),"# mutation\n");
            Check.Throws<InvalidOperationException>(() => AssemblyGltfToolchain.RequireCurrent(actual,CancellationToken.None));
        });
    }
}
