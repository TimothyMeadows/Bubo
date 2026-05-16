using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Inference.CodexCli;

internal static class CodexCliProcess
{
    public static async Task<ToolResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
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
                    Error = "codex-cli process could not be started."
                };
            }

            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

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
                Error = $"codex-cli executable was not found: {executable}"
            };
        }
    }
}
