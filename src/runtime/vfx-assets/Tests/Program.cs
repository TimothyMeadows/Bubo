using Bubo.Vfx.Tests;

var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
string? cli = null;
if (args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--cli") throw new InvalidOperationException("Use [--cli EXISTING_BUBO_DLL_OR_EXE]");
    cli = Path.GetFullPath(args[1]);
    if (!File.Exists(cli)) throw new InvalidOperationException("The explicitly selected built CLI does not exist");
}
var root = Path.Combine(project, "bin/test-fixtures/run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
DomainTests.Run();
InspectionTests.Run(root);
if (cli is null) Check.Limits.Add("Actual public CLI rejection checks not run; select --cli EXISTING_BUBO_DLL_OR_EXE.");
else CliTests.Run(cli, root);
foreach (var limitation in Check.Limits) Console.WriteLine("NOT RUN: " + limitation);
Console.WriteLine($"VFX contract/inspection tests passed: {Check.Passed}. Fixtures retained at {root}");
Console.WriteLine("Byte/hash/path tests do not certify image or GLB authenticity, rendering, visual quality, human review, gameplay, target-device performance, or runtime acceptance.");
