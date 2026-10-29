param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$buboRepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $buboRepositoryRoot
try {
    python -X utf8 scripts/audit-resources.py
    if ($LASTEXITCODE -ne 0) { throw 'Audited resources failed validation.' }
    dotnet restore Bubo.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build Bubo.slnx --configuration $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet publish src/Bubo.Cli/Bubo.Cli.csproj --configuration $Configuration --no-build --no-restore --output artifacts/bubo --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $buboExecutable=Join-Path $buboRepositoryRoot 'artifacts/bubo/bubo.exe'
    if (-not (Test-Path -LiteralPath $buboExecutable)) { $buboExecutable=Join-Path $buboRepositoryRoot 'artifacts/bubo/bubo' }
    $buboCapabilities=& $buboExecutable capabilities --api-version 1
    if ($LASTEXITCODE -ne 0) { throw 'Published runtime verification failed.' }
    $buboReceipt=[ordered]@{schemaVersion='bubo-build/v1'; sourceRevision=(git rev-parse HEAD); sourceDirty=[bool](git status --porcelain); capabilities=($buboCapabilities -join "`n" | ConvertFrom-Json)}
    [IO.File]::WriteAllText((Join-Path $buboRepositoryRoot 'artifacts/bubo/build-info.json'), ($buboReceipt | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    Write-Output 'Built and verified artifacts/bubo. No model/tool provisioning or production execution performed.'
} finally { Pop-Location }
