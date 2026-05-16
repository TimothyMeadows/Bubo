using Bubo.LocalAgent.Runtime;

namespace Bubo.LocalAgent.Runtime.Tests;

public sealed class WorkspaceGuardTests
{
    [Fact]
    public void ResolveInsideWorkspaceAcceptsRelativeChildPath()
    {
        var workspace = CreateWorkspace();
        var guard = new WorkspaceGuard(workspace);

        var resolved = guard.ResolveInsideWorkspace("src/example.txt");

        Assert.StartsWith(workspace, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveInsideWorkspaceRejectsTraversalOutsideWorkspace()
    {
        var workspace = CreateWorkspace();
        var guard = new WorkspaceGuard(workspace);

        Assert.Throws<UnauthorizedAccessException>(
            () => guard.ResolveInsideWorkspace(Path.Combine("..", "outside.txt")));
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "bubo-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return Path.GetFullPath(workspace);
    }
}
