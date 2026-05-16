using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class RunCommandTool : WorkspaceToolBase
{
    private static readonly HashSet<string> AllowedExecutables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "dotnet",
            "git",
            "gh"
        };

    public override string Name => "run_command";

    public override string Description =>
        "Run an allowlisted executable inside the workspace without invoking a shell.";

    protected override Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        var executable = GetCommandArgument(request);
        if (executable.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
        {
            throw new ArgumentException("run_command executable must be a bare command name.");
        }

        if (!AllowedExecutables.Contains(executable))
        {
            throw new ArgumentException($"run_command executable is not allowlisted: {executable}");
        }

        var workingDirectory = guard.WorkspaceRoot;
        if (request.Arguments.TryGetValue("workingDirectory", out var requestedWorkingDirectory) &&
            !string.IsNullOrWhiteSpace(requestedWorkingDirectory))
        {
            workingDirectory = guard.ResolveInsideWorkspace(requestedWorkingDirectory);
            if (!Directory.Exists(workingDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"run_command working directory does not exist: {requestedWorkingDirectory}");
            }
        }

        return ProcessToolRunner.RunAsync(
            executable,
            ParseArguments(request),
            workingDirectory,
            cancellationToken);
    }

    private static string GetCommandArgument(ToolRequest request)
    {
        if (request.Arguments.TryGetValue("executable", out var executable) &&
            !string.IsNullOrWhiteSpace(executable))
        {
            return executable;
        }

        if (request.Arguments.TryGetValue("command", out var command) &&
            !string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        throw new ArgumentException("Missing required tool argument: executable");
    }

    private static IReadOnlyList<string> ParseArguments(ToolRequest request)
    {
        if (!request.Arguments.TryGetValue("arguments", out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(argument => argument.Length > 0)
            .ToArray();
    }
}
