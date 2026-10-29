#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
configuration="${1:-Release}"
[[ "$configuration" == Release || "$configuration" == Debug ]] || { echo 'Use Release or Debug.' >&2; exit 1; }
python3 scripts/audit-resources.py
dotnet restore Bubo.slnx --nologo
dotnet build Bubo.slnx -c "$configuration" --no-restore --nologo
dotnet publish src/Bubo.Cli/Bubo.Cli.csproj -c "$configuration" --no-build --no-restore -o artifacts/bubo --nologo
binary='./artifacts/bubo/bubo'
[[ -f "$binary" ]] || binary='./artifacts/bubo/bubo.exe'
"$binary" capabilities --api-version 1 > artifacts/bubo/capabilities.json
python3 - <<'PY'
from pathlib import Path
import json, subprocess
root=Path.cwd()
receipt={'schemaVersion':'bubo-build/v1','sourceRevision':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),
         'sourceDirty':bool(subprocess.check_output(['git','status','--porcelain'],text=True).strip()),
         'capabilities':json.loads((root/'artifacts/bubo/capabilities.json').read_text())}
(root/'artifacts/bubo/build-info.json').write_text(json.dumps(receipt,indent=2)+'\n')
PY
echo 'Built and verified artifacts/bubo; no model/tool provisioning performed.'
