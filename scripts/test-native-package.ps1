param(
    [ValidateSet("", "win-x64", "linux-x64", "osx-arm64", "linux-arm64", "osx-x64", "win-arm64")]
    [string]$Rid = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$PackageOutput = "artifacts/packages",
    [string]$Ref = "64b38b561b987679c4e1c6231f93860d3eec2638",
    [switch]$BuildNative
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Get-CurrentRid {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
            return "win-arm64"
        }

        return "win-x64"
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
            return "osx-arm64"
        }

        return "osx-x64"
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
        return "linux-arm64"
    }

    return "linux-x64"
}

function Get-NativeLibraryName {
    param([string]$RuntimeIdentifier)

    if ($RuntimeIdentifier.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "llama.dll"
    }

    if ($RuntimeIdentifier.StartsWith("osx-", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "libllama.dylib"
    }

    return "libllama.so"
}

function Resolve-RepoPath {
    param([string]$PathValue)

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $PathValue))
}

function Invoke-NativeCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

if ([string]::IsNullOrWhiteSpace($Rid)) {
    $Rid = Get-CurrentRid
}

$NativeLibraryName = Get-NativeLibraryName -RuntimeIdentifier $Rid
$PackageOutputPath = Resolve-RepoPath $PackageOutput
$ExpectedStagedAsset = Join-Path $RepoRoot (Join-Path "src/LlamaCppSharp.Native/runtimes/$Rid/native" $NativeLibraryName)
$StagedNativeDirectory = Split-Path -Parent $ExpectedStagedAsset

if ($BuildNative) {
    & (Join-Path $PSScriptRoot "build-llama-native.ps1") -Ref $Ref -Rid $Rid -Configuration $Configuration -StageToPackage
    if ($LASTEXITCODE -ne 0) {
        throw "Native build failed."
    }
}

if (-not (Test-Path -LiteralPath $ExpectedStagedAsset)) {
    throw "Expected staged native asset is missing: $ExpectedStagedAsset. Run scripts/build-llama-native.ps1 -Rid $Rid -StageToPackage first, or pass -BuildNative."
}

New-Item -ItemType Directory -Force -Path $PackageOutputPath | Out-Null

Invoke-NativeCommand -FilePath "dotnet" -Arguments @("build", (Join-Path $RepoRoot "Bubo.sln"), "--configuration", $Configuration)

Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
    "run",
    "--no-build",
    "--configuration", $Configuration,
    "--project", (Join-Path $RepoRoot "src/LocalAgent.Cli/LocalAgent.Cli.csproj"),
    "--",
    "native",
    "test",
    "--base-directory", (Join-Path $RepoRoot "src/LlamaCppSharp.Native"),
    "--strict"
)

Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
    "pack", (Join-Path $RepoRoot "src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj"),
    "--configuration", $Configuration,
    "--no-build",
    "--output", $PackageOutputPath,
    "-p:RequireNativeAssetsForPack=true",
    "-p:RequiredNativeRid=$Rid"
)

$Package = Get-ChildItem -LiteralPath $PackageOutputPath -Filter "Bubo.LlamaCppSharp.Native.*.nupkg" |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if (-not $Package) {
    throw "Could not find packed Bubo.LlamaCppSharp.Native package under '$PackageOutputPath'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$Archive = [System.IO.Compression.ZipFile]::OpenRead($Package.FullName)
try {
    $ExpectedEntry = "runtimes/$Rid/native/$NativeLibraryName"
    $Entry = $Archive.Entries | Where-Object { $_.FullName -eq $ExpectedEntry } | Select-Object -First 1
    if (-not $Entry) {
        throw "Package '$($Package.FullName)' does not contain expected native asset '$ExpectedEntry'."
    }

    $StagedAssets = Get-ChildItem -LiteralPath $StagedNativeDirectory -File |
        Where-Object { $_.Extension -in @(".dll", ".dylib", ".so") -or $_.Name -match "\.so\." }

    foreach ($StagedAsset in $StagedAssets) {
        $ExpectedStagedEntry = "runtimes/$Rid/native/$($StagedAsset.Name)"
        $StagedEntry = $Archive.Entries | Where-Object { $_.FullName -eq $ExpectedStagedEntry } | Select-Object -First 1
        if (-not $StagedEntry) {
            throw "Package '$($Package.FullName)' does not contain staged native asset '$ExpectedStagedEntry'."
        }
    }
}
finally {
    $Archive.Dispose()
}

Write-Host "Verified native package: $($Package.FullName)"
Write-Host "Verified native asset entry: runtimes/$Rid/native/$NativeLibraryName"
