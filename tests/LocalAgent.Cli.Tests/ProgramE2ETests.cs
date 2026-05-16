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
