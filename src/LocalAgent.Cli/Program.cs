using Bubo.LlamaCppSharp;
using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Inference.CodexCli;
using Bubo.LocalAgent.Inference.LlamaCpp;
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
            switch (parseResult.Options.Command)
            {
                case "doctor":
                    return RunDoctor();
                case "models-list":
                    return RunModelsList();
                case "native-test":
                    return RunNativeTest(parseResult.Options);
                case "sandbox-test":
                    return await RunSandboxTestAsync(parseResult.Options);
            }

            var configLoad = AgentConfigLoader.Load(
                parseResult.Options.WorkspacePath,
                parseResult.Options.ConfigPath);
            var mode = parseResult.Options.ModeWasSpecified
                ? parseResult.Options.Mode
                : configLoad.Config.Mode;
            var effectiveConfig = configLoad.Config with { Mode = mode };
            effectiveConfig = ApplyCliOpenCawOverrides(effectiveConfig, parseResult.Options);
            var runner = CreateAgentRunner(
                mode,
                parseResult.Options.WorkspacePath,
                effectiveConfig);
            var result = await runner.RunAsync(
                new AgentRunRequest
                {
                    WorkspacePath = parseResult.Options.WorkspacePath,
                    InputPath = parseResult.Options.InputPath,
                    OutputPath = parseResult.Options.OutputPath,
                    Mode = mode
                },
                CancellationToken.None);

            return result.Success ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunSandboxTestAsync(CommandLineOptions options)
    {
        if (!DockerAvailability.TryResolveDockerExecutable("docker", out var dockerExecutable))
        {
            Console.Error.WriteLine("Docker executable was not found.");
            return 1;
        }

        var runner = new DockerSandboxRunner(dockerExecutable);
        var result = await runner.RunCommandAsync(
            "sh",
            BuildSandboxTestArguments(options),
            new SandboxOptions
            {
                WorkspacePath = options.WorkspacePath,
                InputPath = options.WorkspacePath,
                OutputPath = options.WorkspacePath,
                CachePath = options.WorkspacePath,
                ModelsPath = null,
                Network = NetworkPolicy.None,
                Gpu = options.SandboxGpu
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

    private static int RunDoctor()
    {
        Console.WriteLine("Bubo doctor");
        Console.WriteLine($"dotnet: {Environment.Version}");
        Console.WriteLine(DockerAvailability.TryResolveDockerExecutable("docker", out var dockerExecutable)
            ? $"docker: available ({dockerExecutable})"
            : "docker: not found");
        Console.WriteLine($"codex-cli: {(IsExecutableLikelyAvailable("codex") ? "available" : "not found")}");

        var native = LlamaRuntimeAvailability.Probe();
        Console.WriteLine(native.Success
            ? $"llama.cpp native: available ({native.ResolvedPath})"
            : $"llama.cpp native: not found ({native.Error})");
        return 0;
    }

    private static int RunModelsList()
    {
        var config = new AgentRunConfig();
        Console.WriteLine("planner");
        WriteModelProfile(config.Planner);
        Console.WriteLine();
        Console.WriteLine("coder");
        WriteModelProfile(config.Coder);
        return 0;
    }

    private static int RunNativeTest(CommandLineOptions options)
    {
        var native = LlamaRuntimeAvailability.Probe(
            options.NativeBaseDirectory,
            allowFallbackByName: !options.NativeStrict,
            backend: options.NativeBackend);
        if (native.Success)
        {
            Console.WriteLine($"Loaded llama.cpp native library from: {native.ResolvedPath}");
            return 0;
        }

        Console.Error.WriteLine(native.Error);
        return 1;
    }

    private static string[] BuildSandboxTestArguments(CommandLineOptions options)
    {
        var command = "git --version && gh --version && dotnet --version";
        if (string.Equals(options.SandboxGpu, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            command += " && nvidia-smi --query-gpu=name,driver_version --format=csv,noheader";
        }

        return new[] { "-lc", command };
    }

    private static void WriteModelProfile(ModelProfile profile)
    {
        Console.WriteLine($"  family: {profile.Family}");
        Console.WriteLine($"  path: {profile.Path ?? "(configure in bubo config)"}");
        Console.WriteLine($"  contextSize: {profile.ContextSize}");
        Console.WriteLine($"  temperature: {profile.Temperature}");
        Console.WriteLine($"  topP: {profile.TopP}");
        Console.WriteLine($"  repeatPenalty: {profile.RepeatPenalty}");
        Console.WriteLine($"  maxTokens: {profile.MaxTokens}");
        Console.WriteLine($"  gpuLayers: {profile.GpuLayers}");
        Console.WriteLine($"  threads: {profile.Threads}");
    }

    private static AgentRunner CreateAgentRunner(
        AgentMode mode,
        string workspacePath,
        AgentRunConfig config)
    {
        var inferenceProvider = CreateInferenceProvider(mode, workspacePath);
        var sandboxOptions = config.Sandbox;

        return sandboxOptions.UseDocker &&
               DockerAvailability.TryResolveDockerExecutable("docker", out var dockerExecutable)
            ? new AgentRunner(
                new DockerSandboxRunner(dockerExecutable),
                sandboxOptions,
                inferenceProvider,
                config)
            : new AgentRunner(
                sandboxOptions: sandboxOptions,
                inferenceProvider: inferenceProvider,
                config: config);
    }

    private static IInferenceProvider CreateInferenceProvider(AgentMode mode, string workspacePath)
    {
        return mode switch
        {
            AgentMode.Cloud => new CodexCliInferenceProvider(
                new CodexCliOptions
                {
                    WorkingDirectory = Path.GetFullPath(workspacePath)
                }),
            _ => new LlamaCppInferenceProvider()
        };
    }

    private static AgentRunConfig ApplyCliOpenCawOverrides(
        AgentRunConfig config,
        CommandLineOptions options)
    {
        if (options.OpenCawPath is null &&
            options.OpenCawRef is null &&
            options.OpenCawUpdateOnRun is null)
        {
            return config;
        }

        return config with
        {
            OpenCaw = config.OpenCaw with
            {
                Path = options.OpenCawPath ?? config.OpenCaw.Path,
                Ref = options.OpenCawRef ?? config.OpenCaw.Ref,
                UpdateOnRun = options.OpenCawUpdateOnRun ?? config.OpenCaw.UpdateOnRun
            }
        };
    }

    private static bool IsExecutableLikelyAvailable(string executable)
    {
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            return false;
        }

        var executableNames = OperatingSystem.IsWindows()
            ? new[] { executable, $"{executable}.exe" }
            : new[] { executable };

        foreach (var directory in pathEnvironment.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var executableName in executableNames)
            {
                if (File.Exists(Path.Combine(directory, executableName)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
