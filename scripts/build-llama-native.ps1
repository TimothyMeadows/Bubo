param(
    [string]$Ref = "64b38b561b987679c4e1c6231f93860d3eec2638",
    [ValidateSet("", "win-x64", "linux-x64", "osx-arm64", "linux-arm64", "osx-x64", "win-arm64")]
    [string]$Rid = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$SourceRoot = "artifacts/src/llama.cpp",
    [string]$BuildRoot = "artifacts/native-build",
    [string]$OutputRoot = "artifacts/native",
    [switch]$StageToPackage,
    [switch]$Clean
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

function Get-NativeLibraryPattern {
    param([string]$RuntimeIdentifier)

    if ($RuntimeIdentifier.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "*.dll"
    }

    if ($RuntimeIdentifier.StartsWith("osx-", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "*.dylib"
    }

    return "*.so*"
}

function Resolve-RepoPath {
    param([string]$PathValue)

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $PathValue))
}

function Assert-UnderRepo {
    param(
        [string]$PathValue,
        [string]$Description
    )

    $fullPath = [System.IO.Path]::GetFullPath($PathValue)
    $rootPath = [System.IO.Path]::GetFullPath($RepoRoot)
    $rootWithSeparator = $rootPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if ($fullPath -ne $rootPath -and -not $fullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must stay under repository root '$rootPath'. Resolved path: '$fullPath'."
    }
}

function Assert-UnderDirectory {
    param(
        [string]$PathValue,
        [string]$ParentDirectory,
        [string]$Description
    )

    $fullPath = [System.IO.Path]::GetFullPath($PathValue)
    $parentPath = [System.IO.Path]::GetFullPath($ParentDirectory)
    $parentWithSeparator = $parentPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if ($fullPath -ne $parentPath -and -not $fullPath.StartsWith($parentWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must stay under '$parentPath'. Resolved path: '$fullPath'."
    }
}

function Remove-SafeDirectory {
    param(
        [string]$PathValue,
        [string]$Description
    )

    $fullPath = [System.IO.Path]::GetFullPath($PathValue)
    Assert-UnderRepo -PathValue $fullPath -Description $Description

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Invoke-NativeCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory = $RepoRoot
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

$SourceRootPath = Resolve-RepoPath $SourceRoot
$BuildRootPath = Resolve-RepoPath $BuildRoot
$BuildDir = Join-Path $BuildRootPath $Rid
$OutputRootPath = Resolve-RepoPath $OutputRoot
$OutputDir = Join-Path $OutputRootPath $Rid
$NativeLibraryName = Get-NativeLibraryName -RuntimeIdentifier $Rid
$NativeLibraryPattern = Get-NativeLibraryPattern -RuntimeIdentifier $Rid
$StagedPackagePath = Join-Path $RepoRoot (Join-Path "src/LlamaCppSharp.Native/runtimes/$Rid/native" $NativeLibraryName)

Assert-UnderRepo -PathValue $SourceRootPath -Description "llama.cpp source root"
Assert-UnderRepo -PathValue $BuildDir -Description "native build directory"
Assert-UnderRepo -PathValue $OutputDir -Description "native output directory"
Assert-UnderRepo -PathValue $StagedPackagePath -Description "native package staging path"

if ($Clean) {
    Assert-UnderDirectory -PathValue $BuildDir -ParentDirectory (Join-Path $RepoRoot "artifacts/native-build") -Description "clean build directory"
    Assert-UnderDirectory -PathValue $OutputDir -ParentDirectory (Join-Path $RepoRoot "artifacts/native") -Description "clean output directory"
    Remove-SafeDirectory -PathValue $BuildDir -Description "native build directory"
    Remove-SafeDirectory -PathValue $OutputDir -Description "native output directory"
}

if (Test-Path -LiteralPath (Join-Path $SourceRootPath ".git")) {
    Invoke-NativeCommand -FilePath "git" -Arguments @("-C", $SourceRootPath, "fetch", "--tags", "--force", "origin")
}
else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $SourceRootPath) | Out-Null
    Invoke-NativeCommand -FilePath "git" -Arguments @("clone", "https://github.com/ggml-org/llama.cpp", $SourceRootPath)
}

