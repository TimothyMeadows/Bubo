param(
    [string]$Ref = "64b38b561b987679c4e1c6231f93860d3eec2638",
    [ValidateSet("", "win-x64", "linux-x64", "osx-arm64", "linux-arm64", "osx-x64", "win-arm64")]
    [string]$Rid = "",
    [ValidateSet("cpu", "cuda", "metal", "vulkan")]
    [string]$Backend = "cpu",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$CudaArchitectures = "86;89;120",
    [string]$CudaCompiler = "",
    [string]$CudaToolkitRoot = "",
    [string]$Generator = "",
    [string]$Platform = "",
    [string]$Toolset = "",
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

function Get-BackendArtifactDirectory {
    param(
        [string]$Root,
        [string]$RuntimeIdentifier,
        [string]$NativeBackend
    )

    if ($NativeBackend -eq "cpu") {
        return Join-Path $Root $RuntimeIdentifier
    }

    return Join-Path (Join-Path $Root $NativeBackend) $RuntimeIdentifier
}

function Get-PackageNativeDirectory {
    param(
        [string]$RuntimeIdentifier,
        [string]$NativeBackend
    )

    $nativeDirectory = Join-Path $RepoRoot "src/LlamaCppSharp.Native/runtimes/$RuntimeIdentifier/native"
    if ($NativeBackend -eq "cpu") {
        return $nativeDirectory
    }

    return Join-Path $nativeDirectory $NativeBackend
}

function Assert-BackendRidSupported {
    param(
        [string]$RuntimeIdentifier,
        [string]$NativeBackend
    )

    $supported = switch ($NativeBackend) {
        "cpu" { $true }
        "cuda" { $RuntimeIdentifier -in @("linux-x64", "win-x64") }
        "metal" { $RuntimeIdentifier -in @("osx-arm64", "osx-x64") }
        "vulkan" { $RuntimeIdentifier -in @("linux-x64", "win-x64") }
        default { $false }
    }

    if (-not $supported) {
        throw "Backend '$NativeBackend' is not supported for RID '$RuntimeIdentifier' by this build script."
    }
}

function Get-BackendCMakeArgs {
    param(
        [string]$NativeBackend,
        [string]$CudaArchList,
        [string]$CudaCompilerPath,
        [string]$CudaToolkitRootPath
    )

    $args = @(
        "-DGGML_NATIVE=OFF",
        "-DGGML_CUDA=OFF",
        "-DGGML_METAL=OFF",
        "-DGGML_VULKAN=OFF"
    )

    switch ($NativeBackend) {
        "cuda" {
            $args[1] = "-DGGML_CUDA=ON"
            if (-not [string]::IsNullOrWhiteSpace($CudaArchList)) {
                $args += "-DCMAKE_CUDA_ARCHITECTURES=$CudaArchList"
            }

            if (-not [string]::IsNullOrWhiteSpace($CudaCompilerPath)) {
                $args += "-DCMAKE_CUDA_COMPILER=$CudaCompilerPath"
            }

            if (-not [string]::IsNullOrWhiteSpace($CudaToolkitRootPath)) {
                $args += "-DCUDAToolkit_ROOT=$CudaToolkitRootPath"
                $args += "-DCUDA_TOOLKIT_ROOT_DIR=$CudaToolkitRootPath"

                if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
                    $cudaToolkitDir = $CudaToolkitRootPath.TrimEnd(
                        [System.IO.Path]::DirectorySeparatorChar,
                        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
                    $args += "-DCMAKE_VS_GLOBALS=CudaToolkitDir=$cudaToolkitDir"
                }
            }
        }
        "metal" {
            $args[2] = "-DGGML_METAL=ON"
        }
        "vulkan" {
            $args[3] = "-DGGML_VULKAN=ON"
        }
    }

    return $args
}

function Assert-CudaToolchain {
    param(
        [string]$CudaArchList,
        [string]$CudaCompilerPath
    )

    $nvcc = if ([string]::IsNullOrWhiteSpace($CudaCompilerPath)) { "nvcc" } else { $CudaCompilerPath }
    $versionOutput = & $nvcc --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "CUDA backend requires nvcc. Install CUDA Toolkit 12.8+ for RTX 50xx / Blackwell support, or pass -CudaCompiler."
    }

    $versionText = $versionOutput -join "`n"
    $match = [regex]::Match($versionText, "release\s+(?<major>\d+)\.(?<minor>\d+)")
    if (-not $match.Success) {
        Write-Warning "Could not parse nvcc version. Output: $versionText"
        return
    }

    $major = [int]$match.Groups["major"].Value
    $minor = [int]$match.Groups["minor"].Value
    $requiresBlackwellCompiler = $CudaArchList -match "(^|[;,\s])1(00|01|20)($|[;,\s])"
    if ($requiresBlackwellCompiler -and ($major -lt 12 -or ($major -eq 12 -and $minor -lt 8))) {
        throw "CUDA architectures '$CudaArchList' require CUDA Toolkit 12.8+ for Blackwell / RTX 50xx support. Detected nvcc release $major.$minor."
    }
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

Assert-BackendRidSupported -RuntimeIdentifier $Rid -NativeBackend $Backend

$CudaToolkitRootPath = ""
if (-not [string]::IsNullOrWhiteSpace($CudaToolkitRoot)) {
    $CudaToolkitRootPath = [System.IO.Path]::GetFullPath($CudaToolkitRoot)
}

if ($Backend -eq "cuda" -and [string]::IsNullOrWhiteSpace($CudaCompiler) -and -not [string]::IsNullOrWhiteSpace($CudaToolkitRootPath)) {
    $candidateNvcc = Join-Path $CudaToolkitRootPath "bin/nvcc.exe"
    if (-not (Test-Path -LiteralPath $candidateNvcc)) {
        $candidateNvcc = Join-Path $CudaToolkitRootPath "bin/nvcc"
    }

    if (Test-Path -LiteralPath $candidateNvcc) {
        $CudaCompiler = $candidateNvcc
    }
}

$SourceRootPath = Resolve-RepoPath $SourceRoot
$BuildRootPath = Resolve-RepoPath $BuildRoot
$BuildDir = Get-BackendArtifactDirectory -Root $BuildRootPath -RuntimeIdentifier $Rid -NativeBackend $Backend
$OutputRootPath = Resolve-RepoPath $OutputRoot
$OutputDir = Get-BackendArtifactDirectory -Root $OutputRootPath -RuntimeIdentifier $Rid -NativeBackend $Backend
$NativeLibraryName = Get-NativeLibraryName -RuntimeIdentifier $Rid
$NativeLibraryPattern = Get-NativeLibraryPattern -RuntimeIdentifier $Rid
$PackageNativeDir = Get-PackageNativeDirectory -RuntimeIdentifier $Rid -NativeBackend $Backend
$StagedPackagePath = Join-Path $PackageNativeDir $NativeLibraryName

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

if ($Backend -eq "cuda") {
    Assert-CudaToolchain -CudaArchList $CudaArchitectures -CudaCompilerPath $CudaCompiler
}

 $ConfigureArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Generator)) {
    $ConfigureArgs += @("-G", $Generator)
}

