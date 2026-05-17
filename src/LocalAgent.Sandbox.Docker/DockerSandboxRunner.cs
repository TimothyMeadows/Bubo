using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Sandbox.Docker;

public sealed class DockerSandboxRunner : ISandboxRunner
{
    private readonly string _dockerExecutable;

    public DockerSandboxRunner(string dockerExecutable = "docker")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerExecutable);
        _dockerExecutable = dockerExecutable;
    }

    public Task<ToolResult> RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        SandboxOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(options);

        return RunCommandCoreAsync(command, arguments, options, cancellationToken);
    }

    private async Task<ToolResult> RunCommandCoreAsync(
        string command,
        IReadOnlyList<string> arguments,
        SandboxOptions options,
        CancellationToken cancellationToken)
    {
        var dockerArguments = DockerRunCommandBuilder.BuildRunArguments(command, arguments, options);
        var startInfo = new ProcessStartInfo
        {
            FileName = _dockerExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in dockerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ToolResult
                {
                    Success = false,
                    Error = "Docker process could not be started."
                };
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    stdout.AppendLine(eventArgs.Data);
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    stderr.AppendLine(eventArgs.Data);
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw;
            }

            return new ToolResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = stdout.ToString(),
                Error = stderr.ToString()
            };
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Docker executable was not found: {_dockerExecutable}"
            };
        }
    }
}
