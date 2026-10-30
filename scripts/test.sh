#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
configuration="${1:-Release}"
mkdir -p .bubo/test-temp
export TMPDIR="$root/.bubo/test-temp"
for project in src/Bubo.Host.Tests/Bubo.Host.Tests.csproj src/runtime/proof-to-model/Tests/RuntimeTests.csproj src/runtime/model-to-animation/Tests/RuntimeTests.csproj src/runtime/model-to-action/Tests/RuntimeTests.csproj src/media/Tests/Media.Tests.csproj; do
  dotnet run --project "$project" -c "$configuration" --no-build
done
python3 scripts/audit-resources.py
python3 tests/test-cli-contract.py --cli "src/Bubo.Cli/bin/$configuration/net10.0/bubo.dll"
python3 tests/test-media-provision.py
python3 tests/test-music-worker.py
python3 tests/test-voice-worker.py
