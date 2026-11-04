using System.Text.Json;
using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using Bubo.Schematic.Tests;
using Bubo.Schematic.Application;

if (args is ["--write-readiness-fixtures", var readinessDirectory])
{
    Directory.CreateDirectory(readinessDirectory);
    foreach (var (name, design) in ReadinessExamples.All())
    {
        new AssemblyDesignValidator().RequireValid(design);
        var profile = design.AssemblyProfile!;
        var plan = new AssemblyMotionCompiler().Compile(design, profile.SamplesPerRange, profile.SamplesPerCycle);
        _ = new FlexibleMotionCompiler().Compile(design, plan);
        File.WriteAllBytes(Path.Combine(readinessDirectory, name + ".json"), JsonSerializer.SerializeToUtf8Bytes(design, StrictDesignJson.Options));
    }
    return 0;
}

if (args is ["--write-assembly-examples", var assemblyDirectory])
{
    Directory.CreateDirectory(assemblyDirectory);
    foreach (var (name, design) in new Dictionary<string, AssemblyDesign> { ["vehicle"] = AssemblyExamples.Vehicle(), ["device"] = AssemblyExamples.Device(), ["vessel"] = AssemblyExamples.Vessel() })
    {
        new AssemblyDesignValidator().RequireValid(design);
        var profile = design.AssemblyProfile!;
        var plan = new AssemblyMotionCompiler().Compile(design, profile.SamplesPerRange, profile.SamplesPerCycle);
        _ = new FlexibleMotionCompiler().Compile(design, plan);
        File.WriteAllBytes(Path.Combine(assemblyDirectory, name + ".json"), JsonSerializer.SerializeToUtf8Bytes(design, StrictDesignJson.Options));
    }
    return 0;
}

if (args is ["--write-examples", var directory])
{
    Directory.CreateDirectory(directory);
    foreach (var (name, design) in new Dictionary<string, AssemblyDesign> { ["vehicle"] = DesignExamples.Vehicle(), ["device"] = DesignExamples.Device(), ["vessel"] = DesignExamples.Vessel() })
    {
        new AssemblyDesignValidator().RequireValid(design);
        File.WriteAllBytes(Path.Combine(directory, name + ".json"), JsonSerializer.SerializeToUtf8Bytes(design, StrictDesignJson.Options));
    }
    return 0;
}
DomainTests.Run();
PreviewTests.Run();
SessionTests.Run();
PartTests.Run();
MotionTests.Run();
FlexibleTests.Run();
ClearanceTests.Run();
AssemblyTests.Run();
ReadinessExamples.Run();
AssemblyOutputTests.Run();
AssemblySessionTests.Run();
Console.WriteLine($"Schematic design checks: {Check.Passed} passed, {Check.Failed} failed. Synthetic review fixtures grant no human asset acceptance.");
return Check.Failed == 0 ? 0 : 1;
