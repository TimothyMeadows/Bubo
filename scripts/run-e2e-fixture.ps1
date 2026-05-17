param(
    [string] $Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ("bubo-e2e-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $workspace | Out-Null

$openCaw = Join-Path $workspace ".opencaw"
New-Item -ItemType Directory -Force -Path $openCaw | Out-Null
Set-Content -Path (Join-Path $openCaw "AGENTS.md") -Value "OpenCaw baseline fixture for scripted E2E." -Encoding UTF8
git -C $openCaw init | Out-Null
git -C $openCaw config user.email "tests@example.invalid" | Out-Null
git -C $openCaw config user.name "Bubo Tests" | Out-Null
git -C $openCaw remote add origin "https://github.com/TimothyMeadows/OpenCaw" | Out-Null
git -C $openCaw add . | Out-Null
git -C $openCaw commit -m "fixture" | Out-Null

New-Item -ItemType Directory -Force -Path (Join-Path $workspace ".ai/tasks") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $workspace ".ai/FRAGMENTS") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $workspace ".ai/LEARNINGS") | Out-Null
Set-Content -Path (Join-Path $workspace "AGENTS.md") -Value "# Host agents fixture." -Encoding UTF8
Set-Content -Path (Join-Path $workspace ".ai/MEMORY.md") -Value "Host memory fixture." -Encoding UTF8
Set-Content -Path (Join-Path $workspace ".ai/RULES.md") -Value "Host rules fixture." -Encoding UTF8
Set-Content -Path (Join-Path $workspace ".ai/DEBUG.md") -Value "Host debug fixture." -Encoding UTF8
Set-Content -Path (Join-Path $workspace ".ai/tasks/TODO.md") -Value "# TODO" -Encoding UTF8
Set-Content -Path (Join-Path $workspace ".ai/tasks/OPEN_ISSUES.md") -Value "" -Encoding UTF8

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
    "tool": "patch_file",
    "arguments": {
      "path": "generated/script-result.txt",
      "old": "Scripted fixture completed.",
      "new": "Scripted fixture patched."
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

dotnet run --project (Join-Path $root "src/LocalAgent.Cli/LocalAgent.Cli.csproj") --configuration $Configuration -- run --workspace $workspace --mode local --opencaw-update false
if ($LASTEXITCODE -ne 0) {
    throw "Bubo CLI exited with code $LASTEXITCODE"
}

$artifactPath = Join-Path $workspace ".ai/artifacts"
$outputPath = Join-Path $artifactPath "OUTPUT.md"
$generatedPath = Join-Path $workspace "generated/script-result.txt"
$debugPath = Join-Path $artifactPath "agent-debug.jsonl"
$transcriptPath = Join-Path $artifactPath "agent-transcript.md"

foreach ($path in @($outputPath, $generatedPath, $debugPath, $transcriptPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Expected fixture artifact was not created: $path"
    }
}

$generatedContent = Get-Content -LiteralPath $generatedPath -Raw
if ($generatedContent -notmatch "Scripted fixture patched\.") {
    throw "Expected patch_file to update generated fixture content."
}

Write-Host "Bubo E2E fixture passed in $workspace"
