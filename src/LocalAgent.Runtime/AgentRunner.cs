using System.Text;
using System.Text.Json;
using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Runtime.Tools;

namespace Bubo.LocalAgent.Runtime;

public sealed class AgentRunner
{
    private static readonly JsonSerializerOptions DebugJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISandboxRunner? _sandboxRunner;
    private readonly SandboxOptions _sandboxOptions;
    private readonly IInferenceProvider? _inferenceProvider;
    private readonly AgentRunConfig _config;

    public AgentRunner(
        ISandboxRunner? sandboxRunner = null,
        SandboxOptions? sandboxOptions = null,
        IInferenceProvider? inferenceProvider = null,
        AgentRunConfig? config = null)
    {
        _sandboxRunner = sandboxRunner;
        _sandboxOptions = sandboxOptions ?? new SandboxOptions { Gpu = null, ModelsPath = null };
        _inferenceProvider = inferenceProvider;
        _config = config ?? new AgentRunConfig();
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var guard = new WorkspaceGuard(request.WorkspacePath);
        var inputPath = guard.ResolveInsideWorkspace(request.InputPath);
        var outputPath = guard.ResolveInsideWorkspace(request.OutputPath);

        if (!Directory.Exists(guard.WorkspaceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Workspace does not exist: {guard.WorkspaceRoot}");
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file does not exist.", inputPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? guard.WorkspaceRoot);

        var input = await File.ReadAllTextAsync(inputPath, cancellationToken);
        var actions = AgentInputActionParser.Parse(input);
        var events = new List<TranscriptEvent>
        {
            new()
            {
                Type = "run.started",
                Message = "Bubo run started.",
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mode"] = request.Mode.ToString(),
                    ["workspace"] = guard.WorkspaceRoot
                }
            },
            new()
            {
                Type = "input.read",
                Message = "Read task input.",
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["path"] = Path.GetRelativePath(guard.WorkspaceRoot, inputPath),
                    ["characters"] = input.Length.ToString()
                }
            }
        };

        var result = actions.Count == 0
            ? await PlanWithInferenceOrNoOpAsync(input, guard, events, cancellationToken)
            : await ExecuteActionsAsync(
                actions,
                guard,
                ToolRegistry.CreateDefault(_sandboxRunner, _sandboxOptions),
                events,
                "explicit bubo-actions block",
                cancellationToken);

        events.Add(new TranscriptEvent
        {
            Type = "run.completed",
            Message = result.Success
                    ? "Bubo run completed successfully."
                    : "Bubo run completed with failures."
        });

        await File.WriteAllTextAsync(
            outputPath,
            BuildOutputMarkdown(result),
            cancellationToken);

        var outputDirectory = Path.GetDirectoryName(outputPath) ?? guard.WorkspaceRoot;
        await WriteDebugLogAsync(
            Path.Combine(outputDirectory, "agent-debug.jsonl"),
            events,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "agent-transcript.md"),
            BuildTranscriptMarkdown(events),
            cancellationToken);

