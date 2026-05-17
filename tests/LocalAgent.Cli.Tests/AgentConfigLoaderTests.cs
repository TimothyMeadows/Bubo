using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Cli;

namespace Bubo.LocalAgent.Cli.Tests;

public sealed class AgentConfigLoaderTests
{
    [Fact]
    public void LoadReturnsCliSafeDefaultsWhenWorkspaceConfigIsMissing()
    {
        var workspace = CreateWorkspace();

        var result = AgentConfigLoader.Load(workspace);

        Assert.False(result.WasLoaded);
        Assert.Equal(Path.Combine(workspace, "bubo.config.json"), result.ConfigPath);
        Assert.Equal(AgentMode.Local, result.Config.Mode);
        Assert.Null(result.Config.Sandbox.Gpu);
        Assert.Null(result.Config.Sandbox.ModelsPath);
        Assert.Equal(NetworkPolicy.None, result.Config.Sandbox.Network);
        Assert.True(result.Config.OpenCaw.Enabled);
        Assert.True(result.Config.OpenCaw.UpdateOnRun);
        Assert.True(result.Config.OpenCaw.ExecuteBootstrap);
        Assert.Equal(".opencaw", result.Config.OpenCaw.Path);
    }

    [Fact]
    public async Task LoadAppliesExplicitConfigFile()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "custom.bubo.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "mode": "cloud",
              "models": {
                "planner": {
                  "path": "/models/planner-custom.gguf",
                  "contextSize": 16384,
                  "threads": 0
                },
                "coder": {
                  "path": "/models/coder-custom.gguf",
                  "temperature": 0.05,
                  "topP": 0.8,
                  "maxTokens": 2048
                }
              },
              "sandbox": {
                "image": "bubo-sandbox:test",
                "network": "package-restore",
                "gpu": "nvidia",
                "modelsPath": "/mnt/models",
                "cpus": 2.5,
                "pidsLimit": 256
              },
              "limits": {
                "maxToolCalls": 3,
                "maxCommandSeconds": 5,
                "maxPatchBytes": 1024,
                "maxFilesChanged": 4
              }
            }
            """);

        var result = AgentConfigLoader.Load(workspace, configPath);

        Assert.True(result.WasLoaded);
        Assert.Equal(Path.GetFullPath(configPath), result.ConfigPath);
        Assert.Equal(AgentMode.Cloud, result.Config.Mode);
        Assert.Equal("/models/planner-custom.gguf", result.Config.Planner.Path);
        Assert.Equal(16_384, result.Config.Planner.ContextSize);
        Assert.Equal(Environment.ProcessorCount, result.Config.Planner.Threads);
        Assert.Equal("/models/coder-custom.gguf", result.Config.Coder.Path);
        Assert.Equal(0.05, result.Config.Coder.Temperature);
        Assert.Equal(0.8, result.Config.Coder.TopP);
        Assert.Equal(2_048, result.Config.Coder.MaxTokens);
        Assert.Equal("bubo-sandbox:test", result.Config.Sandbox.Image);
        Assert.Equal(NetworkPolicy.PackageRestore, result.Config.Sandbox.Network);
        Assert.Equal("nvidia", result.Config.Sandbox.Gpu);
        Assert.Equal("/mnt/models", result.Config.Sandbox.ModelsPath);
        Assert.Equal(2.5, result.Config.Sandbox.Cpus);
        Assert.Equal(256, result.Config.Sandbox.PidsLimit);
        Assert.Equal(3, result.Config.Limits.MaxToolCalls);
        Assert.Equal(5, result.Config.Limits.MaxCommandSeconds);
        Assert.Equal(1_024, result.Config.Limits.MaxPatchBytes);
        Assert.Equal(4, result.Config.Limits.MaxFilesChanged);
    }

    [Fact]
    public async Task LoadRejectsUnsupportedEnumValues()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "bubo.config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "sandbox": {
                "network": "internet-ish"
              }
            }
            """);

        var exception = Assert.Throws<ArgumentException>(
            () => AgentConfigLoader.Load(workspace, configPath));

        Assert.Contains("sandbox.network", exception.Message);
    }

    [Fact]
    public async Task LoadRejectsUnsupportedGpuValue()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "trusted.bubo.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "sandbox": {
                "gpu": "amd"
              }
            }
            """);

        var exception = Assert.Throws<ArgumentException>(
            () => AgentConfigLoader.Load(workspace, configPath));

        Assert.Contains("sandbox.gpu", exception.Message);
    }

    [Fact]
    public async Task LoadRejectsWorkspaceDefaultSandboxPolicy()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "bubo.config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "sandbox": {
                "network": "full"
              }
            }
            """);

        var exception = Assert.Throws<ArgumentException>(
            () => AgentConfigLoader.Load(workspace));

        Assert.Contains("Workspace-default bubo.config.json cannot set sandbox policy", exception.Message);
    }

    [Fact]
    public async Task LoadRejectsWorkspaceDefaultOpenCawPolicy()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "bubo.config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "openCaw": {
                "path": ".cursor"
              }
            }
            """);

        var exception = Assert.Throws<ArgumentException>(
            () => AgentConfigLoader.Load(workspace));

        Assert.Contains("Workspace-default bubo.config.json cannot set OpenCaw policy", exception.Message);
    }

    [Fact]
    public async Task LoadAppliesExplicitOpenCawConfig()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "trusted.bubo.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "openCaw": {
                "enabled": false,
                "repositoryUrl": "https://github.com/TimothyMeadows/OpenCaw",
                "path": ".cursor",
                "ref": "release/test",
                "updateOnRun": false,
                "executeBootstrap": false
              }
            }
            """);

        var result = AgentConfigLoader.Load(workspace, configPath);

        Assert.False(result.Config.OpenCaw.Enabled);
        Assert.Equal("https://github.com/TimothyMeadows/OpenCaw", result.Config.OpenCaw.RepositoryUrl);
        Assert.Equal(".cursor", result.Config.OpenCaw.Path);
        Assert.Equal("release/test", result.Config.OpenCaw.Ref);
        Assert.False(result.Config.OpenCaw.UpdateOnRun);
        Assert.False(result.Config.OpenCaw.ExecuteBootstrap);
    }

    [Fact]
    public async Task LoadRejectsSandboxHostMountOverrides()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "trusted.bubo.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "sandbox": {
                "workspacePath": "C:/Users"
              }
            }
            """);

        var exception = Assert.Throws<ArgumentException>(
            () => AgentConfigLoader.Load(workspace, configPath));

        Assert.Contains("sandbox.workspacePath", exception.Message);
    }

    [Fact]
    public async Task LoadRejectsDisabledDockerHardening()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "trusted.bubo.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "sandbox": {
                "readOnlyRootFilesystem": false
              }
            }
            """);

        var exception = Assert.Throws<ArgumentException>(
            () => AgentConfigLoader.Load(workspace, configPath));

        Assert.Contains("readOnlyRootFilesystem", exception.Message);
    }

    [Fact]
    public async Task LoadRejectsUnknownMembers()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "bubo.config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "mod": "cloud"
            }
            """);

        var exception = Assert.Throws<ArgumentException>(
            () => AgentConfigLoader.Load(workspace, configPath));

        Assert.Contains("Invalid Bubo config JSON", exception.Message);
    }

    [Fact]
    public async Task LoadRejectsInvalidNumericValues()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "bubo.config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "limits": {
                "maxPatchBytes": -1
              }
            }
            """);

        var exception = Assert.Throws<ArgumentException>(
            () => AgentConfigLoader.Load(workspace, configPath));

        Assert.Contains("maxPatchBytes", exception.Message);
    }

    [Fact]
    public async Task LoadRejectsTimeoutDisable()
    {
        var workspace = CreateWorkspace();
        var configPath = Path.Combine(workspace, "bubo.config.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "limits": {
                "maxCommandSeconds": 0
              }
            }
            """);

        var exception = Assert.Throws<ArgumentException>(
            () => AgentConfigLoader.Load(workspace, configPath));

        Assert.Contains("maxCommandSeconds", exception.Message);
    }

    [Fact]
    public void LoadReadsDocumentedExampleConfigs()
    {
        var repoRoot = FindRepoRoot();
        var workspaceConfig = Path.Combine(repoRoot, "examples", "bubo.config.json");
        var trustedConfig = Path.Combine(repoRoot, "examples", "bubo.trusted.config.json");

        var workspaceResult = AgentConfigLoader.Load(repoRoot, workspaceConfig);
        var trustedResult = AgentConfigLoader.Load(repoRoot, trustedConfig);

        Assert.True(workspaceResult.WasLoaded);
        Assert.Equal(AgentMode.Local, workspaceResult.Config.Mode);
        Assert.Equal(40, workspaceResult.Config.Limits.MaxToolCalls);
        Assert.True(trustedResult.WasLoaded);
        Assert.Equal("bubo-sandbox:local", trustedResult.Config.Sandbox.Image);
        Assert.Equal(NetworkPolicy.PackageRestore, trustedResult.Config.Sandbox.Network);
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "bubo-config-loader-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return Path.GetFullPath(workspace);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bubo.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
