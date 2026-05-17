using System.Diagnostics;
using Bubo.LocalAgent.Cli;

namespace Bubo.LocalAgent.Cli.Tests;

public sealed class ProgramE2ETests
{
    [Fact]
    public async Task RunCommandExecutesFileFixtureActions()
    {
        var workspace = await CreateWorkspaceWithOpenCawAsync();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(
            inputPath,
            """
            # CLI E2E Fixture

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "generated/cli-result.txt",
                  "content": "CLI fixture completed.\n"
                }
              }
            ]
            ```
            """);

        var exitCode = await Program.Main(new[]
        {
            "run",
            "--opencaw-update",
            "false",
            "--workspace",
            workspace,
            "--input",
            inputPath,
            "--output",
            outputPath,
            "--mode",
            "local"
        });

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        Assert.True(File.Exists(Path.Combine(workspace, "generated", "cli-result.txt")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Bubo executed 1 action", output);
        Assert.Contains("generated/cli-result.txt", output);
    }

    [Fact]
    public async Task RunCommandSupportsFolderWithExternalInputAndWorkspaceOutput()
    {
        var root = CreateWorkspace();
        var folder = Path.Combine(root, "code");
        var prompts = Path.Combine(root, "prompts");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(prompts);
        await CreateOpenCawFixtureAsync(folder);
        await WriteHostScaffoldAsync(folder);
        var inputPath = Path.Combine(prompts, "INPUT.md");
        var outputPath = CreateOutputPath(folder, "reports", "OUTPUT.md");
        await File.WriteAllTextAsync(
            inputPath,
            """
            # CLI Folder Fixture

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "generated/folder-result.txt",
                  "content": "folder fixture completed.\n"
                }
              }
            ]
            ```
            """);

        var exitCode = await Program.Main(new[]
        {
            "run",
            "--opencaw-update",
            "false",
            "--folder",
            folder,
            "--input",
            inputPath,
            "--output",
            outputPath,
            "--mode",
            "local"
        });

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(folder, "generated", "folder-result.txt")));
        Assert.True(File.Exists(outputPath));
        Assert.True(File.Exists(CreateDebugLogPath(folder, "reports")));
        Assert.True(File.Exists(CreateTranscriptPath(folder, "reports")));
        Assert.False(File.Exists(Path.Combine(folder, "OUTPUT.md")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Bubo executed 1 action", output);
        Assert.Contains("generated/folder-result.txt", output);
    }

    [Fact]
    public async Task RunCommandSupportsInlineMarkdownInput()
    {
        var folder = await CreateWorkspaceWithOpenCawAsync();
        var outputPath = CreateOutputPath(folder, "reports", "OUTPUT.md");
        var input =
            """
            # CLI Inline Fixture

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "generated/inline-cli-result.txt",
                  "content": "inline CLI fixture completed.\n"
                }
              }
            ]
            ```
            """;

        var exitCode = await Program.Main(new[]
        {
            "run",
            "--opencaw-update",
            "false",
            "--folder",
            folder,
            "--input",
            input,
            "--output",
            outputPath,
            "--mode",
            "local"
        });

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(folder, "generated", "inline-cli-result.txt")));
        Assert.True(File.Exists(outputPath));

        var debugLog = await File.ReadAllTextAsync(CreateDebugLogPath(folder, "reports"));
        Assert.Contains("inline markdown", debugLog);
    }

