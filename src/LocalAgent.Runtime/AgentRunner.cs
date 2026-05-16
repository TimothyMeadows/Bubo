using System.Text;
using System.Text.Json;
using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime;

public sealed class AgentRunner
{
    private static readonly JsonSerializerOptions DebugJsonOptions = new(JsonSerializerDefaults.Web);

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
        var events = new List<TranscriptEvent>
        {
            new()
            {
                Type = "run.started",
                Message = "Bubo no-op foundation run started.",
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
            },
            new()
            {
                Type = "run.completed",
                Message = "No-op foundation run completed without modifying files."
            }
        };

        var result = new AgentRunResult
        {
            Success = true,
            Summary = "Bubo foundation runner executed successfully. No changes were made.",
            FilesChanged = Array.Empty<string>(),
            CommandsRun = Array.Empty<string>(),
            IssuesOrRisks = new[]
            {
                "This foundation slice does not invoke Docker, llama.cpp, codex-cli, or git tools yet."
            }
        };

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
        builder.AppendLine("No-op foundation slice: validate input/output plumbing before agent execution is added.");
        builder.AppendLine();
        builder.AppendLine("## Changes Made");
        builder.AppendLine();
        builder.AppendLine("No changes made.");
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
        builder.AppendLine("Not run by the no-op foundation slice.");
        builder.AppendLine();
        builder.AppendLine("## Issues / Risks");
        builder.AppendLine();
        AppendListOrNone(builder, result.IssuesOrRisks);
        builder.AppendLine();
        builder.AppendLine("## Next Steps");
        builder.AppendLine();
        builder.AppendLine("- Implement Docker sandbox execution in the next goal task.");
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
}
