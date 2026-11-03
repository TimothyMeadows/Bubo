using System.Text.Json;
using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using Bubo.Schematic.Tests;

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
Console.WriteLine($"Schematic design checks: {Check.Passed} passed, {Check.Failed} failed. Synthetic review fixtures grant no human asset acceptance.");
return Check.Failed == 0 ? 0 : 1;
