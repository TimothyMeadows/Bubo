using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Sandbox.Docker;

public static class DockerRunCommandBuilder
{
    public static IReadOnlyList<string> BuildRunArguments(
        string command,
        IReadOnlyList<string> commandArguments,
        SandboxOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(commandArguments);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string> { "run" };

        if (options.RemoveContainer)
        {
            arguments.Add("--rm");
        }

        arguments.Add("--workdir");
        arguments.Add("/workspace");

        arguments.Add("--network");
        arguments.Add(ToDockerNetwork(options.Network));

        if (options.DropAllCapabilities)
        {
            arguments.Add("--cap-drop");
            arguments.Add("ALL");
        }

        if (options.NoNewPrivileges)
        {
            arguments.Add("--security-opt");
            arguments.Add("no-new-privileges");
        }

        if (options.ReadOnlyRootFilesystem)
        {
            arguments.Add("--read-only");
            arguments.Add("--tmpfs");
            arguments.Add("/tmp:rw,noexec,nosuid,size=256m");
        }

        if (options.PidsLimit > 0)
        {
            arguments.Add("--pids-limit");
            arguments.Add(options.PidsLimit.ToString());
        }

        if (!string.IsNullOrWhiteSpace(options.Memory))
        {
            arguments.Add("--memory");
            arguments.Add(options.Memory);
        }

        if (options.Cpus is > 0)
        {
            arguments.Add("--cpus");
            arguments.Add(options.Cpus.Value.ToString("0.###"));
        }

        if (string.Equals(options.Gpu, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--gpus");
            arguments.Add("all");
        }

        AddMount(arguments, options.WorkspacePath, "/workspace", readOnly: false);
        AddMount(arguments, options.InputPath, "/input", readOnly: true);
        AddMount(arguments, options.OutputPath, "/output", readOnly: false);
        AddMount(arguments, options.CachePath, "/cache", readOnly: false);

        if (!string.IsNullOrWhiteSpace(options.ModelsPath))
        {
            AddMount(arguments, options.ModelsPath, "/models", readOnly: true);
        }

        arguments.Add("--env");
        arguments.Add($"BUBO_NETWORK_POLICY={options.Network}");
        arguments.Add(options.Image);
        arguments.Add(command);
        arguments.AddRange(commandArguments);
        return arguments;
    }

    private static void AddMount(
        ICollection<string> arguments,
        string hostPath,
        string containerPath,
        bool readOnly)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            throw new ArgumentException("Host mount path must be provided.", nameof(hostPath));
        }

        arguments.Add("--mount");
        arguments.Add($"type=bind,source={Path.GetFullPath(hostPath)},target={containerPath}{(readOnly ? ",readonly" : string.Empty)}");
    }

    private static string ToDockerNetwork(NetworkPolicy policy)
    {
        return policy switch
        {
            NetworkPolicy.None => "none",
            NetworkPolicy.PackageRestore => "bridge",
            NetworkPolicy.Research => "bridge",
            NetworkPolicy.Full => "bridge",
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unsupported network policy.")
        };
    }
}