Invoke-NativeCommand -FilePath "git" -Arguments @("-C", $SourceRootPath, "checkout", "--force", $Ref)
$ResolvedCommit = (& git -C $SourceRootPath rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ResolvedCommit)) {
    throw "Unable to resolve llama.cpp commit for '$Ref'."
}

New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$ConfigureArgs = @(
    "-S", $SourceRootPath,
    "-B", $BuildDir,
    "-DBUILD_SHARED_LIBS=ON",
    "-DGGML_NATIVE=OFF",
    "-DGGML_CUDA=OFF",
    "-DGGML_METAL=OFF",
    "-DGGML_VULKAN=OFF",
    "-DLLAMA_BUILD_TESTS=OFF",
    "-DLLAMA_BUILD_EXAMPLES=OFF",
    "-DLLAMA_BUILD_TOOLS=OFF",
    "-DLLAMA_BUILD_COMMON=OFF",
    "-DLLAMA_BUILD_SERVER=OFF"
)

Invoke-NativeCommand -FilePath "cmake" -Arguments $ConfigureArgs
Invoke-NativeCommand -FilePath "cmake" -Arguments @("--build", $BuildDir, "--config", $Configuration, "--parallel")

$BuiltAssets = Get-ChildItem -LiteralPath $BuildDir -Recurse -File -Filter $NativeLibraryPattern |
    Where-Object { $_.FullName -notmatch [regex]::Escape((Join-Path $BuildDir "CMakeFiles")) } |
    Sort-Object FullName

if (-not ($BuiltAssets | Where-Object { $_.Name -eq $NativeLibraryName } | Select-Object -First 1)) {
    throw "Could not find '$NativeLibraryName' under '$BuildDir'."
}

$CopiedAssets = @()
foreach ($AssetGroup in ($BuiltAssets | Group-Object Name)) {
    $Asset = $AssetGroup.Group | Select-Object -First 1
    $OutputAssetPath = Join-Path $OutputDir $Asset.Name
    Copy-Item -LiteralPath $Asset.FullName -Destination $OutputAssetPath -Force
    $CopiedAssets += $OutputAssetPath
}

$PrimaryOutputAssetPath = Join-Path $OutputDir $NativeLibraryName

$Manifest = [ordered]@{
    upstreamRepository = "https://github.com/ggml-org/llama.cpp"
    requestedRef = $Ref
    resolvedCommit = $ResolvedCommit
    rid = $Rid
    configuration = $Configuration
    nativeLibrary = $NativeLibraryName
    nativeAssets = @($CopiedAssets | ForEach-Object { Split-Path -Leaf $_ })
    sourceRoot = $SourceRootPath
    outputPath = $PrimaryOutputAssetPath
    builtAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
    cmake = $ConfigureArgs
}

$ManifestPath = Join-Path $OutputDir "llama-native-build.json"
$Manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8

if ($StageToPackage) {
    $PackageNativeDir = Split-Path -Parent $StagedPackagePath
    New-Item -ItemType Directory -Force -Path $PackageNativeDir | Out-Null
    Get-ChildItem -LiteralPath $PackageNativeDir -File |
        Where-Object { $_.Extension -in @(".dll", ".dylib", ".so") -or $_.Name -match "\.so\." } |
        Remove-Item -Force

    foreach ($CopiedAsset in $CopiedAssets) {
        Copy-Item -LiteralPath $CopiedAsset -Destination (Join-Path $PackageNativeDir (Split-Path -Leaf $CopiedAsset)) -Force
    }

    Write-Host "Staged native assets: $PackageNativeDir"
}

Write-Host "Built native asset: $PrimaryOutputAssetPath"
Write-Host "Copied native asset count: $($CopiedAssets.Count)"
Write-Host "Resolved llama.cpp commit: $ResolvedCommit"
