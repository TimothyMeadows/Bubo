namespace Bubo.LocalAgent.Abstractions;

public sealed record AgentRunConfig
{
    public AgentMode Mode { get; init; } = AgentMode.Local;

    public ModelProfile Planner { get; init; } = new()
    {
        Role = "planner",
        Family = "Qwen3 14B Instruct or equivalent GGUF",
        Path = "/models/planner.gguf",
        Temperature = 0.2,
        TopP = 0.9,
        MaxTokens = 4_096
    };

    public ModelProfile Coder { get; init; } = new()
    {
        Role = "coder",
        Family = "Qwen2.5-Coder 14B Instruct, Qwen3-Coder mid-size, or equivalent GGUF",
        Path = "/models/coder.gguf",
        Temperature = 0.1,
        TopP = 0.95,
        MaxTokens = 8_192
    };

    public SandboxOptions Sandbox { get; init; } = new();

    public AgentLimits Limits { get; init; } = new();
}
