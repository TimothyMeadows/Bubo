using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Sandbox.Docker;

namespace Bubo.LocalAgent.Sandbox.Docker.Tests;

public sealed class DockerRunCommandBuilderTests
{
    [Fact]
    public void BuildRunArgumentsDefaultsToNoNetworkAndSecurityFlags()
    {
        var workspace = CreateDirectory("workspace");
        var input = CreateDirectory("input");
        var output = CreateDirectory("output");
        var cache = CreateDirectory("cache");
        var models = CreateDirectory("models");

        var args = DockerRunCommandBuilder.BuildRunArguments(
            "dotnet",
            new[] { "--info" },
            new SandboxOptions
            {
                WorkspacePath = workspace,
                InputPath = input,
                OutputPath = output,
                CachePath = cache,
                ModelsPath = models,
                Gpu = null
            });

        Assert.Contains("--network", args);
        Assert.Contains("none", args);
        Assert.Contains("--cap-drop", args);
        Assert.Contains("ALL", args);
        Assert.Contains("--security-opt", args);
        Assert.Contains("no-new-privileges", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("--pids-limit", args);
        Assert.Contains("dotnet", args);
        Assert.Contains("--info", args);
    }

    [Fact]
    public void BuildRunArgumentsMountsInputAndModelsReadOnly()
    {
        var workspace = CreateDirectory("workspace");
        var input = CreateDirectory("input");
        var output = CreateDirectory("output");
        var cache = CreateDirectory("cache");
        var models = CreateDirectory("models");

        var args = DockerRunCommandBuilder.BuildRunArguments(
            "true",
            Array.Empty<string>(),
            new SandboxOptions
            {
                WorkspacePath = workspace,
                InputPath = input,
                OutputPath = output,
                CachePath = cache,
                ModelsPath = models,
                Gpu = null
            });

        Assert.Contains(args, value => value.Contains("target=/input,readonly", StringComparison.Ordinal));
        Assert.Contains(args, value => value.Contains("target=/models,readonly", StringComparison.Ordinal));
        Assert.Contains(args, value => value.Contains("target=/workspace", StringComparison.Ordinal) &&
                                       !value.Contains("target=/workspace,readonly", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRunArgumentsAddsNvidiaGpuWhenRequested()
    {
        var workspace = CreateDirectory("workspace");

        var args = DockerRunCommandBuilder.BuildRunArguments(
            "true",
            Array.Empty<string>(),
            new SandboxOptions
            {
                WorkspacePath = workspace,
                InputPath = workspace,
                OutputPath = workspace,
                CachePath = workspace,
                ModelsPath = null,
                Gpu = "nvidia"
            });

        Assert.Contains("--gpus", args);
        Assert.Contains("all", args);
    }

    [Fact]
    public void BuildRunArgumentsRejectsUnsupportedGpuMode()
    {
        var workspace = CreateDirectory("workspace");

        var exception = Assert.Throws<ArgumentException>(() =>
            DockerRunCommandBuilder.BuildRunArguments(
                "true",
                Array.Empty<string>(),
                new SandboxOptions
                {
                    WorkspacePath = workspace,
                    InputPath = workspace,
                    OutputPath = workspace,
                    CachePath = workspace,
                    ModelsPath = null,
                    Gpu = "amd"
                }));

        Assert.Contains("Unsupported Docker GPU mode", exception.Message);
    }

    [Fact]
    public void BuildRunArgumentsUsesContainerWorkingDirectory()
    {
        var workspace = CreateDirectory("workspace");

        var args = DockerRunCommandBuilder.BuildRunArguments(
            "git",
            new[] { "status" },
            new SandboxOptions
            {
                WorkspacePath = workspace,
                InputPath = workspace,
                OutputPath = workspace,
                CachePath = workspace,
                ModelsPath = null,
                Gpu = null,
                ContainerWorkingDirectory = "/workspace/src"
            });

        var workdirIndex = args.ToList().IndexOf("--workdir");
        Assert.True(workdirIndex >= 0);
        Assert.Equal("/workspace/src", args[workdirIndex + 1]);
    }

    [Fact]
    public void BuildRunArgumentsRejectsSymlinkMountSource()
    {
        var target = CreateDirectory("target");
        var link = Path.Combine(Path.GetTempPath(), "bubo-docker-tests", Guid.NewGuid().ToString("N"), "link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var thrown = Assert.Throws<ArgumentException>(() =>
            DockerRunCommandBuilder.BuildRunArguments(
                "true",
                Array.Empty<string>(),
                new SandboxOptions
                {
                    WorkspacePath = link,
                    InputPath = target,
                    OutputPath = target,
                    CachePath = target,
                    ModelsPath = null,
                    Gpu = null
                }));
        Assert.Contains("symlink or reparse point", thrown.Message);
    }

    private static string CreateDirectory(string name)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "bubo-docker-tests",
            Guid.NewGuid().ToString("N"),
            name);
        Directory.CreateDirectory(path);
        return path;
    }
}
