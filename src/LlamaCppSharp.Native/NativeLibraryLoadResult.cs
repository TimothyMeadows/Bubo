namespace Bubo.LlamaCppSharp.Native;

public sealed record NativeLibraryLoadResult
{
    public required bool Success { get; init; }

    public nint Handle { get; init; }

    public string? ResolvedPath { get; init; }

    public string Backend { get; init; } = LlamaNativeLibrary.CpuBackend;

    public string? RuntimeIdentifier { get; init; }

    public string? ExpectedPath { get; init; }

    public bool FallbackUsed { get; init; }

    public string? Error { get; init; }
}
