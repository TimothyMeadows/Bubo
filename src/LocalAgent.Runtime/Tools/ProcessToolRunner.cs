using System.ComponentModel;
using System.Diagnostics;
using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

internal static class ProcessToolRunner
{
    public static async Task<ToolResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
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
                    Error = $"Process could not be started: {executable}"
                };
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return new ToolResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = await outputTask,
                Error = await errorTask
            };
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Executable was not found: {executable}"
            };
        }
    }
}