    [Fact]
    public async Task RunCommandReturnsFailureWhenRuntimeFails()
    {
        var workspace = await CreateWorkspaceWithOpenCawAsync();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(
            inputPath,
            """
            # CLI Failure Fixture

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "../escape.txt",
                  "content": "nope"
                }
              }
            ]
            ```
            """);

        var exitCode = await Program.Main(new[]
        {
            "run",
            "--opencaw-update",
            "false",
            "--workspace",
            workspace,
            "--input",
            inputPath,
            "--output",
            outputPath,
            "--mode",
            "local"
        });

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(outputPath));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Bubo stopped", output);
        Assert.Contains("write_file failed", output);
    }

    [Fact]
    public async Task RunCommandLoadsWorkspaceConfigByDefault()
    {
        var workspace = await CreateWorkspaceWithOpenCawAsync();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "bubo.config.json"),
            """
            {
              "limits": {
                "maxToolCalls": 1
              }
            }
            """);
        await File.WriteAllTextAsync(
            inputPath,
            """
            # CLI Config Fixture

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "one.txt",
                  "content": "one"
                }
              },
              {
                "tool": "write_file",
                "arguments": {
                  "path": "two.txt",
                  "content": "two"
                }
              }
            ]
            ```
            """);

        var exitCode = await Program.Main(new[]
        {
            "run",
            "--opencaw-update",
            "false",
            "--workspace",
            workspace,
            "--input",
            inputPath,
            "--output",
            outputPath
        });

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(Path.Combine(workspace, "one.txt")));
        Assert.False(File.Exists(Path.Combine(workspace, "two.txt")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("exceeds maxToolCalls (1)", output);
    }

    [Fact]
    public async Task RunCommandLetsExplicitModeOverrideConfigMode()
    {
        var workspace = await CreateWorkspaceWithOpenCawAsync();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "bubo.config.json"),
            """
            {
              "mode": "cloud"
            }
            """);
        await File.WriteAllTextAsync(
            inputPath,
            """
            # CLI Config Mode Fixture

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "mode.txt",
                  "content": "cloud mode from config"
                }
              }
            ]
            ```
            """);

        var exitCode = await Program.Main(new[]
        {
            "run",
            "--opencaw-update",
            "false",
            "--workspace",
            workspace,
            "--input",
            inputPath,
            "--output",
            outputPath,
            "--mode",
            "local"
        });

        Assert.Equal(0, exitCode);

        var debugLog = await File.ReadAllTextAsync(CreateDebugLogPath(workspace));
        Assert.Contains("\"mode\":\"Local\"", debugLog);
    }

    [Fact]
    public async Task RunCommandUsesConfigModeWhenModeIsOmitted()
    {
        var workspace = await CreateWorkspaceWithOpenCawAsync();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "bubo.config.json"),
            """
            {
              "mode": "cloud"
            }
            """);
        await File.WriteAllTextAsync(
            inputPath,
            """
            # CLI Config Mode Fixture

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "mode.txt",
                  "content": "cloud mode from config"
                }
              }
            ]
            ```
            """);

        var exitCode = await Program.Main(new[]
        {
            "run",
            "--opencaw-update",
            "false",
            "--workspace",
            workspace,
            "--input",
            inputPath,
            "--output",
            outputPath
        });

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace, "mode.txt")));

        var debugLog = await File.ReadAllTextAsync(CreateDebugLogPath(workspace));
        Assert.Contains("\"mode\":\"Cloud\"", debugLog);
    }

    [Fact]
    public async Task RunCommandReportsInvalidConfig()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Invalid Config Fixture");
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "bubo.config.json"),
            """
            {
              "mode": "orbital"
            }
            """);

        var exitCode = await Program.Main(new[]
        {
            "run",
            "--workspace",
            workspace,
            "--input",
            inputPath,
            "--output",
            outputPath
        });

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task NativeTestReportsMissingStrictBaseDirectoryAsset()
    {
        var baseDirectory = CreateWorkspace();
        using var error = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(error);
        try
        {
            var exitCode = await Program.Main(new[]
            {
                "native",
                "test",
                "--base-directory",
                baseDirectory,
                "--backend",
                "cuda",
                "--strict"
            });

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var message = error.ToString();
        Assert.Contains("Unable to load llama.cpp native library", message);
        Assert.Contains("backend 'cuda'", message);
        Assert.Contains("runtimes", message);
        Assert.Contains(Path.Combine("native", "cuda"), message);
        Assert.DoesNotContain("or by name", message);
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "bubo-cli-e2e-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return Path.GetFullPath(workspace);
    }

    private static async Task<string> CreateWorkspaceWithOpenCawAsync()
    {
        var workspace = CreateWorkspace();
        await CreateOpenCawFixtureAsync(workspace);
        await WriteHostScaffoldAsync(workspace);
        return workspace;
    }

    private static async Task CreateOpenCawFixtureAsync(string workspace)
    {
        var openCawPath = Path.Combine(workspace, ".opencaw");
        Directory.CreateDirectory(openCawPath);
        await File.WriteAllTextAsync(
            Path.Combine(openCawPath, "AGENTS.md"),
            "OpenCaw baseline fixture for CLI tests.");

        await RunGitAsync(openCawPath, "init");
        await RunGitAsync(openCawPath, "config", "user.email", "tests@example.invalid");
        await RunGitAsync(openCawPath, "config", "user.name", "Bubo Tests");
        await RunGitAsync(openCawPath, "remote", "add", "origin", "https://github.com/TimothyMeadows/OpenCaw");
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

    private static string CreateOutputPath(string workspace, params string[] relativeSegments)
    {
        return CreateArtifactPath(
            workspace,
            relativeSegments.Length == 0 ? new[] { "OUTPUT.md" } : relativeSegments);
    }

    private static string CreateDebugLogPath(string workspace, params string[] relativeDirectorySegments)
    {
        return CreateArtifactPath(
            workspace,
            relativeDirectorySegments.Concat(new[] { "agent-debug.jsonl" }).ToArray());
    }

    private static string CreateTranscriptPath(string workspace, params string[] relativeDirectorySegments)
    {
        return CreateArtifactPath(
            workspace,
            relativeDirectorySegments.Concat(new[] { "agent-transcript.md" }).ToArray());
    }

    private static string CreateArtifactPath(string workspace, params string[] relativeSegments)
    {
        return Path.Combine(
            new[] { workspace, ".ai", "artifacts" }
                .Concat(relativeSegments)
                .ToArray());
    }
}
