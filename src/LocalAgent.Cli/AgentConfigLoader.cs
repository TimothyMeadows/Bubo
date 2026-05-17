using System.Text.Json;
using System.Text.Json.Serialization;
using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Cli;

public static class AgentConfigLoader
{
    public const string DefaultConfigFileName = "bubo.config.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AgentConfigLoadResult Load(string workspacePath, string? configPath = null)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new ArgumentException("Workspace path is required.", nameof(workspacePath));
        }

        var defaultConfig = CreateCliDefaultConfig();
        var resolvedConfigPath = ResolveConfigPath(workspacePath, configPath);
        if (!File.Exists(resolvedConfigPath))
        {
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                throw new FileNotFoundException("Config file does not exist.", resolvedConfigPath);
            }

            return new AgentConfigLoadResult
            {
                Config = defaultConfig,
                ConfigPath = resolvedConfigPath,
                WasLoaded = false
            };
        }

        RejectReparsePointPath(resolvedConfigPath);

        BuboConfigFile fileConfig;
        try
        {
            var json = File.ReadAllText(resolvedConfigPath);
            fileConfig = JsonSerializer.Deserialize<BuboConfigFile>(json, JsonOptions) ?? new BuboConfigFile();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"Invalid Bubo config JSON in `{resolvedConfigPath}`: {exception.Message}",
                exception);
        }

        return new AgentConfigLoadResult
        {
            Config = Apply(
                defaultConfig,
                fileConfig,
                allowTrustedSandboxPolicy: !string.IsNullOrWhiteSpace(configPath)),
            ConfigPath = resolvedConfigPath,
            WasLoaded = true
        };
    }

    private static AgentRunConfig CreateCliDefaultConfig()
    {
        return new AgentRunConfig
        {
            Sandbox = new SandboxOptions
            {
                Gpu = null,
                ModelsPath = null
            }
        };
    }

    private static string ResolveConfigPath(string workspacePath, string? configPath)
    {
        var path = string.IsNullOrWhiteSpace(configPath)
            ? Path.Combine(Path.GetFullPath(workspacePath), DefaultConfigFileName)
            : configPath;

        return Path.GetFullPath(path);
    }

    private static AgentRunConfig Apply(
        AgentRunConfig defaults,
        BuboConfigFile fileConfig,
        bool allowTrustedSandboxPolicy)
    {
        return defaults with
        {
            Mode = ParseMode(fileConfig.Mode, defaults.Mode),
            Planner = ApplyModelProfile(defaults.Planner, fileConfig.Models?.Planner),
            Coder = ApplyModelProfile(defaults.Coder, fileConfig.Models?.Coder),
            Sandbox = ApplySandbox(defaults.Sandbox, fileConfig.Sandbox, allowTrustedSandboxPolicy),
            Limits = ApplyLimits(defaults.Limits, fileConfig.Limits)
        };
    }

    private static ModelProfile ApplyModelProfile(
        ModelProfile defaults,
        ModelProfileConfig? config)
    {
        if (config is null)
        {
            return defaults;
        }

        return defaults with
        {
            Role = config.Role ?? defaults.Role,
            Family = config.Family ?? defaults.Family,
            Path = config.Path ?? defaults.Path,
            ContextSize = GetPositive(config.ContextSize, defaults.ContextSize, "contextSize"),
            Temperature = GetNonNegative(config.Temperature, defaults.Temperature, "temperature"),
            TopP = GetProbability(config.TopP, defaults.TopP, "topP"),
            RepeatPenalty = GetPositive(config.RepeatPenalty, defaults.RepeatPenalty, "repeatPenalty"),
            MaxTokens = GetPositive(config.MaxTokens, defaults.MaxTokens, "maxTokens"),
            GpuLayers = config.GpuLayers ?? defaults.GpuLayers,
            Threads = GetThreadCount(config.Threads, defaults.Threads)
        };
    }

    private static SandboxOptions ApplySandbox(
        SandboxOptions defaults,
        SandboxOptionsConfig? config,
        bool allowTrustedSandboxPolicy)
    {
        if (config is null)
        {
            return defaults;
        }

        RejectAutoSandboxEscalation(config, allowTrustedSandboxPolicy);
        RejectHostMountOverride(config.WorkspacePath, "sandbox.workspacePath");
        RejectHostMountOverride(config.InputPath, "sandbox.inputPath");
        RejectHostMountOverride(config.OutputPath, "sandbox.outputPath");
        RejectHostMountOverride(config.CachePath, "sandbox.cachePath");
        RejectHostMountOverride(config.ContainerWorkingDirectory, "sandbox.containerWorkingDirectory");
        RejectDisabledDocker(config.UseDocker);
        RejectDisabledHardening(config.RemoveContainer, defaults.RemoveContainer, "sandbox.removeContainer");
        RejectDisabledHardening(config.ReadOnlyRootFilesystem, defaults.ReadOnlyRootFilesystem, "sandbox.readOnlyRootFilesystem");
        RejectDisabledHardening(config.DropAllCapabilities, defaults.DropAllCapabilities, "sandbox.dropAllCapabilities");
        RejectDisabledHardening(config.NoNewPrivileges, defaults.NoNewPrivileges, "sandbox.noNewPrivileges");

        return defaults with
        {
            Image = config.Image ?? defaults.Image,
            UseDocker = config.UseDocker ?? defaults.UseDocker,
            WorkspacePath = defaults.WorkspacePath,
            InputPath = defaults.InputPath,
            OutputPath = defaults.OutputPath,
            ModelsPath = config.ModelsPath ?? defaults.ModelsPath,
            CachePath = defaults.CachePath,
            ContainerWorkingDirectory = defaults.ContainerWorkingDirectory,
            Network = ParseNetwork(config.Network, defaults.Network),
            Gpu = ParseGpu(config.Gpu, defaults.Gpu),
            Memory = config.Memory ?? defaults.Memory,
            Cpus = GetNullablePositive(config.Cpus, defaults.Cpus, "cpus"),
            PidsLimit = GetBoundedPositive(config.PidsLimit, defaults.PidsLimit, defaults.PidsLimit, "pidsLimit"),
            RemoveContainer = config.RemoveContainer ?? defaults.RemoveContainer,
            ReadOnlyRootFilesystem = config.ReadOnlyRootFilesystem ?? defaults.ReadOnlyRootFilesystem,
            DropAllCapabilities = config.DropAllCapabilities ?? defaults.DropAllCapabilities,
            NoNewPrivileges = config.NoNewPrivileges ?? defaults.NoNewPrivileges
        };
    }

    private static void RejectAutoSandboxEscalation(
        SandboxOptionsConfig config,
        bool allowTrustedSandboxPolicy)
    {
        if (allowTrustedSandboxPolicy)
        {
            return;
        }

        if (config.Image is not null ||
            config.UseDocker is not null ||
            config.ModelsPath is not null ||
            config.Network is not null ||
            config.Gpu is not null ||
            config.Memory is not null ||
            config.Cpus is not null ||
            config.PidsLimit is not null ||
            config.RemoveContainer is not null ||
            config.ReadOnlyRootFilesystem is not null ||
            config.DropAllCapabilities is not null ||
            config.NoNewPrivileges is not null)
        {
            throw new ArgumentException(
                "Workspace-default bubo.config.json cannot set sandbox policy. Pass the file with --config to explicitly trust sandbox settings.");
        }
    }

    private static void RejectHostMountOverride(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        throw new ArgumentException(
            $"Bubo config `{name}` is not supported. Workspace, input, output, cache, and working-directory paths are derived from the guarded workspace.");
    }

    private static void RejectDisabledDocker(bool? useDocker)
    {
        if (useDocker == false)
        {
            throw new ArgumentException("Bubo config `sandbox.useDocker` cannot be false.");
        }
    }

    private static void RejectDisabledHardening(
        bool? configured,
        bool defaultValue,
        string name)
    {
        if (configured == false && defaultValue)
        {
            throw new ArgumentException($"Bubo config `{name}` cannot disable Docker hardening.");
        }
    }

    private static AgentLimits ApplyLimits(
        AgentLimits defaults,
        AgentLimitsConfig? config)
    {
        if (config is null)
        {
            return defaults;
        }

        return defaults with
        {
            MaxIterations = GetBoundedPositive(config.MaxIterations, defaults.MaxIterations, defaults.MaxIterations, "maxIterations"),
            MaxToolCalls = GetBoundedNonNegative(config.MaxToolCalls, defaults.MaxToolCalls, defaults.MaxToolCalls, "maxToolCalls"),
            MaxCommandSeconds = GetBoundedPositive(config.MaxCommandSeconds, defaults.MaxCommandSeconds, defaults.MaxCommandSeconds, "maxCommandSeconds"),
            MaxPatchBytes = GetBoundedNonNegative(config.MaxPatchBytes, defaults.MaxPatchBytes, defaults.MaxPatchBytes, "maxPatchBytes"),
            MaxFilesChanged = GetBoundedNonNegative(config.MaxFilesChanged, defaults.MaxFilesChanged, defaults.MaxFilesChanged, "maxFilesChanged"),
            MaxTokensPerStep = GetBoundedPositive(config.MaxTokensPerStep, defaults.MaxTokensPerStep, defaults.MaxTokensPerStep, "maxTokensPerStep")
        };
    }

    private static AgentMode ParseMode(string? value, AgentMode fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (string.Equals(value, "local", StringComparison.OrdinalIgnoreCase))
        {
            return AgentMode.Local;
        }

        if (string.Equals(value, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return AgentMode.Cloud;
        }

        throw new ArgumentException($"Unsupported Bubo config mode: {value}");
    }

    private static NetworkPolicy ParseNetwork(string? value, NetworkPolicy fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkPolicy.None;
        }

        if (string.Equals(normalized, "packagerestore", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkPolicy.PackageRestore;
        }

        if (string.Equals(normalized, "research", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkPolicy.Research;
        }

        if (string.Equals(normalized, "full", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkPolicy.Full;
        }

        throw new ArgumentException($"Unsupported Bubo config sandbox.network: {value}");
    }

    private static string? ParseGpu(string? value, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(value, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            return "nvidia";
        }

        throw new ArgumentException($"Unsupported Bubo config sandbox.gpu: {value}");
    }

    private static int GetPositive(int? value, int fallback, string name)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value.Value <= 0)
        {
            throw new ArgumentException($"Bubo config `{name}` must be greater than zero.");
        }

        return value.Value;
    }

    private static int GetBoundedPositive(int? value, int fallback, int maximum, string name)
    {
        var result = GetPositive(value, fallback, name);
        if (result > maximum)
        {
            throw new ArgumentException($"Bubo config `{name}` must not exceed {maximum}.");
        }

        return result;
    }

    private static int GetNonNegative(int? value, int fallback, string name)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value.Value < 0)
        {
            throw new ArgumentException($"Bubo config `{name}` must not be negative.");
        }

        return value.Value;
    }

    private static int GetBoundedNonNegative(int? value, int fallback, int maximum, string name)
    {
        var result = GetNonNegative(value, fallback, name);
        if (result > maximum)
        {
            throw new ArgumentException($"Bubo config `{name}` must not exceed {maximum}.");
        }

        return result;
    }

    private static double GetPositive(double? value, double fallback, string name)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value.Value <= 0)
        {
            throw new ArgumentException($"Bubo config `{name}` must be greater than zero.");
        }

        return value.Value;
    }

    private static double GetNonNegative(double? value, double fallback, string name)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value.Value < 0)
        {
            throw new ArgumentException($"Bubo config `{name}` must not be negative.");
        }

        return value.Value;
    }

    private static double GetProbability(double? value, double fallback, string name)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value.Value <= 0 || value.Value > 1)
        {
            throw new ArgumentException($"Bubo config `{name}` must be greater than zero and less than or equal to one.");
        }

        return value.Value;
    }

    private static double? GetNullablePositive(double? value, double? fallback, string name)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value.Value <= 0)
        {
            throw new ArgumentException($"Bubo config `{name}` must be greater than zero.");
        }

        return value.Value;
    }

    private static int GetThreadCount(int? value, int fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value.Value < 0)
        {
            throw new ArgumentException("Bubo config `threads` must not be negative.");
        }

        return value.Value == 0
            ? Environment.ProcessorCount
            : value.Value;
    }

    private static void RejectReparsePointPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        string? current = File.Exists(fullPath) || Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath);

        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException(
                        $"Bubo config path must not be a symlink or reparse point: {current}");
                }
            }

            var parent = Directory.GetParent(current);
            if (parent is null || string.Equals(parent.FullName, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent.FullName;
        }
    }

    private sealed record BuboConfigFile
    {
        public string? Mode { get; init; }

        public ModelProfilesConfig? Models { get; init; }

        public SandboxOptionsConfig? Sandbox { get; init; }

        public AgentLimitsConfig? Limits { get; init; }
    }

    private sealed record ModelProfilesConfig
    {
        public ModelProfileConfig? Planner { get; init; }

        public ModelProfileConfig? Coder { get; init; }
    }

    private sealed record ModelProfileConfig
    {
        public string? Role { get; init; }

        public string? Family { get; init; }

        public string? Path { get; init; }

        public int? ContextSize { get; init; }

        public double? Temperature { get; init; }

        public double? TopP { get; init; }

        public double? RepeatPenalty { get; init; }

        public int? MaxTokens { get; init; }

        public string? GpuLayers { get; init; }

        public int? Threads { get; init; }
    }

    private sealed record SandboxOptionsConfig
    {
        public string? Image { get; init; }

        public bool? UseDocker { get; init; }

        public string? WorkspacePath { get; init; }

        public string? InputPath { get; init; }

        public string? OutputPath { get; init; }

        public string? ModelsPath { get; init; }

        public string? CachePath { get; init; }

        public string? ContainerWorkingDirectory { get; init; }

        public string? Network { get; init; }

        public string? Gpu { get; init; }

        public string? Memory { get; init; }

        public double? Cpus { get; init; }

        public int? PidsLimit { get; init; }

        public bool? RemoveContainer { get; init; }

        public bool? ReadOnlyRootFilesystem { get; init; }

        public bool? DropAllCapabilities { get; init; }

        public bool? NoNewPrivileges { get; init; }
    }

    private sealed record AgentLimitsConfig
    {
        public int? MaxIterations { get; init; }

        public int? MaxToolCalls { get; init; }

        public int? MaxCommandSeconds { get; init; }

        public int? MaxPatchBytes { get; init; }

        public int? MaxFilesChanged { get; init; }

        public int? MaxTokensPerStep { get; init; }
    }
}
