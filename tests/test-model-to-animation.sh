#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
project='src/runtime/model-to-animation/Tests/RuntimeTests.csproj'
dotnet restore "$project" --nologo
dotnet build "$project" --no-restore --nologo
dotnet run --project "$project" --no-build
python3 scripts/audit-resources.py
echo 'Passed standalone model-to-animation executable fixtures. Native Blender/browser acceptance is separate; see README.'
