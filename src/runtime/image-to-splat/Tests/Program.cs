using Bubo.Splats.Tests;

if (args.Length != 0) throw new InvalidOperationException("Splat CPU contract tests accept no arguments");
DomainTests.Run();
ArtifactTests.Run();
DescriptorIdentityTests.Run();
InspectionTests.Run();
Console.WriteLine($"Splat CPU contract and Gaussian inspection checks passed: {Check.Passed}");
Console.WriteLine("Synthetic in-memory fixtures establish no model, GPU, renderer, physical scale, licence acceptance, human review or runtime asset acceptance.");
