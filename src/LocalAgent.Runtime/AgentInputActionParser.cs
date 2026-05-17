using System.Text.Json;

namespace Bubo.LocalAgent.Runtime;

internal static class AgentInputActionParser
{
    private const string Fence = "```";
    private const string ActionFence = "```bubo-actions";

    public static IReadOnlyList<AgentInputAction> Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var actionJson = ExtractActionJson(input);
        if (string.IsNullOrWhiteSpace(actionJson))
        {
            return Array.Empty<AgentInputAction>();
        }

        using var document = ParseActionJson(actionJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("The bubo-actions fence must contain a JSON array.");
        }

        var actions = new List<AgentInputAction>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            actions.Add(ParseAction(element));
        }

        return actions;
    }

    public static IReadOnlyList<AgentInputAction> ParseSingleFence(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (CountActionFences(input) > 1)
        {
            throw new ArgumentException("Model output must contain at most one bubo-actions fence.");
        }

        return Parse(input);
    }

    private static JsonDocument ParseActionJson(string actionJson)
    {
        try
        {
            return JsonDocument.Parse(actionJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The bubo-actions fence must contain valid JSON.", exception);
        }
    }

    private static string? ExtractActionJson(string input)
    {
        using var reader = new StringReader(input);
        string? line;
        var collecting = false;
        var builder = new StringWriter();

        while ((line = reader.ReadLine()) is not null)
        {
            if (!collecting &&
                line.Trim().Equals(ActionFence, StringComparison.OrdinalIgnoreCase))
            {
                collecting = true;
                continue;
            }

            if (collecting && line.Trim().Equals(Fence, StringComparison.Ordinal))
            {
                return builder.ToString();
            }

            if (collecting)
            {
                builder.WriteLine(line);
            }
        }

        return null;
    }

    private static int CountActionFences(string input)
    {
        using var reader = new StringReader(input);
        var count = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim().Equals(ActionFence, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static AgentInputAction ParseAction(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Each bubo-actions item must be a JSON object.");
        }

        if (!TryGetProperty(element, "tool", out var toolElement) ||
            toolElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Each bubo-actions item requires a string `tool` property.");
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryGetProperty(element, "arguments", out var argumentsElement))
        {
            if (argumentsElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The bubo-actions `arguments` property must be an object.");
            }

            foreach (var property in argumentsElement.EnumerateObject())
            {
                arguments[property.Name] = ConvertArgumentValue(property.Value);
            }
        }

        return new AgentInputAction
        {
            Tool = toolElement.GetString()!,
            Arguments = arguments
        };
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ConvertArgumentValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => string.Join(
                Environment.NewLine,
                value.EnumerateArray().Select(ConvertArgumentValue)),
            _ => value.GetRawText()
        };
    }
}