if (-not [string]::IsNullOrWhiteSpace($Platform)) {
    $ConfigureArgs += @("-A", $Platform)
}

if (-not [string]::IsNullOrWhiteSpace($Toolset)) {
    $ConfigureArgs += @("-T", $Toolset)
}

$ConfigureArgs += @(
    "-S", $SourceRootPath,
    "-B", $BuildDir,
    "-DBUILD_SHARED_LIBS=ON",
    "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DCMAKE_BUILD_WITH_INSTALL_RPATH=ON",
    '-DCMAKE_INSTALL_RPATH=$ORIGIN',
    "-DLLAMA_BUILD_TESTS=OFF",
    "-DLLAMA_BUILD_EXAMPLES=OFF",
    "-DLLAMA_BUILD_TOOLS=OFF",
    "-DLLAMA_BUILD_COMMON=OFF",
    "-DLLAMA_BUILD_SERVER=OFF"
)

$ConfigureArgs += Get-BackendCMakeArgs -NativeBackend $Backend -CudaArchList $CudaArchitectures -CudaCompilerPath $CudaCompiler -CudaToolkitRootPath $CudaToolkitRootPath

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
    backend = $Backend
    configuration = $Configuration
    cudaArchitectures = if ($Backend -eq "cuda") { $CudaArchitectures } else { $null }
    cudaToolkitRoot = if ($Backend -eq "cuda") { $CudaToolkitRootPath } else { $null }
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
