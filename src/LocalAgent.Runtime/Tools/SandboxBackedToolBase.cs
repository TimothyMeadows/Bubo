using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public abstract class SandboxBackedToolBase : WorkspaceToolBase
{
    private readonly ISandboxRunner? _sandboxRunner;
    private readonly SandboxOptions _sandboxOptions;

    protected SandboxBackedToolBase(
        ISandboxRunner? sandboxRunner,
        SandboxOptions? sandboxOptions)
    {
        _sandboxRunner = sandboxRunner;
        _sandboxOptions = sandboxOptions ?? new SandboxOptions { Gpu = null, ModelsPath = null };
    }

    protected Task<ToolResult> RunSandboxedCommandAsync(
        WorkspaceGuard guard,
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (_sandboxRunner is null)
        {
            throw new ArgumentException(
                $"{Name} requires a Docker sandbox runner for command execution.");
        }

        var containerWorkingDirectory = ToContainerWorkingDirectory(guard, workingDirectory);
        var options = _sandboxOptions with
        {
            WorkspacePath = guard.WorkspaceRoot,
            InputPath = guard.WorkspaceRoot,
            OutputPath = guard.WorkspaceRoot,
            CachePath = guard.WorkspaceRoot,
            ModelsPath = ResolveOptionalDirectory(_sandboxOptions.ModelsPath),
            ContainerWorkingDirectory = containerWorkingDirectory
        };

        return _sandboxRunner.RunCommandAsync(command, arguments, options, cancellationToken);
    }

    private static string ToContainerWorkingDirectory(WorkspaceGuard guard, string workingDirectory)
    {
        var relative = Path.GetRelativePath(guard.WorkspaceRoot, workingDirectory);
        if (relative == ".")
        {
            return "/workspace";
        }

        var containerRelative = relative.Replace('\\', '/');
        return $"/workspace/{containerRelative}";
    }

    private static string? ResolveOptionalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Directory.Exists(path)
            ? path
            : null;
    }
}
