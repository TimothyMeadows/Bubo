using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime;

public sealed class WorkspaceGuard : IWorkspaceGuard
{
    private readonly string _workspaceRootWithSeparator;
    private readonly StringComparison _pathComparison;

    public WorkspaceGuard(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        _workspaceRootWithSeparator = EnsureTrailingSeparator(WorkspaceRoot);
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public string WorkspaceRoot { get; }

    public string ResolveInsideWorkspace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(WorkspaceRoot, path));

        if (!IsInsideWorkspace(fullPath))
        {
            throw new UnauthorizedAccessException(
                $"Path resolves outside the workspace: {path}");
        }

        return fullPath;
    }

    public bool IsInsideWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(WorkspaceRoot, path));

        return string.Equals(fullPath, WorkspaceRoot, _pathComparison) ||
               fullPath.StartsWith(_workspaceRootWithSeparator, _pathComparison);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ||
               path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
