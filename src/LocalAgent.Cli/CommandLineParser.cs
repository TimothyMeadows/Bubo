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

        string? folder = null;
        string? workspace = null;
        string? input = null;
        string? output = null;
        string? configPath = null;
        var mode = AgentMode.Local;
        var modeWasSpecified = false;
        string? openCawPath = null;
        string? openCawRef = null;
        bool? openCawUpdate = null;

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
                case "--folder":
                    folder = value;
                    break;
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
                case "--opencaw-path":
                    openCawPath = value;
                    break;
                case "--opencaw-ref":
                    openCawRef = value;
                    break;
                case "--opencaw-update":
                    if (!TryParseBoolean(value, out var parsedUpdate))
                    {
                        return ParseResult.Failure($"Unsupported OpenCaw update value: {value}");
                    }

                    openCawUpdate = parsedUpdate;
                    break;
                default:
                    return ParseResult.Failure($"Unknown option: {current}");
            }
        }

        string runFolder;
        try
        {
            runFolder = ResolveRunFolder(folder, workspace);
        }
        catch (ArgumentException exception)
        {
            return ParseResult.Failure(exception.Message);
        }

        input ??= Path.Combine(runFolder, "INPUT.md");
        output ??= Path.Combine(runFolder, ".ai", "artifacts", "run.md");

        return ParseResult.Success(new CommandLineOptions
        {
            Command = "run",
            WorkspacePath = runFolder,
            InputPath = input,
            OutputPath = output,
            Mode = mode,
            ModeWasSpecified = modeWasSpecified,
            ConfigPath = configPath,
            OpenCawPath = openCawPath,
            OpenCawRef = openCawRef,
            OpenCawUpdateOnRun = openCawUpdate
        });
    }

    public static string GetUsage()
    {
        return """
               Usage:
                 bubo run --folder <path> --input <INPUT.md|markdown> --output <artifact-anchor.md> --mode <local|cloud> --config <bubo.config.json>
                 bubo doctor
                 bubo models list
                 bubo sandbox test --workspace <path> --gpu <none|nvidia>
                 bubo native test --base-directory <path> --backend <cpu|cuda|metal|vulkan> --strict

               Defaults:
                 --folder current directory
                 --workspace compatibility alias for --folder
                 --input <folder>/INPUT.md, or inline Markdown when provided explicitly
                 --output artifact sidecar anchor under <folder>/.ai/artifacts; report Markdown is written to stdout
                 --mode local
                 --config <folder>/bubo.config.json when present
                 --opencaw-update true
               """;
    }

    private static string ResolveRunFolder(string? folder, string? workspace)
    {
        if (!string.IsNullOrWhiteSpace(folder) &&
            !string.IsNullOrWhiteSpace(workspace) &&
            !PathsResolveEqual(folder, workspace))
        {
            throw new ArgumentException(
                "--folder and --workspace resolve to different paths. Use one folder value.");
        }

        if (!string.IsNullOrWhiteSpace(folder))
        {
            return folder;
        }

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            return workspace;
        }

        return Environment.CurrentDirectory;
    }

    private static bool PathsResolveEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
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
            OutputPath = Path.Combine(workspace, ".ai", "artifacts", "run.md"),
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
            OutputPath = Path.Combine(workspace, ".ai", "artifacts", "run.md"),
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
            OutputPath = Path.Combine(workspace, ".ai", "artifacts", "run.md")
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
            OutputPath = Path.Combine(workspace, ".ai", "artifacts", "run.md")
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

    private static bool TryParseBoolean(string value, out bool parsed)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            parsed = true;
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
        {
            parsed = false;
            return true;
        }

        parsed = false;
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
