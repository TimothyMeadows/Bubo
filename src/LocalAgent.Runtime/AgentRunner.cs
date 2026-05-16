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

    public AgentRunner(
        ISandboxRunner? sandboxRunner = null,
        SandboxOptions? sandboxOptions = null)
    {
        _sandboxRunner = sandboxRunner;
        _sandboxOptions = sandboxOptions ?? new SandboxOptions { Gpu = null, ModelsPath = null };
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
            ? CreateNoOpResult(events)
            : await ExecuteActionsAsync(actions, guard, events, cancellationToken);

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

    private async Task<AgentRunResult> ExecuteActionsAsync(
        IReadOnlyList<AgentInputAction> actions,
        WorkspaceGuard guard,
        ICollection<TranscriptEvent> events,
        CancellationToken cancellationToken)
    {
        var registry = ToolRegistry.CreateDefault(_sandboxRunner, _sandboxOptions);
        var filesChanged = new List<string>();
        var commandsRun = new List<string>();
        var changesMade = new List<string>();
        var testResults = new List<string>();
        var issues = new List<string>();

        events.Add(new TranscriptEvent
        {
            Type = "plan.created",
            Message = $"Parsed {actions.Count} bubo-actions item(s) from INPUT.md.",
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
                var message = $"Unknown tool requested by INPUT.md: {action.Tool}";
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

            var toolResult = await tool.InvokeAsync(
                new ToolRequest
                {
                    Name = action.Tool,
                    WorkspaceRoot = guard.WorkspaceRoot,
                    Arguments = action.Arguments
                },
                cancellationToken);

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
                ? $"Bubo executed {actions.Count} action(s) from INPUT.md."
                : "Bubo stopped after a requested action failed.",
            Plan = new[]
            {
                "Read INPUT.md.",
                "Execute the explicit bubo-actions block with guarded tools.",
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
                ? new[] { "Directive-based execution is deterministic; model-driven planning remains future work." }
                : issues,
            NextSteps = new[]
            {
                "Review OUTPUT.md and git diff before committing generated workspace changes."
            }
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
        if (string.Equals(action.Tool, "write_file", StringComparison.OrdinalIgnoreCase) &&
            result.Success &&
            !string.IsNullOrWhiteSpace(result.Output))
        {
            filesChanged.Add(result.Output.Trim());
            changesMade.Add($"Wrote `{result.Output.Trim()}`.");
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
