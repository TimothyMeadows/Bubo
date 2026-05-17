using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class ListFilesTool : WorkspaceToolBase
{
    public override string Name => "list_files";

    public override string Description => "List files under a workspace path.";

    protected override Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        var requestedPath = request.Arguments.TryGetValue("path", out var value)
            ? value
            : ".";
        var path = guard.ResolveExistingDirectoryInsideWorkspace(requestedPath);
        var files = Directory.EnumerateFiles(path, "*", SafeEnumerationOptions())
            .Take(500)
            .Select(guard.ResolveExistingFileInsideWorkspace)
            .Select(file => Path.GetRelativePath(guard.WorkspaceRoot, file).Replace('\\', '/'));

        return Task.FromResult(new ToolResult
        {
            Success = true,
            Output = string.Join(Environment.NewLine, files)
        });
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
