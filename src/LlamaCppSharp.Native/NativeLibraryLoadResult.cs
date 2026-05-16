namespace Bubo.LlamaCppSharp.Native;

public sealed record NativeLibraryLoadResult
{
    public required bool Success { get; init; }

    public nint Handle { get; init; }

    public string? ResolvedPath { get; init; }

    public string? Error { get; init; }
}
