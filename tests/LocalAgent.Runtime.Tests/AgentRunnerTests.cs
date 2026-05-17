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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nSay hello.");

        var runner = new AgentRunner(new FakeSandboxRunner());
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
        Assert.True(File.Exists(Path.Combine(workspace, "agent-debug.jsonl")));
        Assert.True(File.Exists(Path.Combine(workspace, "agent-transcript.md")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("# Result", output);
        Assert.Contains("## Summary", output);
        Assert.Contains("## Next Steps", output);
    }

    [Fact]
    public async Task RunAsyncExecutesBuboActions()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(new FakeSandboxRunner());
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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

        var transcript = await File.ReadAllTextAsync(Path.Combine(workspace, "agent-transcript.md"));
        Assert.Contains("inference.started", transcript);
        Assert.Contains("inference.completed", transcript);
    }

    [Fact]
    public async Task RunAsyncPassesConfiguredCoderProfileToInferenceProvider()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            []
            ```
            """);
        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");
        var provider = new FakeInferenceProvider(
            """
            ```bubo-actions
            {
              "tool": "write_file"
            }
            ```
            """);

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");
        var provider = new FakeInferenceProvider("provider failed", success: false);

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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

        var transcript = await File.ReadAllTextAsync(Path.Combine(workspace, "agent-transcript.md"));
        Assert.Contains("inference.retry_planned", transcript);
        Assert.Contains("iteration 2", transcript);

        var debugLog = await File.ReadAllTextAsync(Path.Combine(workspace, "agent-debug.jsonl"));
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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

        var transcript = await File.ReadAllTextAsync(Path.Combine(workspace, "agent-transcript.md"));
        Assert.Contains("inference.max_iterations_reached", transcript);
    }

    [Fact]
    public async Task RunAsyncReportsPriorSideEffectsWhenInferenceIterationLimitIsReached()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");

        var runner = new AgentRunner(
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
        Assert.True(File.Exists(Path.Combine(workspace, "agent-debug.jsonl")));
        Assert.Contains("provider exploded", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task RunAsyncReportsNoActionsFromInferenceProvider()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nWrite a generated note.");

        var runner = new AgentRunner(
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

        var transcript = await File.ReadAllTextAsync(Path.Combine(workspace, "agent-transcript.md"));
        Assert.Contains("inference.no_actions", transcript);
    }

    [Fact]
    public async Task RunAsyncRejectsInferenceActionPlanAboveToolLimit()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner();
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(new FakeSandboxRunner());
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
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
            new FakeSandboxRunner(),
            inferenceProvider: provider,
            config: new AgentRunConfig
            {
                OpenCaw = new OpenCawOptions { Enabled = true }
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
        Assert.Contains("opencaw.context_loaded", await File.ReadAllTextAsync(Path.Combine(workspace, "agent-transcript.md")));
    }

    [Fact]
    public async Task RunAsyncStopsOnOpenCawFailureBeforeReadingInput()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
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

        var runner = new AgentRunner(
            config: new AgentRunConfig
            {
                OpenCaw = new OpenCawOptions { Enabled = true }
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
        Assert.Contains("OpenCaw checkout is unavailable.", await File.ReadAllTextAsync(Path.Combine(workspace, "agent-debug.jsonl")));
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

    private sealed class FakeSandboxRunner : ISandboxRunner
    {
        public Task<ToolResult> RunCommandAsync(
            string command,
            IReadOnlyList<string> arguments,
            SandboxOptions options,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolResult
            {
                Success = true,
                ExitCode = 0,
                Output = $"{command} {string.Join(" ", arguments)}"
            });
        }
    }

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
