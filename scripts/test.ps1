param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$buboRepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buboFixtureRoot=Join-Path $buboRepositoryRoot '.bubo/test-temp'
New-Item -ItemType Directory -Force -Path $buboFixtureRoot | Out-Null
$buboPreviousTemp=$env:TEMP; $buboPreviousTmp=$env:TMP
$env:TEMP=$buboFixtureRoot; $env:TMP=$buboFixtureRoot
Push-Location $buboRepositoryRoot
try {
    foreach ($buboTestProject in @('src/Bubo.Host.Tests/Bubo.Host.Tests.csproj','src/runtime/proof-to-model/Tests/RuntimeTests.csproj','src/runtime/model-to-animation/Tests/RuntimeTests.csproj','src/runtime/model-to-action/Tests/RuntimeTests.csproj','src/media/Tests/Media.Tests.csproj')) {
        dotnet run --project $buboTestProject --configuration $Configuration --no-build
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $buboTestProject" }
    }
    python -X utf8 scripts/audit-resources.py
    if ($LASTEXITCODE -ne 0) { throw 'Resource audit failed.' }
    python -X utf8 tests/test-cli-contract.py --cli "src/Bubo.Cli/bin/$Configuration/net10.0/bubo.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Public CLI contract checks failed.' }
    python -X utf8 tests/test-media-provision.py
    if ($LASTEXITCODE -ne 0) { throw 'Fixed media provisioning contract checks failed.' }
    python -X utf8 tests/test-music-worker.py
    if ($LASTEXITCODE -ne 0) { throw 'Fixed music worker contract checks failed.' }
    python -X utf8 tests/test-voice-worker.py
    if ($LASTEXITCODE -ne 0) { throw 'Fixed voice worker contract checks failed.' }
} finally { Pop-Location; $env:TEMP=$buboPreviousTemp; $env:TMP=$buboPreviousTmp }
