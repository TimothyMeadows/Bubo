using Bubo.LocalAgent.Cli;

namespace Bubo.LocalAgent.Cli.Tests;

public sealed class ProgramE2ETests
{
    [Fact]
    public async Task RunCommandExecutesFileFixtureActions()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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
    public async Task RunCommandReturnsFailureWhenRuntimeFails()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var debugLog = await File.ReadAllTextAsync(Path.Combine(workspace, "agent-debug.jsonl"));
        Assert.Contains("\"mode\":\"Local\"", debugLog);
    }

    [Fact]
    public async Task RunCommandUsesConfigModeWhenModeIsOmitted()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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
            "--workspace",
            workspace,
            "--input",
            inputPath,
            "--output",
            outputPath
        });

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace, "mode.txt")));

        var debugLog = await File.ReadAllTextAsync(Path.Combine(workspace, "agent-debug.jsonl"));
        Assert.Contains("\"mode\":\"Cloud\"", debugLog);
    }

    [Fact]
    public async Task RunCommandReportsInvalidConfig()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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
        Assert.Contains("runtimes", message);
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
}
