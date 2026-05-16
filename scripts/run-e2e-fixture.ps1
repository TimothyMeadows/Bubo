param(
    [string] $Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ("bubo-e2e-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $workspace | Out-Null

$input = @'
# Scripted E2E Fixture

```bubo-actions
[
  {
    "tool": "write_file",
    "arguments": {
      "path": "generated/script-result.txt",
      "content": "Scripted fixture completed.\n"
    }
  },
  {
    "tool": "run_command",
    "arguments": {
      "executable": "dotnet",
      "arguments": ["--version"]
    }
  }
]
```
'@

Set-Content -Path (Join-Path $workspace "INPUT.md") -Value $input -Encoding UTF8

dotnet run --project (Join-Path $root "src/LocalAgent.Cli/LocalAgent.Cli.csproj") --configuration $Configuration -- run --workspace $workspace --mode local
if ($LASTEXITCODE -ne 0) {
    throw "Bubo CLI exited with code $LASTEXITCODE"
}

$outputPath = Join-Path $workspace "OUTPUT.md"
$generatedPath = Join-Path $workspace "generated/script-result.txt"
$debugPath = Join-Path $workspace "agent-debug.jsonl"
$transcriptPath = Join-Path $workspace "agent-transcript.md"

foreach ($path in @($outputPath, $generatedPath, $debugPath, $transcriptPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Expected fixture artifact was not created: $path"
    }
}

Write-Host "Bubo E2E fixture passed in $workspace"
