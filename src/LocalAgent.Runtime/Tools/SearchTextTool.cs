using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class SearchTextTool : WorkspaceToolBase
{
    public override string Name => "search_text";

    public override string Description => "Search workspace text files for a literal pattern.";

    protected override async Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        var pattern = GetArgument(request, "pattern");
        var requestedPath = request.Arguments.TryGetValue("path", out var value)
            ? value
            : ".";
        var root = guard.ResolveExistingDirectoryInsideWorkspace(requestedPath);
        var matches = new List<string>();

        foreach (var discoveredFile in Directory.EnumerateFiles(root, "*", SafeEnumerationOptions()).Take(1_000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = guard.ResolveExistingFileInsideWorkspace(discoveredFile);
            var lineNumber = 0;
            foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken))
            {
                lineNumber++;
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add($"{Path.GetRelativePath(guard.WorkspaceRoot, file).Replace('\\', '/')}:{lineNumber}:{line.Trim()}");
                }
            }
        }

        return new ToolResult
        {
            Success = true,
            Output = string.Join(Environment.NewLine, matches)
        };
    }

    private static EnumerationOptions SafeEnumerationOptions()
    {
        return new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
    }
}
