# Floor-plan design runtime

Domain owns typed metre-based architecture and pure bounded geometry validation. Application compiles validated plans through an architectural-renderer port. Infrastructure owns strict JSON, SVG output and immutable guarded workspace/review state. CLI composes those services through API2; it reads no host instructions.

This delivery produces complete design previews only. Model construction, Blender source/export and actual engine traversal remain separate stages. Tests use synthetic caller/reviewer assertions explicitly; their success does not supply human acceptance.

The authoritative versioned contract, geometry conventions and native examples are in resources/floor-plan-to-model/README.md at the repository root. Run the new executable fixtures through the normal scripts/test entrypoint or dotnet run --project src/runtime/floor-plan-to-model/Tests/FloorPlan.Tests.csproj --configuration Release after building. Public CLI tests are tests/test-floor-plan-cli.py.
