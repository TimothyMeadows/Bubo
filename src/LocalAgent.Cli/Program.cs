using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Runtime;
using Bubo.LocalAgent.Sandbox.Docker;

namespace Bubo.LocalAgent.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parseResult = CommandLineParser.Parse(args);
        if (parseResult.ShowHelp)
        {
            Console.WriteLine(CommandLineParser.GetUsage());
            return 0;
        }

        if (!parseResult.IsSuccess || parseResult.Options is null)
        {
            Console.Error.WriteLine(parseResult.ErrorMessage);
            Console.Error.WriteLine(CommandLineParser.GetUsage());
            return 2;
        }

        try
        {
            if (string.Equals(parseResult.Options.Command, "sandbox-test", StringComparison.Ordinal))
            {
                return await RunSandboxTestAsync(parseResult.Options);
            }

            var runner = new AgentRunner();
            await runner.RunAsync(
                new AgentRunRequest
                {
                    WorkspacePath = parseResult.Options.WorkspacePath,
                    InputPath = parseResult.Options.InputPath,
                    OutputPath = parseResult.Options.OutputPath,
                    Mode = parseResult.Options.Mode
                },
                CancellationToken.None);

            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunSandboxTestAsync(CommandLineOptions options)
    {
        if (!DockerAvailability.IsDockerLikelyAvailable())
        {
            Console.Error.WriteLine("Docker executable was not found on PATH.");
            return 1;
        }

        var runner = new DockerSandboxRunner();
        var result = await runner.RunCommandAsync(
            "sh",
            new[] { "-lc", "git --version && gh --version && dotnet --version" },
            new SandboxOptions
            {
                WorkspacePath = options.WorkspacePath,
                InputPath = options.WorkspacePath,
                OutputPath = options.WorkspacePath,
                CachePath = options.WorkspacePath,
                ModelsPath = null,
                Network = NetworkPolicy.None,
                Gpu = null
            },
            CancellationToken.None);

        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            Console.Write(result.Output);
        }

        if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
        {
            Console.Error.Write(result.Error);
        }

        return result.Success ? 0 : 1;
    }
}
