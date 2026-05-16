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
        var path = guard.ResolveInsideWorkspace(requestedPath);
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Take(500)
            .Select(file => Path.GetRelativePath(guard.WorkspaceRoot, file).Replace('\\', '/'));

        return Task.FromResult(new ToolResult
        {
            Success = true,
            Output = string.Join(Environment.NewLine, files)
        });
    }
}
