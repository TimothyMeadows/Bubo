using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Cli;

public static class CommandLineParser
{
    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || IsHelp(args[0]))
        {
            return ParseResult.Help();
        }

        if (string.Equals(args[0], "sandbox", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSandbox(args);
        }

        if (!string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            return ParseResult.Failure($"Unknown command: {args[0]}");
        }

        var workspace = Environment.CurrentDirectory;
        string? input = null;
        string? output = null;
        var mode = AgentMode.Local;

        for (var index = 1; index < args.Count; index++)
        {
            var current = args[index];
            if (IsHelp(current))
            {
                return ParseResult.Help();
            }

            if (index + 1 >= args.Count)
            {
                return ParseResult.Failure($"Missing value for option: {current}");
            }

            var value = args[++index];
            switch (current)
            {
                case "--workspace":
                    workspace = value;
                    break;
                case "--input":
                    input = value;
                    break;
                case "--output":
                    output = value;
                    break;
                case "--mode":
                    if (!TryParseMode(value, out mode))
                    {
                        return ParseResult.Failure($"Unsupported mode: {value}");
                    }

                    break;
                default:
                    return ParseResult.Failure($"Unknown option: {current}");
            }
        }

        input ??= Path.Combine(workspace, "INPUT.md");
        output ??= Path.Combine(workspace, "OUTPUT.md");

        return ParseResult.Success(new CommandLineOptions
        {
            Command = "run",
            WorkspacePath = workspace,
            InputPath = input,
            OutputPath = output,
            Mode = mode
        });
    }

    public static string GetUsage()
    {
        return """
               Usage:
                 bubo run --workspace <path> --input <INPUT.md> --output <OUTPUT.md> --mode <local|cloud>
                 bubo sandbox test --workspace <path>

               Defaults:
                 --workspace current directory
                 --input <workspace>/INPUT.md
                 --output <workspace>/OUTPUT.md
                 --mode local
               """;
    }

    private static ParseResult ParseSandbox(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !string.Equals(args[1], "test", StringComparison.OrdinalIgnoreCase))
        {
            return ParseResult.Failure("Unsupported sandbox command. Use: bubo sandbox test");
        }

        var workspace = Environment.CurrentDirectory;
        for (var index = 2; index < args.Count; index++)
        {
            var current = args[index];
            if (IsHelp(current))
            {
                return ParseResult.Help();
            }

            if (index + 1 >= args.Count)
            {
                return ParseResult.Failure($"Missing value for option: {current}");
            }

            var value = args[++index];
            if (string.Equals(current, "--workspace", StringComparison.OrdinalIgnoreCase))
            {
                workspace = value;
                continue;
            }

            return ParseResult.Failure($"Unknown option: {current}");
        }

        return ParseResult.Success(new CommandLineOptions
        {
            Command = "sandbox-test",
            WorkspacePath = workspace,
            InputPath = Path.Combine(workspace, "INPUT.md"),
            OutputPath = Path.Combine(workspace, "OUTPUT.md")
        });
    }

    private static bool TryParseMode(string value, out AgentMode mode)
    {
        if (string.Equals(value, "local", StringComparison.OrdinalIgnoreCase))
        {
            mode = AgentMode.Local;
            return true;
        }

        if (string.Equals(value, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            mode = AgentMode.Cloud;
            return true;
        }

        mode = AgentMode.Local;
        return false;
    }

    private static bool IsHelp(string value)
    {
        return string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase);
    }
}
