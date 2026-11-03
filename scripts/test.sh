#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
configuration="${1:-Release}"
mkdir -p .bubo/test-temp
export TMPDIR="$root/.bubo/test-temp"
for project in src/Bubo.Host.Tests/Bubo.Host.Tests.csproj src/runtime/proof-to-model/Tests/RuntimeTests.csproj src/runtime/model-to-animation/Tests/RuntimeTests.csproj src/runtime/model-to-action/Tests/RuntimeTests.csproj src/media/Tests/Media.Tests.csproj src/runtime/floor-plan-to-model/Tests/FloorPlan.Tests.csproj src/runtime/schematic-to-model/Tests/Schematic.Tests.csproj; do
  dotnet run --project "$project" -c "$configuration" --no-build
done
python3 scripts/audit-resources.py
python3 tests/test-cli-contract.py --cli "src/Bubo.Cli/bin/$configuration/net10.0/bubo.dll"
python3 tests/test-floor-plan-authoring.py
python3 tests/test-floor-plan-export-geometry.py
python3 tests/test-floor-plan-export-blender.py
python3 tests/test-floor-plan-cli.py --cli "src/Bubo.Cli/bin/$configuration/net10.0/bubo.dll"
python3 tests/test-schematic-cli.py --cli "src/Bubo.Cli/bin/$configuration/net10.0/bubo.dll"
python3 -B tests/test-schematic-parts-admission.py
python3 tests/test-media-provision.py
python3 tests/test-music-worker.py
python3 tests/test-voice-worker.py
