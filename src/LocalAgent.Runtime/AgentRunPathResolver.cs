using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime;

internal static class AgentRunPathResolver
{
    private static readonly string[] MarkdownExtensions = { ".md", ".markdown" };
    private static readonly string[] ArtifactRootSegments = { ".ai", "artifacts" };

    public static AgentRunPaths ResolveWorkspaceAndOutput(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workspaceRoot = Path.GetFullPath(request.WorkspacePath);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Workspace does not exist: {workspaceRoot}");
        }

        RejectReparsePointSegments(workspaceRoot, includeTarget: true, description: "Workspace path");

        var guard = new WorkspaceGuard(workspaceRoot);
        var outputPath = ResolveOutputPath(request.OutputPath, guard);
        return new AgentRunPaths(workspaceRoot, outputPath);
    }

    public static async Task<AgentRunInput> ReadInputAsync(
        string input,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (TryResolveExistingInputPath(input, guard, out var inputPath))
        {
            return new AgentRunInput(
                await File.ReadAllTextAsync(inputPath, cancellationToken),
                inputPath,
                IsInline: false);
        }

        if (LooksLikeMissingFilePath(input))
        {
            var missingPath = TryGetFullPath(input, out var fullPath)
                ? fullPath
                : input;
            throw new FileNotFoundException("Input Markdown file does not exist.", missingPath);
        }

        return new AgentRunInput(input, "inline markdown", IsInline: true);
    }

    public static string DescribeInput(string input, WorkspaceGuard guard)
    {
        if (TryResolveExistingInputPath(input, guard, out var inputPath))
        {
            return guard.IsInsideWorkspace(inputPath)
                ? Path.GetRelativePath(guard.WorkspaceRoot, inputPath)
                : inputPath;
        }

        return LooksLikeMissingFilePath(input)
            ? input
            : "inline markdown";
    }

    private static bool TryResolveExistingInputPath(
        string path,
        WorkspaceGuard guard,
        out string inputPath)
    {
        inputPath = string.Empty;
        if (!TryGetFullPath(path, out var fullPath))
        {
            return false;
        }

        if (guard.IsInsideWorkspace(fullPath))
        {
            var workspaceInputPath = guard.ResolveExistingFileInsideWorkspace(fullPath);
            if (!File.Exists(workspaceInputPath))
            {
                return false;
            }

            RejectNonMarkdownInput(workspaceInputPath);
            inputPath = workspaceInputPath;
            return true;
        }

        if (!File.Exists(fullPath))
        {
            return false;
        }

        RejectReparsePointSegments(fullPath, includeTarget: true, description: "Input path");
        RejectNonMarkdownInput(fullPath);
        inputPath = fullPath;
        return true;
    }

    private static bool LooksLikeMissingFilePath(string input)
    {
        if (input.Contains('\r') ||
            input.Contains('\n'))
        {
            return false;
        }

        if (input.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (Path.IsPathRooted(input))
        {
            return true;
        }

        if (input.Contains(Path.DirectorySeparatorChar) ||
            input.Contains(Path.AltDirectorySeparatorChar))
        {
            return true;
        }

        var extension = Path.GetExtension(input);
        return MarkdownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static void RejectNonMarkdownInput(string path)
    {
        var extension = Path.GetExtension(path);
        if (!MarkdownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Input file must be a Markdown file with a .md or .markdown extension: {path}");
        }
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static string ResolveOutputPath(string path, WorkspaceGuard guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var artifactRoot = GetArtifactRoot(guard);
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : ResolveRelativeOutputPath(path, guard, artifactRoot);
        if (Path.IsPathRooted(path) && !IsInsideArtifactRoot(fullPath, artifactRoot))
        {
            throw new UnauthorizedAccessException(
                $"Output path must resolve under the workspace artifact directory: {artifactRoot}");
        }

        if (!IsInsideArtifactRoot(fullPath, artifactRoot))
        {
            throw new UnauthorizedAccessException(
                $"Output path must resolve under the workspace artifact directory: {artifactRoot}");
        }

        fullPath = guard.ResolveWritableFileInsideWorkspace(fullPath);
        if (Directory.Exists(fullPath))
        {
            throw new IOException($"Output path points to a directory: {fullPath}");
        }

        return fullPath;
    }

    private static string ResolveRelativeOutputPath(
        string path,
        WorkspaceGuard guard,
        string artifactRoot)
    {
        var segments = path.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            string.Equals(segments[0], ".ai", PathComparison) &&
            string.Equals(segments[1], "artifacts", PathComparison))
        {
            return Path.Combine(guard.WorkspaceRoot, path);
        }

        if (segments.Length > 0 &&
            string.Equals(segments[0], ".ai", PathComparison))
        {
            throw new UnauthorizedAccessException(
                $"Output path must resolve under the workspace artifact directory: {artifactRoot}");
        }

        return Path.Combine(artifactRoot, path);
    }

    private static string GetArtifactRoot(WorkspaceGuard guard)
    {
        return Path.Combine(
            guard.WorkspaceRoot,
            Path.Combine(ArtifactRootSegments));
    }

    private static bool IsInsideArtifactRoot(string path, string artifactRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var fullArtifactRoot = Path.GetFullPath(artifactRoot);
        var artifactRootWithSeparator = EnsureTrailingSeparator(fullArtifactRoot);
        return string.Equals(fullPath, fullArtifactRoot, PathComparison) ||
               fullPath.StartsWith(artifactRootWithSeparator, PathComparison);
    }

    private static void RejectReparsePointSegments(
        string path,
        bool includeTarget,
        string description)
    {
        var fullPath = Path.GetFullPath(path);
        var current = includeTarget
            ? fullPath
            : Path.GetDirectoryName(fullPath);
        var segments = new List<string>();

        while (!string.IsNullOrWhiteSpace(current))
        {
            segments.Add(current);
            var parent = Directory.GetParent(current);
            if (parent is null ||
                string.Equals(parent.FullName, current, PathComparison))
            {
                break;
            }

            current = parent.FullName;
        }

        for (var index = segments.Count - 1; index >= 0; index--)
        {
            var segment = segments[index];
            if (!File.Exists(segment) && !Directory.Exists(segment))
            {
                continue;
            }

            var attributes = File.GetAttributes(segment);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"{description} must not contain a symlink or reparse point: {segment}");
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ||
               path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}

internal sealed record AgentRunPaths(
    string WorkspaceRoot,
    string OutputPath);

internal sealed record AgentRunInput(
    string Markdown,
    string Source,
    bool IsInline);
