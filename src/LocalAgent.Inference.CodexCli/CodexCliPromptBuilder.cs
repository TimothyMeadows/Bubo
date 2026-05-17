namespace Bubo.LocalAgent.Inference.CodexCli;

public static class CodexCliPromptBuilder
{
    public static string Compose(string? systemPrompt, string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return prompt;
        }

        return $"""
               <system>
               {systemPrompt}
               </system>

               <user>
               {prompt}
               </user>
               """;
    }
}
