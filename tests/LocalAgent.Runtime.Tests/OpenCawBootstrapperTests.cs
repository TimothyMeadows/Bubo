using System.Diagnostics;
using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tests;

public sealed class OpenCawBootstrapperTests
{
    [Fact]
    public async Task BootstrapAsyncLoadsOpenCawAndHostAiContext()
    {
        var workspace = CreateWorkspace();
        await CreateOpenCawFixtureAsync(workspace, origin: "https://github.com/TimothyMeadows/OpenCaw.git");
        await WriteHostScaffoldAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, ".ai", "FRAGMENTS", "repo-map.md"), "Fragment fact.");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".ai", "LEARNINGS", "workflows.md"), "Learning fact.");

        var result = await new OpenCawBootstrapper().BootstrapAsync(
            new WorkspaceGuard(workspace),
            new OpenCawOptions
            {
                UpdateOnRun = false
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("OpenCaw baseline fixture.", result.SystemPrompt);
        Assert.Contains("Host memory fixture.", result.SystemPrompt);
        Assert.Contains("Fragment fact.", result.SystemPrompt);
        Assert.Contains("Learning fact.", result.SystemPrompt);
        Assert.Contains(result.Events, item => item.Type == "opencaw.verify_origin");
        Assert.Contains(result.Events, item => item.Type == "opencaw.bootstrap_not_needed");
    }

    [Fact]
    public async Task BootstrapAsyncRunsOpenCawScaffoldBeforeLoadingContext()
    {
        if (!IsExecutableAvailable("bash"))
        {
            return;
        }

        var workspace = CreateWorkspace();
        await CreateOpenCawFixtureAsync(workspace, includeScaffoldScript: true);

        var result = await new OpenCawBootstrapper().BootstrapAsync(
            new WorkspaceGuard(workspace),
            new OpenCawOptions
            {
                UpdateOnRun = false
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(workspace, "AGENTS.md")));
        Assert.True(File.Exists(Path.Combine(workspace, ".ai", "MEMORY.md")));
        Assert.Contains("Host memory from scaffold.", result.SystemPrompt);
        Assert.Contains(result.Events, item => item.Type == "opencaw.bootstrap");
    }

    [Fact]
    public async Task BootstrapAsyncRejectsWrongOpenCawOrigin()
    {
        var workspace = CreateWorkspace();
        await CreateOpenCawFixtureAsync(workspace, origin: "https://github.com/example/not-opencaw");
        await WriteHostScaffoldAsync(workspace);

        var result = await new OpenCawBootstrapper().BootstrapAsync(
            new WorkspaceGuard(workspace),
            new OpenCawOptions
            {
                UpdateOnRun = false
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("does not match expected URL", result.Error);
    }

    [Fact]
    public async Task BootstrapAsyncRejectsPathTraversal()
    {
        var workspace = CreateWorkspace();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => new OpenCawBootstrapper().BootstrapAsync(
                new WorkspaceGuard(workspace),
                new OpenCawOptions
                {
                    Path = "../opencaw",
                    UpdateOnRun = false
                },
                CancellationToken.None));
    }

    private static async Task CreateOpenCawFixtureAsync(
        string workspace,
        string origin = "https://github.com/TimothyMeadows/OpenCaw",
        bool includeScaffoldScript = false)
    {
        var openCawPath = Path.Combine(workspace, ".opencaw");
        Directory.CreateDirectory(openCawPath);
        await File.WriteAllTextAsync(Path.Combine(openCawPath, "AGENTS.md"), "OpenCaw baseline fixture.");

        if (includeScaffoldScript)
        {
            var commandsPath = Path.Combine(openCawPath, "commands");
            Directory.CreateDirectory(commandsPath);
            await File.WriteAllTextAsync(
                Path.Combine(commandsPath, "create-host-ai-scaffold.sh"),
                """
                #!/usr/bin/env bash
                set -euo pipefail
                mkdir -p ../.ai/tasks ../.ai/FRAGMENTS ../.ai/LEARNINGS
                [ -f ../AGENTS.md ] || printf '%s\n' '# Host agents from scaffold.' > ../AGENTS.md
                [ -f ../.ai/MEMORY.md ] || printf '%s\n' 'Host memory from scaffold.' > ../.ai/MEMORY.md
                [ -f ../.ai/RULES.md ] || printf '%s\n' 'Host rules from scaffold.' > ../.ai/RULES.md
                [ -f ../.ai/DEBUG.md ] || printf '%s\n' 'Host debug from scaffold.' > ../.ai/DEBUG.md
                [ -f ../.ai/tasks/TODO.md ] || printf '%s\n' '# TODO' > ../.ai/tasks/TODO.md
                [ -f ../.ai/tasks/OPEN_ISSUES.md ] || : > ../.ai/tasks/OPEN_ISSUES.md
                """);
        }

        await RunGitAsync(openCawPath, "init");
        await RunGitAsync(openCawPath, "config", "user.email", "tests@example.invalid");
        await RunGitAsync(openCawPath, "config", "user.name", "Bubo Tests");
        await RunGitAsync(openCawPath, "remote", "add", "origin", origin);
        await RunGitAsync(openCawPath, "add", ".");
        await RunGitAsync(openCawPath, "commit", "-m", "fixture");
    }

    private static async Task WriteHostScaffoldAsync(string workspace)
    {
        Directory.CreateDirectory(Path.Combine(workspace, ".ai", "tasks"));
        Directory.CreateDirectory(Path.Combine(workspace, ".ai", "FRAGMENTS"));
        Directory.CreateDirectory(Path.Combine(workspace, ".ai", "LEARNINGS"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), "# Host agents fixture.");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".ai", "MEMORY.md"), "Host memory fixture.");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".ai", "RULES.md"), "Host rules fixture.");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".ai", "DEBUG.md"), "Host debug fixture.");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".ai", "tasks", "TODO.md"), "# TODO");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".ai", "tasks", "OPEN_ISSUES.md"), string.Empty);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(" ", arguments)} failed: {output}{error}");
        }
    }

    private static bool IsExecutableAvailable(string executable)
    {
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            return false;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { executable, $"{executable}.exe" }
            : new[] { executable };

        return pathEnvironment
            .Split(Path.PathSeparator)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Any(directory => names.Any(name => File.Exists(Path.Combine(directory, name))));
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "bubo-opencaw-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return Path.GetFullPath(workspace);
    }
}