        return result;
    }

    private static AgentRunResult CreateNoOpResult(ICollection<TranscriptEvent> events)
    {
        events.Add(new TranscriptEvent
        {
            Type = "plan.created",
            Message = "No bubo-actions fence was found in INPUT.md; no tool actions were run."
        });

        return new AgentRunResult
        {
            Success = true,
            Summary = "Bubo runner executed successfully. No actions were requested.",
            Plan = new[]
            {
                "Validate input/output plumbing.",
                "Skip tool execution because INPUT.md did not include a bubo-actions block."
            },
            ChangesMade = new[] { "No changes made." },
            FilesChanged = Array.Empty<string>(),
            CommandsRun = Array.Empty<string>(),
            TestResults = new[] { "No commands were run." },
            IssuesOrRisks = new[]
            {
                "Model-driven planning/coding is still behind the local/cloud inference provider integration."
            },
            NextSteps = new[]
            {
                "Add a bubo-actions fenced block or enable a planner/coder loop in a future runtime slice."
            }
        };
    }

    private async Task<AgentRunResult> PlanWithInferenceOrNoOpAsync(
        string input,
        WorkspaceGuard guard,
        ICollection<TranscriptEvent> events,
        CancellationToken cancellationToken)
    {
        if (_inferenceProvider is null)
        {
            return CreateNoOpResult(events);
        }

        var registry = ToolRegistry.CreateModelSafe(_sandboxRunner, _sandboxOptions);
        var prompt = InferenceActionPromptBuilder.Build(input, registry.Tools);
        events.Add(new TranscriptEvent
        {
            Type = "inference.started",
            Message = $"Requesting guarded actions from `{_inferenceProvider.Name}`.",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["provider"] = _inferenceProvider.Name,
                ["role"] = _config.Coder.Role,
                ["promptCharacters"] = prompt.Length.ToString()
            }
        });

        InferenceResponse response;
        try
        {
            response = await _inferenceProvider.GenerateAsync(
                new InferenceRequest
                {
                    Role = _config.Coder.Role,
                    Prompt = prompt,
                    ModelProfile = _config.Coder
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            events.Add(new TranscriptEvent
            {
                Type = "inference.failed",
                Message = exception.Message
            });

            return new AgentRunResult
            {
                Success = false,
                Summary = "Bubo could not get model-proposed actions.",
                Plan = new[]
                {
                    "Read INPUT.md.",
                    "Ask the configured inference provider for a fenced bubo-actions JSON array.",
                    "Stop before tool execution because inference threw an exception."
                },
                ChangesMade = new[] { "No changes made." },
                FilesChanged = Array.Empty<string>(),
                CommandsRun = Array.Empty<string>(),
                TestResults = new[] { "No commands were run." },
                IssuesOrRisks = new[] { exception.Message },
                NextSteps = new[]
                {
                    "Inspect agent-debug.jsonl for provider details, then fix the provider configuration or use a deterministic bubo-actions block."
                }
            };
        }

        foreach (var providerEvent in response.Events)
        {
            events.Add(providerEvent);
        }

        events.Add(new TranscriptEvent
        {
            Type = "inference.completed",
            Message = $"Inference provider `{_inferenceProvider.Name}` returned a response.",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["responseCharacters"] = response.Text.Length.ToString()
            }
        });

        if (!response.Success)
        {
            events.Add(new TranscriptEvent
            {
                Type = "inference.failed",
                Message = $"Inference provider `{_inferenceProvider.Name}` failed."
            });

            return new AgentRunResult
            {
                Success = false,
                Summary = "Bubo could not get model-proposed actions.",
                Plan = new[]
                {
                    "Read INPUT.md.",
                    "Ask the configured inference provider for a fenced bubo-actions JSON array.",
                    "Stop before tool execution because inference failed."
                },
                ChangesMade = new[] { "No changes made." },
                FilesChanged = Array.Empty<string>(),
                CommandsRun = Array.Empty<string>(),
                TestResults = new[] { "No commands were run." },
                IssuesOrRisks = new[]
                {
                    $"Inference provider `{_inferenceProvider.Name}` failed before returning guarded actions."
                },
                NextSteps = new[]
                {
                    "Inspect agent-debug.jsonl for provider details, then fix the provider configuration or use a deterministic bubo-actions block."
                }
            };
        }

        IReadOnlyList<AgentInputAction> generatedActions;
        try
        {
            generatedActions = AgentInputActionParser.ParseSingleFence(response.Text);
        }
        catch (ArgumentException exception)
        {
            events.Add(new TranscriptEvent
            {
                Type = "inference.parse_failed",
                Message = exception.Message
            });

            return new AgentRunResult
            {
                Success = false,
                Summary = "Bubo could not parse model-proposed actions.",
                Plan = new[]
                {
                    "Read INPUT.md.",
                    "Ask the configured inference provider for a fenced bubo-actions JSON array.",
                    "Stop before tool execution because the response was invalid."
                },
                ChangesMade = new[] { "No changes made." },
                FilesChanged = Array.Empty<string>(),
                CommandsRun = Array.Empty<string>(),
                TestResults = new[] { "No commands were run." },
                IssuesOrRisks = new[] { exception.Message },
                NextSteps = new[]
                {
                    "Inspect agent-debug.jsonl and retry with a response that contains valid fenced bubo-actions JSON."
                }
            };
        }

        if (generatedActions.Count == 0)
        {
            events.Add(new TranscriptEvent
            {
                Type = "inference.no_actions",
                Message = "Inference response did not include a bubo-actions fence."
            });

            return new AgentRunResult
            {
                Success = true,
                Summary = "Bubo completed without tool actions from the inference provider.",
                Plan = new[]
                {
                    "Read INPUT.md.",
                    "Ask the configured inference provider for guarded actions.",
                    "Skip tool execution because no fenced bubo-actions JSON was returned."
                },
                ChangesMade = new[] { "No changes made." },
                FilesChanged = Array.Empty<string>(),
                CommandsRun = Array.Empty<string>(),
                TestResults = new[] { "No commands were run." },
                IssuesOrRisks = new[]
                {
                    "Inference did not produce executable guarded actions."
                },
                NextSteps = new[]
                {
                    "Use a deterministic bubo-actions block or configure an inference provider that can return one."
                }
            };
        }

        return await ExecuteActionsAsync(
            generatedActions,
            guard,
            registry,
            events,
            "inference-generated bubo-actions block",
            cancellationToken);
    }

    private async Task<AgentRunResult> ExecuteActionsAsync(
        IReadOnlyList<AgentInputAction> actions,
        WorkspaceGuard guard,
        ToolRegistry registry,
        ICollection<TranscriptEvent> events,
        string actionSource,
        CancellationToken cancellationToken)
    {
        var filesChanged = new List<string>();
        var commandsRun = new List<string>();
        var changesMade = new List<string>();
        var testResults = new List<string>();
        var issues = new List<string>();

        if (actions.Count > _config.Limits.MaxToolCalls)
        {
            var message = $"Action count {actions.Count} exceeds maxToolCalls ({_config.Limits.MaxToolCalls}).";
            events.Add(new TranscriptEvent
            {
                Type = "plan.rejected",
                Message = message
            });

            return new AgentRunResult
            {
                Success = false,
                Summary = "Bubo rejected an oversized action plan.",
                Plan = new[]
                {
                    "Read INPUT.md.",
                    $"Reject the {actionSource} before tool execution because it exceeds configured limits."
                },
                ChangesMade = new[] { "No changes made." },
                FilesChanged = Array.Empty<string>(),
                CommandsRun = Array.Empty<string>(),
                TestResults = new[] { "No commands were run." },
                IssuesOrRisks = new[] { message },
                NextSteps = new[] { "Reduce the number of requested tool actions and rerun Bubo." }
            };
        }

        events.Add(new TranscriptEvent
        {
            Type = "plan.created",
            Message = $"Parsed {actions.Count} action(s) from {actionSource}.",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["actions"] = string.Join(", ", actions.Select(action => action.Tool))
            }
        });

        var success = true;
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            if (!registry.TryGet(action.Tool, out var tool))
            {
                success = false;
                var message = $"Unknown tool requested by {actionSource}: {action.Tool}";
                issues.Add(message);
                events.Add(new TranscriptEvent
                {
                    Type = "tool.failed",
                    Message = message
                });
                break;
            }

            events.Add(new TranscriptEvent
            {
                Type = "tool.started",
                Message = $"Running tool `{action.Tool}`.",
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["index"] = index.ToString(),
                    ["arguments"] = string.Join(", ", action.Arguments.Keys)
                }
            });

            ToolResult toolResult;
            using (var toolCallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (_config.Limits.MaxCommandSeconds > 0)
                {
                    toolCallCts.CancelAfter(TimeSpan.FromSeconds(_config.Limits.MaxCommandSeconds));
                }

                try
                {
                    toolResult = await tool.InvokeAsync(
                        CreateToolRequest(action, guard),
                        toolCallCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    toolResult = new ToolResult
                    {
                        Success = false,
                        Error = $"{action.Tool} timed out after {_config.Limits.MaxCommandSeconds} second(s)."
                    };
                }
            }

            RecordToolResult(
                action,
                toolResult,
                filesChanged,
                commandsRun,
                changesMade,
                testResults);

            events.Add(new TranscriptEvent
            {
                Type = toolResult.Success ? "tool.completed" : "tool.failed",
                Message = toolResult.Success
                    ? $"Tool `{action.Tool}` completed."
                    : $"Tool `{action.Tool}` failed.",
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exitCode"] = toolResult.ExitCode?.ToString() ?? string.Empty,
                    ["output"] = Truncate(toolResult.Output),
                    ["error"] = Truncate(toolResult.Error)
                }
            });

            if (!toolResult.Success)
            {
                success = false;
                issues.Add($"{action.Tool} failed: {toolResult.Error}".Trim());
                break;
            }
        }

        return new AgentRunResult
        {
            Success = success,
            Summary = success
                ? $"Bubo executed {actions.Count} action(s) from {actionSource}."
                : "Bubo stopped after a requested action failed.",
            Plan = new[]
            {
                "Read INPUT.md.",
                $"Execute the {actionSource} with guarded tools.",
                "Write auditable OUTPUT.md, agent-debug.jsonl, and agent-transcript.md."
            },
            ChangesMade = changesMade.Count == 0
                ? new[] { "No file changes were made." }
                : changesMade,
            FilesChanged = filesChanged,
            CommandsRun = commandsRun,
            TestResults = testResults.Count == 0
                ? new[] { "No command actions were run." }
                : testResults,
            IssuesOrRisks = issues.Count == 0
                ? new[] { "Tool execution stayed inside the guarded runtime path." }
                : issues,
            NextSteps = new[]
            {
                "Review OUTPUT.md and git diff before committing generated workspace changes."
            }
        };
    }

    private ToolRequest CreateToolRequest(AgentInputAction action, WorkspaceGuard guard)
    {
        var arguments = new Dictionary<string, string>(action.Arguments, StringComparer.Ordinal);
        if (string.Equals(action.Tool, "patch_file", StringComparison.OrdinalIgnoreCase))
        {
            arguments["maxPatchBytes"] = _config.Limits.MaxPatchBytes.ToString();
        }

        if (string.Equals(action.Tool, "git_apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            arguments["maxPatchBytes"] = _config.Limits.MaxPatchBytes.ToString();
            arguments["maxFilesChanged"] = _config.Limits.MaxFilesChanged.ToString();
        }

        return new ToolRequest
        {
            Name = action.Tool,
            WorkspaceRoot = guard.WorkspaceRoot,
            Arguments = arguments
        };
    }

    private static string BuildOutputMarkdown(AgentRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Result");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine(result.Summary);
        builder.AppendLine();
        builder.AppendLine("## Plan");
        builder.AppendLine();
        AppendListOrNone(builder, result.Plan);
        builder.AppendLine();
        builder.AppendLine("## Changes Made");
        builder.AppendLine();
        AppendListOrNone(builder, result.ChangesMade);
        builder.AppendLine();
        builder.AppendLine("## Files Changed");
        builder.AppendLine();
        AppendListOrNone(builder, result.FilesChanged);
        builder.AppendLine();
        builder.AppendLine("## Commands Run");
        builder.AppendLine();
        AppendListOrNone(builder, result.CommandsRun);
        builder.AppendLine();
        builder.AppendLine("## Test Results");
        builder.AppendLine();
        AppendListOrNone(builder, result.TestResults);
        builder.AppendLine();
        builder.AppendLine("## Issues / Risks");
        builder.AppendLine();
        AppendListOrNone(builder, result.IssuesOrRisks);
        builder.AppendLine();
        builder.AppendLine("## Next Steps");
        builder.AppendLine();
        AppendListOrNone(builder, result.NextSteps);
        return builder.ToString();
    }

    private static async Task WriteDebugLogAsync(
        string path,
        IEnumerable<TranscriptEvent> events,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        foreach (var transcriptEvent in events)
        {
            var json = JsonSerializer.Serialize(transcriptEvent, DebugJsonOptions);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
    }

    private static string BuildTranscriptMarkdown(IEnumerable<TranscriptEvent> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Agent Transcript");
        builder.AppendLine();
        builder.AppendLine("This transcript records observable events and does not include hidden chain-of-thought.");
        builder.AppendLine();

        foreach (var transcriptEvent in events)
        {
            builder.AppendLine($"## {transcriptEvent.Type}");
            builder.AppendLine();
            builder.AppendLine(transcriptEvent.Message);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendListOrNone(StringBuilder builder, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            builder.AppendLine("- None");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- {value}");
        }
    }

    private static void RecordToolResult(
        AgentInputAction action,
        ToolResult result,
        ICollection<string> filesChanged,
        ICollection<string> commandsRun,
        ICollection<string> changesMade,
        ICollection<string> testResults)
    {
        if ((string.Equals(action.Tool, "write_file", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(action.Tool, "patch_file", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(action.Tool, "git_apply_patch", StringComparison.OrdinalIgnoreCase)) &&
            result.Success &&
            !string.IsNullOrWhiteSpace(result.Output))
        {
            var changedFiles = SplitToolOutputLines(result.Output);
            foreach (var changedFile in changedFiles)
            {
                filesChanged.Add(changedFile);
            }

            var verb = string.Equals(action.Tool, "write_file", StringComparison.OrdinalIgnoreCase)
                ? "Wrote"
                : "Changed";
            changesMade.Add($"{verb} `{string.Join("`, `", changedFiles)}`.");
            return;
        }

        if (string.Equals(action.Tool, "run_command", StringComparison.OrdinalIgnoreCase))
        {
            var display = BuildCommandDisplay(action.Arguments);
            commandsRun.Add(display);
            testResults.Add(result.Success
                ? $"`{display}` exited with code {result.ExitCode ?? 0}."
                : $"`{display}` failed with code {result.ExitCode?.ToString() ?? "unknown"}.");
            return;
        }

        changesMade.Add(result.Success
            ? $"Ran `{action.Tool}` successfully."
            : $"`{action.Tool}` did not complete successfully.");
    }

    private static IReadOnlyList<string> SplitToolOutputLines(string output)
    {
        return output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static string BuildCommandDisplay(IReadOnlyDictionary<string, string> arguments)
    {
        var executable = arguments.TryGetValue("executable", out var executableValue)
            ? executableValue
            : arguments.TryGetValue("command", out var commandValue)
                ? commandValue
                : "unknown";
        if (!arguments.TryGetValue("arguments", out var rawArguments) ||
            string.IsNullOrWhiteSpace(rawArguments))
        {
            return executable;
        }

        var displayArguments = rawArguments
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(argument => argument.Length > 0);
        return string.Join(" ", new[] { executable }.Concat(displayArguments));
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 1_000
            ? value
            : string.Concat(value.AsSpan(0, 1_000), "...");
    }
}
