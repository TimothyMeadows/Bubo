using System.Text.Json.Serialization;

namespace Bubo.Vfx.Domain;

/// <summary>Fixed inputs and finite native processing bounds; no provider, decoder or executable callback.</summary>
public sealed record VfxPreparationRequest
{
    public required string SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string StyleSha256 { get; init; }
    public required VfxEngineIdentity Engine { get; init; }
    public required string Profile { get; init; }
    public required IReadOnlyList<VfxPreparationSource> Sources { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VfxSheet? Sheet { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VfxMathMask? Mask { get; init; }
    public required VfxPreparationTiming Timing { get; init; }
    public required VfxAtlasLayout Atlas { get; init; }
    public required VfxPreparationBudgets Budgets { get; init; }
}

public sealed record VfxPreparationSource
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
}

public sealed record VfxSheet
{
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public required int CellWidth { get; init; }
    public required int CellHeight { get; init; }
    public required int OriginX { get; init; }
    public required int OriginY { get; init; }
    public required int StrideX { get; init; }
    public required int StrideY { get; init; }
    public required IReadOnlyList<int> FrameIndices { get; init; }
}

public sealed record VfxMathMask
{
    public required string Kind { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double InnerRadius { get; init; }
    public required double OuterRadius { get; init; }
    public required double Softness { get; init; }
    public required double AngleDegrees { get; init; }
}

public sealed record VfxPreparationTiming
{
    public required int FirstFrame { get; init; }
    public required int FrameCount { get; init; }
    public required int FrameStep { get; init; }
    public required int RateNumerator { get; init; }
    public required int RateDenominator { get; init; }
    public required string Loop { get; init; }
}

public sealed record VfxAtlasLayout
{
    public required int Columns { get; init; }
    public required int Padding { get; init; }
    public required int Extrusion { get; init; }
    public required string Alpha { get; init; }
    public required string ColorSpace { get; init; }
}

public sealed record VfxPreparationBudgets
{
    public required long MaxSourceBytes { get; init; }
    public required long MaxTotalSourceBytes { get; init; }
    public required long MaxChunkBytes { get; init; }
    public required long MaxCompressedBytes { get; init; }
    public required int MaxFrames { get; init; }
    /// <summary>Per decoded input image, independently of cumulative pixel-work accounting.</summary>
    public required long MaxDecodedPixels { get; init; }
    public required long MaxAtlasPixels { get; init; }
    public required long MaxPixelWork { get; init; }
    /// <summary>Conservative live-allocation bound, not an OS process-memory guarantee.</summary>
    public required long MaxWorkingBytes { get; init; }
    public required long MaxOutputBytes { get; init; }
    public required int MaxOutputFiles { get; init; }
    public required int MaxSeconds { get; init; }
    public required long MinimumFreeDiskBytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? HardProcessMemoryBytes { get; init; }
}
