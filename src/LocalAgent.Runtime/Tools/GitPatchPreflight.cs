namespace Bubo.LocalAgent.Runtime.Tools;

internal static class GitPatchPreflight
{
    private static readonly string[] DangerousModeMarkers =
    {
        "new file mode 120000",
        "old mode 120000",
        "new mode 120000",
        "new file mode 160000",
        "old mode 160000",
        "new mode 160000",
        "GIT binary patch",
        "Binary files "
    };

    public static IReadOnlyList<string> Scan(string patch, int maxFilesChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patch);
        if (maxFilesChanged <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFilesChanged),
                maxFilesChanged,
                "Max files changed must be positive.");
        }

        foreach (var marker in DangerousModeMarkers)
        {
            if (patch.Contains(marker, StringComparison.Ordinal))
            {
                throw new ArgumentException($"git_apply_patch rejects unsupported patch marker: {marker}");
            }
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(patch);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                AddDiffGitPaths(paths, line);
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal) ||
                line.StartsWith("+++ ", StringComparison.Ordinal) ||
                line.StartsWith("rename from ", StringComparison.Ordinal) ||
                line.StartsWith("rename to ", StringComparison.Ordinal) ||
                line.StartsWith("copy from ", StringComparison.Ordinal) ||
                line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                AddPatchPath(paths, line[(line.IndexOf(' ') + 1)..]);
            }
        }

        if (paths.Count == 0)
        {
            throw new ArgumentException("git_apply_patch patch does not contain any file paths.");
        }

        if (paths.Count > maxFilesChanged)
        {
            throw new ArgumentException(
                $"git_apply_patch changes {paths.Count} files, exceeding maxFilesChanged ({maxFilesChanged}).");
        }

        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static void AddDiffGitPaths(HashSet<string> paths, string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            throw new ArgumentException("git_apply_patch contains an invalid diff --git header.");
        }

        AddPatchPath(paths, parts[2]);
        AddPatchPath(paths, parts[3]);
    }

    private static void AddPatchPath(HashSet<string> paths, string rawPath)
    {
        var path = NormalizePatchPath(rawPath);
        if (string.IsNullOrWhiteSpace(path) ||
            string.Equals(path, "/dev/null", StringComparison.Ordinal))
        {
            return;
        }

        ValidateRelativePath(path);
        paths.Add(path);
    }

    private static string NormalizePatchPath(string rawPath)
    {
        var path = rawPath.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1];
        }

        if (path.StartsWith("a/", StringComparison.Ordinal) ||
            path.StartsWith("b/", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path.Replace('\\', '/');
    }

    private static void ValidateRelativePath(string path)
    {
        if (path.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(path))
        {
            throw new ArgumentException($"git_apply_patch rejects absolute path: {path}");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException($"git_apply_patch rejects parent traversal path: {path}");
        }

        if (segments.Length > 0 &&
            string.Equals(segments[0], ".git", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"git_apply_patch rejects Git metadata path: {path}");
        }
    }
}
