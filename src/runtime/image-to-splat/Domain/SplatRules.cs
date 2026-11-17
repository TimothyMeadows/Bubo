namespace Bubo.Splats.Domain;

/// <summary>Pure invariants for the initial TripoSplat request profile.</summary>
public static class SplatRules
{
    public const string RequestSchemaVersion = "bubo-splat-request/v1";
    public const string ArtifactSchemaVersion = "bubo-splat-artifact/v1";
    public const string GaussianProfile = "triposplat-ply-sh0/v1";
    public const int LowGaussianCount = 32_768;
    public const int StandardGaussianCount = 131_072;
    public const int MaximumGaussians = 262_144;
    public const long MaximumSeed = 9_007_199_254_740_991;

    public static void ValidateRequest(SplatRequest request)
    {
        if (request is null)
            throw new InvalidOperationException("Splat request is required");
        if (request.SchemaVersion != RequestSchemaVersion)
            throw new InvalidOperationException("Unsupported splat request schemaVersion");
        if (request.Backend != "triposplat")
            throw new InvalidOperationException("Splat backend must be triposplat; no fallback is available");
        RequireSha256(request.StyleSha256, "styleSha256");
        ValidateSource(request.Input);
        ValidateBudgets(request.Budgets);
        if (request.GaussianCounts is null || request.GaussianCounts.Length is < 1 or > 2)
            throw new InvalidOperationException("Splat gaussianCounts must contain one or two declared profiles");
        if (request.GaussianCounts.Distinct().Count() != request.GaussianCounts.Length)
            throw new InvalidOperationException("Splat gaussianCounts must be unique");
        foreach (var count in request.GaussianCounts)
        {
            if (count is not (LowGaussianCount or StandardGaussianCount))
                throw new InvalidOperationException("Initial splat profiles support only 32768 and 131072 Gaussians");
            if (count > request.Budgets.MaxGaussians)
                throw new InvalidOperationException("Splat gaussianCounts exceed the declared Gaussian budget");
        }
        if (request.Seed is < 0 or > MaximumSeed)
            throw new InvalidOperationException("Splat seed must be an exactly representable nonnegative integer up to 9007199254740991");
    }

    public static void ValidateSource(SplatSource source)
    {
        if (source is null)
            throw new InvalidOperationException("Splat input identity is required");
        RequireRelativePath(source.Path);
        RequireSha256(source.Sha256, "input.sha256");
    }

    public static void ValidateArtifact(SplatArtifact artifact)
    {
        if (artifact is null)
            throw new InvalidOperationException("Splat artifact is required");
        if (artifact.SchemaVersion != ArtifactSchemaVersion)
            throw new InvalidOperationException("Unsupported splat artifact schemaVersion");
        if (artifact.Backend != "triposplat" || artifact.Profile != GaussianProfile)
            throw new InvalidOperationException("Splat artifact requires the declared TripoSplat SH0 profile");
        RequireSha256(artifact.StyleSha256, "styleSha256");
        ValidateSource(artifact.Input);
        ValidateSource(artifact.Payload);
        if (string.Equals(artifact.Input.Path, artifact.Payload.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Splat original input and Gaussian payload must use distinct paths");
        ValidateBudgets(artifact.Budgets);
        if (artifact.GaussianCount is not (LowGaussianCount or StandardGaussianCount)
            || artifact.GaussianCount > artifact.Budgets.MaxGaussians)
            throw new InvalidOperationException("Splat artifact count must select an initial density within its declared budget");
        if ((long)artifact.GaussianCount * 17 * sizeof(float) > artifact.Budgets.MaxDecodedBytes)
            throw new InvalidOperationException("Splat artifact decoded-byte budget cannot hold its declared SH0 payload");
        if (artifact.Coordinates != "source-local" || artifact.Units != "uncalibrated" || artifact.ScaleConfidence != "unverified")
            throw new InvalidOperationException("Splat master v1 is source-local, uncalibrated and unverified; physical scale is not established");
        var ancestry = artifact.Ancestry;
        if (ancestry is null)
            throw new InvalidOperationException("Complete splat construction ancestry is required");
        RequireSha256(ancestry.RequestSha256, "ancestry.requestSha256");
        RequireRevision(ancestry.SourceRevision, "ancestry.sourceRevision");
        RequireRevision(ancestry.ModelRevision, "ancestry.modelRevision");
        RequireSha256(ancestry.EnvironmentSha256, "ancestry.environmentSha256");
        RequireSha256(ancestry.RuntimeBuildSha256, "ancestry.runtimeBuildSha256");
        RequireSha256(ancestry.ResourceCatalogSha256, "ancestry.resourceCatalogSha256");
        if (ancestry.ParentArtifactSha256 is not null)
            RequireSha256(ancestry.ParentArtifactSha256, "ancestry.parentArtifactSha256");
    }

    public static void ValidateHash(string hash) => RequireSha256(hash, "sha256");

    public static void ValidateBudgets(SplatBudgets budgets)
    {
        if (budgets is null)
            throw new InvalidOperationException("Explicit splat budgets are required");
        if (budgets.MaxGaussians is < 1 or > MaximumGaussians)
            throw new InvalidOperationException("Splat maxGaussians must be between 1 and 262144");
        if (budgets.MaxInputBytes is < 1 or > 512L * 1024 * 1024)
            throw new InvalidOperationException("Splat maxInputBytes must be between 1 byte and 512 MiB");
        if (budgets.MaxDecodedBytes is < 1 or > 1024L * 1024 * 1024)
            throw new InvalidOperationException("Splat maxDecodedBytes must be between 1 byte and 1 GiB");
        if (budgets.MaxHeaderBytes is < 128 or > 65_536)
            throw new InvalidOperationException("Splat maxHeaderBytes must be between 128 and 65536");
        if (budgets.TimeoutSeconds is < 1 or > 86_400)
            throw new InvalidOperationException("Splat timeoutSeconds must be between 1 and 86400");
        if (budgets.MaxLogBytes is < 1024 or > 16L * 1024 * 1024)
            throw new InvalidOperationException("Splat maxLogBytes must be between 1024 bytes and 16 MiB");
        if (budgets.MaxProcessMemoryBytes is < 64L * 1024 * 1024 or > 128L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Splat maxProcessMemoryBytes must be between 64 MiB and 128 GiB");
    }

    static void RequireSha256(string value, string field)
    {
        if (value is null || value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("Splat " + field + " must be a lowercase SHA-256 identity");
    }

    static void RequireRevision(string value, string field)
    {
        if (value is null || value.Length != 40
            || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("Splat " + field + " must be a complete lowercase 40-character revision");
    }

    static void RequireRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512
            || path.Contains(':') || path.Contains('\\') || path.Any(char.IsControl)
            || path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidOperationException("Splat input path must be a bounded project-relative slash path without colons, backslashes, empty or dot segments, or control characters");
    }
}
