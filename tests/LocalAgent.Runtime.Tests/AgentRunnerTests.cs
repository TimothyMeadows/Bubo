using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Runtime;

namespace Bubo.LocalAgent.Runtime.Tests;

public sealed class AgentRunnerTests
{
    [Fact]
    public async Task RunAsyncWritesOutputDebugAndTranscriptFiles()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nSay hello.");

        var runner = CreateRunner(new FakeSandboxRunner());
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(outputPath));
        Assert.True(File.Exists(CreateDebugLogPath(workspace)));
        Assert.True(File.Exists(CreateTranscriptPath(workspace)));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("# Result", output);
        Assert.Contains("## Summary", output);
        Assert.Contains("## Next Steps", output);
    }

    [Fact]
    public async Task RunAsyncAllowsExternalInputAndWritesArtifactsInWorkspace()
    {
        var root = CreateWorkspace();
        var workspace = Path.Combine(root, "code");
        var inputDirectory = Path.Combine(root, "prompts");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(inputDirectory);

        var inputPath = Path.Combine(inputDirectory, "INPUT.md");
        var outputPath = CreateOutputPath(workspace, "reports", "OUTPUT.md");
        await File.WriteAllTextAsync(
            inputPath,
            """
            # External Prompt

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "generated/result.txt",
                  "content": "folder bounded\n"
                }
              }
            ]
            ```
            """);

        var runner = CreateRunner(new FakeSandboxRunner());
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            "folder bounded\n",
            await File.ReadAllTextAsync(Path.Combine(workspace, "generated", "result.txt")));
        Assert.True(File.Exists(outputPath));
        Assert.True(File.Exists(CreateDebugLogPath(workspace, "reports")));
        Assert.True(File.Exists(CreateTranscriptPath(workspace, "reports")));
        Assert.False(File.Exists(CreateOutputPath(workspace)));
        Assert.False(File.Exists(Path.Combine(inputDirectory, "generated", "result.txt")));
    }

    [Fact]
    public async Task RunAsyncAcceptsInlineMarkdownInput()
    {
        var workspace = CreateWorkspace();
        var outputPath = CreateOutputPath(workspace);
        var inlineInput =
            """
            # Inline Prompt

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "generated/inline.txt",
                  "content": "inline prompt completed\n"
                }
              }
            ]
            ```
            """;

        var runner = CreateRunner(new FakeSandboxRunner());
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inlineInput,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            "inline prompt completed\n",
            await File.ReadAllTextAsync(Path.Combine(workspace, "generated", "inline.txt")));
        Assert.Contains("inline markdown", await File.ReadAllTextAsync(CreateDebugLogPath(workspace)));
    }

    [Fact]
    public async Task RunAsyncTreatsMissingMarkdownInputPathAsMissingFile()
    {
        var workspace = CreateWorkspace();
        var outputPath = CreateOutputPath(workspace);
        var missingInputPath = Path.Combine(workspace, "missing.md");

        var runner = CreateRunner(new FakeSandboxRunner());

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => runner.RunAsync(
                new AgentRunRequest
                {
                    WorkspacePath = workspace,
                    InputPath = missingInputPath,
                    OutputPath = outputPath,
                    Mode = AgentMode.Local
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncRejectsExistingNonMarkdownInputFile()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.txt");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task");

        var runner = CreateRunner(new FakeSandboxRunner());

        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.RunAsync(
                new AgentRunRequest
                {
                    WorkspacePath = workspace,
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    Mode = AgentMode.Local
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncCreatesWorkspaceOutputDirectory()
    {
        var root = CreateWorkspace();
        var workspace = Path.Combine(root, "code");
        Directory.CreateDirectory(workspace);
        var inputPath = Path.Combine(root, "INPUT.md");
        var outputPath = "nested/reports/OUTPUT.md";
        var resolvedOutputPath = CreateOutputPath(workspace, "nested", "reports", "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nNo actions.");

        var runner = CreateRunner(new FakeSandboxRunner());
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(resolvedOutputPath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(resolvedOutputPath)!, "agent-debug.jsonl")));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(resolvedOutputPath)!, "agent-transcript.md")));
    }

    [Fact]
    public async Task RunAsyncRejectsExternalOutput()
    {
        var root = CreateWorkspace();
        var workspace = Path.Combine(root, "code");
        var reports = Path.Combine(root, "reports");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(reports);
        var inputPath = Path.Combine(root, "INPUT.md");
        var outputPath = Path.Combine(reports, "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nNo actions.");

        var runner = CreateRunner(new FakeSandboxRunner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => runner.RunAsync(
                new AgentRunRequest
                {
                    WorkspacePath = workspace,
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    Mode = AgentMode.Local
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncRejectsWorkspaceOutputOutsideArtifacts()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nNo actions.");

        var runner = CreateRunner(new FakeSandboxRunner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => runner.RunAsync(
                new AgentRunRequest
                {
                    WorkspacePath = workspace,
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    Mode = AgentMode.Local
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncWorkspaceOutputDoesNotExpandSandboxCommandMounts()
    {
        var root = CreateWorkspace();
        var workspace = Path.Combine(root, "code");
        Directory.CreateDirectory(workspace);
        var inputPath = Path.Combine(root, "INPUT.md");
        var outputPath = "reports/OUTPUT.md";
        await File.WriteAllTextAsync(
            inputPath,
            """
            # Workspace Output Command

            ```bubo-actions
            [
              {
                "tool": "run_command",
                "arguments": {
                  "executable": "dotnet",
                  "arguments": ["--version"]
                }
              }
            ]
            ```
            """);

        var sandboxRunner = new FakeSandboxRunner();
        var runner = CreateRunner(sandboxRunner);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        var options = Assert.Single(sandboxRunner.Calls).Options;
        Assert.Equal(workspace, options.WorkspacePath);
        Assert.Equal(workspace, options.InputPath);
        Assert.Equal(workspace, options.OutputPath);
        Assert.Equal(workspace, options.CachePath);
    }

    [Fact]
    public async Task RunAsyncRejectsOutputInsideWorkspaceGitMetadata()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, ".git", "OUTPUT.md");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        await File.WriteAllTextAsync(inputPath, "# Task\n\nNo actions.");

        var runner = CreateRunner(new FakeSandboxRunner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => runner.RunAsync(
                new AgentRunRequest
                {
                    WorkspacePath = workspace,
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    Mode = AgentMode.Local
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncRejectsExternalInputSymlink()
    {
        var root = CreateWorkspace();
        var workspace = Path.Combine(root, "code");
        Directory.CreateDirectory(workspace);
        var target = Path.Combine(root, "target.md");
        var link = Path.Combine(root, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(target, "# Linked input");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var runner = CreateRunner(new FakeSandboxRunner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => runner.RunAsync(
                new AgentRunRequest
                {
                    WorkspacePath = workspace,
                    InputPath = link,
                    OutputPath = outputPath,
                    Mode = AgentMode.Local
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncRejectsWorkspaceOutputParentSymlink()
    {
        var root = CreateWorkspace();
        var workspace = Path.Combine(root, "code");
        var realReports = Path.Combine(root, "real-reports");
        var artifactRoot = Path.Combine(workspace, ".ai", "artifacts");
        var linkReports = Path.Combine(artifactRoot, "reports-link");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(artifactRoot);
        Directory.CreateDirectory(realReports);
        var inputPath = Path.Combine(root, "INPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nNo actions.");

        try
        {
            Directory.CreateSymbolicLink(linkReports, realReports);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var runner = CreateRunner(new FakeSandboxRunner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => runner.RunAsync(
                new AgentRunRequest
                {
                    WorkspacePath = workspace,
                    InputPath = inputPath,
                    OutputPath = Path.Combine(linkReports, "OUTPUT.md"),
                    Mode = AgentMode.Local
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncPassesCodeFolderToOpenCawWhenInputIsExternalAndOutputIsWorkspace()
    {
        var root = CreateWorkspace();
        var workspace = Path.Combine(root, "code");
        Directory.CreateDirectory(workspace);
        var inputPath = Path.Combine(root, "INPUT.md");
        var outputPath = CreateOutputPath(workspace, "reports", "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nNo actions.");
        var bootstrapper = new FakeOpenCawBootstrapper(new OpenCawBootstrapResult());

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            config: new AgentRunConfig
            {
                OpenCaw = new OpenCawOptions()
            },
            openCawBootstrapper: bootstrapper);

        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(bootstrapper.WasCalled);
        Assert.Equal(workspace, bootstrapper.LastGuard?.WorkspaceRoot);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task RunAsyncExecutesBuboActions()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(
            inputPath,
            """
            # Task

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "notes/result.txt",
                  "content": "Hello from Bubo\n"
                }
              },
              {
                "tool": "run_command",
                "arguments": {
                  "executable": "dotnet",
                  "arguments": ["--version"]
                }
              }
            ]
            ```
            """);

        var runner = CreateRunner(new FakeSandboxRunner());
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("notes/result.txt", result.FilesChanged);
        Assert.Contains("dotnet --version", result.CommandsRun);
        Assert.Equal("Hello from Bubo\n", await File.ReadAllTextAsync(Path.Combine(workspace, "notes", "result.txt")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Bubo executed 2 action", output);
        Assert.Contains("dotnet --version", output);
    }

    [Fact]
    public async Task RunAsyncUsesInferenceProviderWhenNoExplicitActionsExist()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "generated/inferred.txt",
                  "content": "Inferred action completed.\n"
                }
              }
            ]
            ```
            """);

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(provider.WasCalled);
        Assert.Equal("coder", provider.LastRequest.Role);
        Assert.Contains("generated/inferred.txt", result.FilesChanged);
        Assert.Contains("write_file", provider.LastPrompt);
        Assert.Equal(
            "Inferred action completed.\n",
            await File.ReadAllTextAsync(Path.Combine(workspace, "generated", "inferred.txt")));

        var transcript = await File.ReadAllTextAsync(CreateTranscriptPath(workspace));
        Assert.Contains("inference.started", transcript);
        Assert.Contains("inference.completed", transcript);
    }

    [Fact]
    public async Task RunAsyncPassesConfiguredCoderProfileToInferenceProvider()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            []
            ```
            """);
        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                Coder = new ModelProfile
                {
                    Role = "custom-coder",
                    Path = "/models/custom-coder.gguf",
                    MaxTokens = 1234
                }
            });

        await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(provider.WasCalled);
        Assert.Equal("custom-coder", provider.LastRequest.Role);
        Assert.Equal("/models/custom-coder.gguf", provider.LastRequest.ModelProfile.Path);
        Assert.Equal(1_234, provider.LastRequest.ModelProfile.MaxTokens);
    }

    [Fact]
    public async Task RunAsyncDoesNotCallInferenceProviderWhenExplicitActionsExist()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(
            inputPath,
            """
            # Task

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "notes/result.txt",
                  "content": "Explicit action wins.\n"
                }
              }
            ]
            ```
            """);
        var provider = new FakeInferenceProvider("not used");

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(provider.WasCalled);
        Assert.Equal("Explicit action wins.\n", await File.ReadAllTextAsync(Path.Combine(workspace, "notes", "result.txt")));
    }

    [Fact]
    public async Task RunAsyncReportsInvalidInferenceActionsWithoutRunningTools()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            {
              "tool": "write_file"
            }
            ```
            """);

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(result.ChangesMade, change => change.Contains("Wrote", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, provider.CallCount);

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("could not parse model-proposed actions", output);
        Assert.Contains("bubo-actions fence must contain a JSON array", output);
    }

    [Fact]
    public async Task RunAsyncDoesNotRetryInvalidInferenceActions()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");
        var provider = new FakeInferenceProvider(new[]
        {
            """
            ```bubo-actions
            {
              "tool": "write_file"
            }
            ```
            """,
            """
            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "should-not-write.txt",
                  "content": "nope"
                }
              }
            ]
            ```
            """
        });

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                Limits = new AgentLimits { MaxIterations = 2 }
            });
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, provider.CallCount);
        Assert.False(File.Exists(Path.Combine(workspace, "should-not-write.txt")));
    }

    [Fact]
    public async Task RunAsyncReportsInferenceProviderFailureWithoutRunningTools()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");
        var provider = new FakeInferenceProvider("provider failed", success: false);

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(result.FilesChanged);

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("could not get model-proposed actions", output);
        Assert.Contains("inference provider `fake-inference` failed", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsyncRetriesInferenceGeneratedActionsAfterToolFailure()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        var targetPath = Path.Combine(workspace, "notes.txt");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nPatch the note.");
        await File.WriteAllTextAsync(targetPath, "hello");
        var provider = new FakeInferenceProvider(new[]
        {
            """
            ```bubo-actions
            [
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "notes.txt",
                  "old": "missing",
                  "new": "ignored"
                }
              }
            ]
            ```
            """,
            """
            ```bubo-actions
            [
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "notes.txt",
                  "old": "hello",
                  "new": "hi"
                }
              }
            ]
            ```
            """
        });

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                Limits = new AgentLimits { MaxIterations = 2 }
            });
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, provider.CallCount);
        Assert.Contains("Previous attempt observations", provider.Prompts[1]);
        Assert.Contains("patch_file failed", provider.Prompts[1]);
        Assert.Equal("hi", await File.ReadAllTextAsync(targetPath));

        var transcript = await File.ReadAllTextAsync(CreateTranscriptPath(workspace));
        Assert.Contains("inference.retry_planned", transcript);
        Assert.Contains("iteration 2", transcript);

        var debugLog = await File.ReadAllTextAsync(CreateDebugLogPath(workspace));
        Assert.Contains("\"type\":\"inference.iteration_started\"", debugLog);
        Assert.Contains("\"maxIterations\":\"2\"", debugLog);
        Assert.Contains("\"type\":\"inference.retry_planned\"", debugLog);
        Assert.Contains("\"observation\"", debugLog);
    }

    [Fact]
    public async Task RunAsyncStopsAfterInferenceIterationLimit()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        var targetPath = Path.Combine(workspace, "notes.txt");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nPatch the note.");
        await File.WriteAllTextAsync(targetPath, "hello");
        var provider = new FakeInferenceProvider(new[]
        {
            """
            ```bubo-actions
            [
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "notes.txt",
                  "old": "missing",
                  "new": "ignored"
                }
              }
            ]
            ```
            """,
            """
            ```bubo-actions
            [
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "notes.txt",
                  "old": "still missing",
                  "new": "ignored"
                }
              }
            ]
            ```
            """
        });

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                Limits = new AgentLimits { MaxIterations = 2 }
            });
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal("hello", await File.ReadAllTextAsync(targetPath));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("failed inference-generated action iteration", output);
        Assert.Contains("Reached maxIterations (2)", output);

        var transcript = await File.ReadAllTextAsync(CreateTranscriptPath(workspace));
        Assert.Contains("inference.max_iterations_reached", transcript);
    }

    [Fact]
    public async Task RunAsyncReportsPriorSideEffectsWhenInferenceIterationLimitIsReached()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        var targetPath = Path.Combine(workspace, "notes.txt");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite and patch the note.");
        await File.WriteAllTextAsync(targetPath, "hello");
        var provider = new FakeInferenceProvider(new[]
        {
            """
            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "notes.txt",
                  "content": "partial"
                }
              },
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "notes.txt",
                  "old": "missing",
                  "new": "ignored"
                }
              }
            ]
            ```
            """,
            """
            ```bubo-actions
            [
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "notes.txt",
                  "old": "still missing",
                  "new": "ignored"
                }
              }
            ]
            ```
            """
        });

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                Limits = new AgentLimits { MaxIterations = 2 }
            });
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal("partial", await File.ReadAllTextAsync(targetPath));
        Assert.Contains("notes.txt", result.FilesChanged);
        Assert.Contains(result.ChangesMade, change => change.Contains("Wrote `notes.txt`", StringComparison.Ordinal));
        Assert.Contains(result.IssuesOrRisks, issue => issue.Contains("Earlier inference iteration issue", StringComparison.Ordinal));
        Assert.Contains(result.IssuesOrRisks, issue => issue.Contains("Reached maxIterations (2)", StringComparison.Ordinal));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("notes.txt", output);
        Assert.Contains("Wrote `notes.txt`", output);
        Assert.Contains("Reached maxIterations (2)", output);
    }

    [Fact]
    public async Task RunAsyncReportsFailureWhenRetryReturnsNoActionsAfterFailure()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        var targetPath = Path.Combine(workspace, "notes.txt");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nPatch the note.");
        await File.WriteAllTextAsync(targetPath, "hello");
        var provider = new FakeInferenceProvider(new[]
        {
            """
            ```bubo-actions
            [
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "notes.txt",
                  "old": "missing",
                  "new": "ignored"
                }
              }
            ]
            ```
            """,
            """
            No action fence here.
            """
        });

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                Limits = new AgentLimits { MaxIterations = 2 }
            });
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal("hello", await File.ReadAllTextAsync(targetPath));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("returned no actions following a failed iteration", output);
        Assert.Contains("Earlier inference iteration issue", output);
    }

    [Fact]
    public async Task RunAsyncReportsInferenceExceptionWithoutSkippingArtifacts()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: new ThrowingInferenceProvider());
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(outputPath));
        Assert.True(File.Exists(CreateDebugLogPath(workspace)));
        Assert.Contains("provider exploded", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task RunAsyncReportsNoActionsFromInferenceProvider()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: new FakeInferenceProvider("No action fence here."));
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.FilesChanged);

        var transcript = await File.ReadAllTextAsync(CreateTranscriptPath(workspace));
        Assert.Contains("inference.no_actions", transcript);
    }

    [Fact]
    public async Task RunAsyncRejectsInferenceActionPlanAboveToolLimit()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite notes.");
        var provider = new FakeInferenceProvider(
            """
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

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                Limits = new AgentLimits { MaxToolCalls = 1 }
            });
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, provider.CallCount);
        Assert.False(File.Exists(Path.Combine(workspace, "one.txt")));
        Assert.Contains("exceeds maxToolCalls", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task RunAsyncNamesInferenceSourceForUnknownTool()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nDo something unsupported.");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            [
              {
                "tool": "unknown_tool",
                "arguments": {}
              }
            ]
            ```
            """);

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, provider.CallCount);
        Assert.Contains("inference-generated bubo-actions block", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task RunAsyncRejectsRunCommandFromInferenceActions()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nRun a command.");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            [
              {
                "tool": "run_command",
                "arguments": {
                  "executable": "git",
                  "arguments": ["reset", "--hard"]
                }
              }
            ]
            ```
            """);

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, provider.CallCount);

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Unknown tool requested by inference-generated bubo-actions block: run_command", output);
    }

    [Fact]
    public async Task RunAsyncEnforcesCumulativeInferenceToolCallLimit()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite generated notes.");
        var provider = new FakeInferenceProvider(new[]
        {
            """
            ```bubo-actions
            [
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "missing.txt",
                  "old": "x",
                  "new": "y"
                }
              }
            ]
            ```
            """,
            """
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
            """
        });

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                Limits = new AgentLimits
                {
                    MaxIterations = 2,
                    MaxToolCalls = 2
                }
            });
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, provider.CallCount);
        Assert.False(File.Exists(Path.Combine(workspace, "one.txt")));
        Assert.False(File.Exists(Path.Combine(workspace, "two.txt")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("cumulative action count 3 exceeds maxToolCalls (2)", output);
    }

    [Fact]
    public async Task RunAsyncOverridesModelSuppliedPatchLimits()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        var targetPath = Path.Combine(workspace, "notes.txt");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nPatch the note.");
        await File.WriteAllTextAsync(targetPath, "abcdef");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            [
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "notes.txt",
                  "old": "abcdef",
                  "new": "uvwxyz",
                  "maxPatchBytes": "1000000"
                }
              }
            ]
            ```
            """);

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                Limits = new AgentLimits { MaxPatchBytes = 4 }
            });
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("abcdef", await File.ReadAllTextAsync(targetPath));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("maxPatchBytes (4)", output);
    }

    [Fact]
    public async Task RunAsyncRejectsMultipleInferenceActionFences()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a note.");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            []
            ```
            ```bubo-actions
            []
            ```
            """);

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Cloud
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("at most one bubo-actions fence", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task RunAsyncTimesOutSlowToolCalls()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(
            inputPath,
            """
            # Task

            ```bubo-actions
            [
              {
                "tool": "run_command",
                "arguments": {
                  "executable": "dotnet",
                  "arguments": ["--version"]
                }
              }
            ]
            ```
            """);

        var runner = CreateRunner(
            new SlowSandboxRunner(),
            config: new AgentRunConfig
            {
                Limits = new AgentLimits { MaxCommandSeconds = 1 }
            });
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("timed out after 1 second", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task RunAsyncDeniesActionPathTraversal()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(
            inputPath,
            """
            # Task

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

        var runner = CreateRunner();
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(workspace)!.FullName, "escape.txt")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Bubo stopped", output);
        Assert.Contains("write_file failed", output);
    }

    [Fact]
    public async Task RunAsyncReportsPatchFileChanges()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "notes.txt"), "old value");
        await File.WriteAllTextAsync(
            inputPath,
            """
            # Task

            ```bubo-actions
            [
              {
                "tool": "patch_file",
                "arguments": {
                  "path": "notes.txt",
                  "old": "old",
                  "new": "new"
                }
              }
            ]
            ```
            """);

        var runner = CreateRunner(new FakeSandboxRunner());
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("notes.txt", result.FilesChanged);
        Assert.Contains("Changed `notes.txt`.", result.ChangesMade);
    }

    [Fact]
    public async Task RunAsyncPassesOpenCawSystemPromptToInferenceProvider()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        await File.WriteAllTextAsync(inputPath, "# Task\n\nUse the project baseline.");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            []
            ```
            """);
        var bootstrapper = new FakeOpenCawBootstrapper(
            new OpenCawBootstrapResult
            {
                SystemPrompt = "OpenCaw baseline plus host .ai memory.",
                Events = new[]
                {
                    new TranscriptEvent
                    {
                        Type = "opencaw.context_loaded",
                        Message = "Loaded fake OpenCaw context."
                    }
                }
            });

        var runner = CreateRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                OpenCaw = new OpenCawOptions()
            },
            openCawBootstrapper: bootstrapper);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(bootstrapper.WasCalled);
        Assert.Equal("OpenCaw baseline plus host .ai memory.", provider.LastRequest.SystemPrompt);
        Assert.Contains("Use the project baseline.", provider.LastPrompt);
        Assert.Contains("opencaw.context_loaded", await File.ReadAllTextAsync(CreateTranscriptPath(workspace)));
    }

    [Fact]
    public async Task RunAsyncStopsOnOpenCawFailureBeforeReadingInput()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = CreateOutputPath(workspace);
        var bootstrapper = new FakeOpenCawBootstrapper(
            new OpenCawBootstrapResult
            {
                Success = false,
                Error = "OpenCaw checkout is unavailable.",
                Events = new[]
                {
                    new TranscriptEvent
                    {
                        Type = "opencaw.failed",
                        Message = "OpenCaw checkout is unavailable."
                    }
                }
            });

        var runner = CreateRunner(
            config: new AgentRunConfig
            {
                OpenCaw = new OpenCawOptions()
            },
            openCawBootstrapper: bootstrapper);
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(outputPath));
        Assert.Contains("could not initialize the OpenCaw session", await File.ReadAllTextAsync(outputPath));
        Assert.Contains("OpenCaw checkout is unavailable.", await File.ReadAllTextAsync(CreateDebugLogPath(workspace)));
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "bubo-runner-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return Path.GetFullPath(workspace);
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

    private sealed class FakeSandboxRunner : ISandboxRunner
    {
        public List<SandboxCall> Calls { get; } = new();

        public Task<ToolResult> RunCommandAsync(
            string command,
            IReadOnlyList<string> arguments,
            SandboxOptions options,
            CancellationToken cancellationToken)
        {
            Calls.Add(new SandboxCall(command, arguments, options));
            return Task.FromResult(new ToolResult
            {
                Success = true,
                ExitCode = 0,
                Output = $"{command} {string.Join(" ", arguments)}"
            });
        }
    }

    private sealed record SandboxCall(
        string Command,
        IReadOnlyList<string> Arguments,
        SandboxOptions Options);

    private sealed class SlowSandboxRunner : ISandboxRunner
    {
        public async Task<ToolResult> RunCommandAsync(
            string command,
            IReadOnlyList<string> arguments,
            SandboxOptions options,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            return new ToolResult { Success = true };
        }
    }

    private static AgentRunner CreateRunner(
        ISandboxRunner? sandboxRunner = null,
        SandboxOptions? sandboxOptions = null,
        IInferenceProvider? inferenceProvider = null,
        AgentRunConfig? config = null,
        IOpenCawBootstrapper? openCawBootstrapper = null)
    {
        return new AgentRunner(
            sandboxRunner,
            sandboxOptions,
            inferenceProvider,
            config,
            openCawBootstrapper ?? new FakeOpenCawBootstrapper(new OpenCawBootstrapResult()));
    }

    private sealed class FakeOpenCawBootstrapper : IOpenCawBootstrapper
    {
        private readonly OpenCawBootstrapResult _result;

        public FakeOpenCawBootstrapper(OpenCawBootstrapResult result)
        {
            _result = result;
        }

        public bool WasCalled { get; private set; }

        public WorkspaceGuard? LastGuard { get; private set; }

        public OpenCawOptions? LastOptions { get; private set; }

        public Task<OpenCawBootstrapResult> BootstrapAsync(
            WorkspaceGuard guard,
            OpenCawOptions options,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastGuard = guard;
            LastOptions = options;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeInferenceProvider : IInferenceProvider
    {
        private readonly Queue<string> _responses;
        private readonly bool _success;

        public FakeInferenceProvider(string response, bool success = true)
            : this(new[] { response }, success)
        {
        }

        public FakeInferenceProvider(IEnumerable<string> responses, bool success = true)
        {
            _responses = new Queue<string>(responses);
            if (_responses.Count == 0)
            {
                throw new ArgumentException("At least one fake inference response is required.", nameof(responses));
            }

            _success = success;
        }

        public string Name => "fake-inference";

        public bool WasCalled { get; private set; }

        public int CallCount { get; private set; }

        public string LastPrompt { get; private set; } = string.Empty;

        public List<string> Prompts { get; } = new();

        public InferenceRequest LastRequest { get; private set; } = new()
        {
            Role = string.Empty,
            Prompt = string.Empty,
            ModelProfile = new ModelProfile()
        };

        public Task<InferenceResponse> GenerateAsync(
            InferenceRequest request,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            CallCount++;
            LastPrompt = request.Prompt;
            Prompts.Add(request.Prompt);
            LastRequest = request;
            var response = _responses.Count > 1
                ? _responses.Dequeue()
                : _responses.Peek();
            return Task.FromResult(new InferenceResponse
            {
                Success = _success,
                Text = response,
                Events = new[]
                {
                    new TranscriptEvent
                    {
                        Type = "fake-inference.completed",
                        Message = "Fake inference completed."
                    }
                }
            });
        }
    }

    private sealed class ThrowingInferenceProvider : IInferenceProvider
    {
        public string Name => "throwing-inference";

        public Task<InferenceResponse> GenerateAsync(
            InferenceRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("provider exploded");
        }
    }
}
