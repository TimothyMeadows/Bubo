using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Runtime;

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
}
