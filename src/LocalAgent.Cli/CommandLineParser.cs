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

        if (string.Equals(args[0], "doctor", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSimpleCommand(args, "doctor");
        }

        if (string.Equals(args[0], "models", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNestedSimpleCommand(args, "list", "models-list");
        }

        if (string.Equals(args[0], "native", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNative(args);
        }

        if (!string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            return ParseResult.Failure($"Unknown command: {args[0]}");
        }

        var workspace = Environment.CurrentDirectory;
        string? input = null;
        string? output = null;
        string? configPath = null;
        var mode = AgentMode.Local;
        var modeWasSpecified = false;

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

                    modeWasSpecified = true;
                    break;
                case "--config":
                    configPath = value;
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
            Mode = mode,
            ModeWasSpecified = modeWasSpecified,
            ConfigPath = configPath
        });
    }

    public static string GetUsage()
    {
        return """
               Usage:
                 bubo run --workspace <path> --input <INPUT.md> --output <OUTPUT.md> --mode <local|cloud> --config <bubo.config.json>
                 bubo doctor
                 bubo models list
                 bubo sandbox test --workspace <path> --gpu <none|nvidia>
                 bubo native test --base-directory <path> --backend <cpu|cuda|metal|vulkan> --strict

               Defaults:
                 --workspace current directory
                 --input <workspace>/INPUT.md
                 --output <workspace>/OUTPUT.md
                 --mode local
                 --config <workspace>/bubo.config.json when present
               """;
    }

    private static ParseResult ParseSandbox(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !string.Equals(args[1], "test", StringComparison.OrdinalIgnoreCase))
        {
            return ParseResult.Failure("Unsupported sandbox command. Use: bubo sandbox test");
        }

        var workspace = Environment.CurrentDirectory;
        string? gpu = null;
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

            if (string.Equals(current, "--gpu", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(value, "nvidia", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseResult.Failure($"Unsupported GPU mode: {value}");
                }

                gpu = string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "nvidia";
                continue;
            }

            return ParseResult.Failure($"Unknown option: {current}");
        }

        return ParseResult.Success(new CommandLineOptions
        {
            Command = "sandbox-test",
            WorkspacePath = workspace,
            InputPath = Path.Combine(workspace, "INPUT.md"),
            OutputPath = Path.Combine(workspace, "OUTPUT.md"),
            SandboxGpu = gpu
        });
    }

    private static ParseResult ParseNative(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !string.Equals(args[1], "test", StringComparison.OrdinalIgnoreCase))
        {
            return ParseResult.Failure("Unsupported native command. Use: bubo native test");
        }

        string? baseDirectory = null;
        var strict = false;
        var backend = "cpu";
        for (var index = 2; index < args.Count; index++)
        {
            var current = args[index];
            if (IsHelp(current))
            {
                return ParseResult.Help();
            }

            if (string.Equals(current, "--strict", StringComparison.OrdinalIgnoreCase))
            {
                strict = true;
                continue;
            }

            if (index + 1 >= args.Count)
            {
                return ParseResult.Failure($"Missing value for option: {current}");
            }

            var value = args[++index];
            if (string.Equals(current, "--base-directory", StringComparison.OrdinalIgnoreCase))
            {
                baseDirectory = value;
                continue;
            }

            if (string.Equals(current, "--backend", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSupportedNativeBackend(value))
                {
                    return ParseResult.Failure($"Unsupported native backend: {value}");
                }

                backend = value.ToLowerInvariant();
                continue;
            }

            return ParseResult.Failure($"Unknown option: {current}");
        }

        var workspace = Environment.CurrentDirectory;
        return ParseResult.Success(new CommandLineOptions
        {
            Command = "native-test",
            WorkspacePath = workspace,
            InputPath = Path.Combine(workspace, "INPUT.md"),
            OutputPath = Path.Combine(workspace, "OUTPUT.md"),
            NativeBaseDirectory = baseDirectory,
            NativeStrict = strict,
            NativeBackend = backend
        });
    }

    private static ParseResult ParseSimpleCommand(IReadOnlyList<string> args, string command)
    {
        if (args.Count > 1 && IsHelp(args[1]))
        {
            return ParseResult.Help();
        }

        if (args.Count > 1)
        {
            return ParseResult.Failure($"Unexpected argument for {args[0]}: {args[1]}");
        }

        var workspace = Environment.CurrentDirectory;
        return ParseResult.Success(new CommandLineOptions
        {
            Command = command,
            WorkspacePath = workspace,
            InputPath = Path.Combine(workspace, "INPUT.md"),
            OutputPath = Path.Combine(workspace, "OUTPUT.md")
        });
    }

    private static ParseResult ParseNestedSimpleCommand(
        IReadOnlyList<string> args,
        string expectedSubcommand,
        string command)
    {
        if (args.Count < 2 || !string.Equals(args[1], expectedSubcommand, StringComparison.OrdinalIgnoreCase))
        {
            return ParseResult.Failure($"Unsupported {args[0]} command. Use: bubo {args[0]} {expectedSubcommand}");
        }

        if (args.Count > 2 && IsHelp(args[2]))
        {
            return ParseResult.Help();
        }

        if (args.Count > 2)
        {
            return ParseResult.Failure($"Unexpected argument for {args[0]} {expectedSubcommand}: {args[2]}");
        }

        var workspace = Environment.CurrentDirectory;
        return ParseResult.Success(new CommandLineOptions
        {
            Command = command,
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

    private static bool IsSupportedNativeBackend(string value)
    {
        return string.Equals(value, "cpu", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "cuda", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "metal", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "vulkan", StringComparison.OrdinalIgnoreCase);
    }
}
