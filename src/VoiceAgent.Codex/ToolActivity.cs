using System.Text.Json;

namespace VoiceAgent.Codex;

public sealed record ToolActivity(string Name, string Status);

internal sealed class TurnToolActivities
{
    private const int Limit = 64;
    private readonly Dictionary<string, ToolActivity> _items = new(StringComparer.Ordinal);

    public void Observe(JsonElement data, bool completed)
    {
        if (!data.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object ||
            ReadString(item, "id") is not { Length: > 0 and <= 128 } id)
        {
            return;
        }

        var name = ReadString(item, "type") switch
        {
            "commandExecution" => "Command execution",
            "fileChange" => "File change",
            "mcpToolCall" => $"MCP · {Identifier(item, "tool") ?? "Tool call"}",
            "dynamicToolCall" => Identifier(item, "tool") ?? "Dynamic tool",
            "collabAgentToolCall" => "Agent tool",
            "webSearch" => "Web search",
            "imageView" => "Image view",
            "imageGeneration" => "Image generation",
            _ => null,
        };
        if (name is null || (_items.Count >= Limit && !_items.ContainsKey(id)))
        {
            return;
        }

        if (completed)
        {
            var status = ReadString(item, "status") switch
            {
                "failed" => "Failed",
                "declined" => "Declined",
                "cancelled" or "interrupted" => "Interrupted",
                "completed" => "Completed",
                null when ReadString(item, "type") is "webSearch" or "imageView" => "Completed",
                _ => "Unknown",
            };
            _items[id] = new ToolActivity(name, status);
        }
        else
        {
            _items.TryAdd(id, new ToolActivity(name, "Unknown"));
        }
    }

    public void Blocked(string category)
    {
        if (_items.Count < Limit)
            _items.TryAdd("blocked:" + category, new ToolActivity(category, "Denied"));
    }

    public IReadOnlyList<ToolActivity> Snapshot() => _items.Values.ToArray();

    /// <summary>A short line about what Codex has just started ("Running · git status"), or null for items not worth showing.</summary>
    public static string? Describe(JsonElement data)
    {
        if (!data.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
        return ReadString(item, "type") switch
        {
            "commandExecution" => Detail("Running", Command(item)),
            "fileChange" => Detail("Editing", ChangedFiles(item)),
            "mcpToolCall" => Detail("MCP", Identifier(item, "tool")),
            "dynamicToolCall" => Detail("Tool", Identifier(item, "tool")),
            "collabAgentToolCall" => "Asking another agent",
            "webSearch" => Detail("Searching the web", ReadString(item, "query")),
            "imageView" => "Looking at an image",
            "imageGeneration" => "Generating an image",
            "reasoning" => "Thinking",
            _ => null,
        };
    }

    private static string Detail(string what, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return what;
        var line = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"{what} · {(line.Length <= 90 ? line : line[..90] + "…")}";
    }

    private static string? Command(JsonElement item)
    {
        if (!item.TryGetProperty("command", out var command)) return null;
        if (command.ValueKind == JsonValueKind.String) return Unwrap(command.GetString());
        if (command.ValueKind != JsonValueKind.Array) return null;
        var parts = command.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.String).Select(part => part.GetString()!).ToArray();
        return Unwrap(string.Join(' ', parts));
    }

    /// <summary>Drops a shell wrapper such as <c>powershell.exe -Command "…"</c> so the real command shows.</summary>
    internal static string? Unwrap(string? command)
    {
        if (command is null) return null;
        foreach (var marker in new[] { " -Command ", " -c ", " /c " })
        {
            var at = command.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at < 80) { command = command[(at + marker.Length)..]; break; }
        }
        return command.Trim().Trim('"', '\'').Trim();
    }

    private static string? ChangedFiles(JsonElement item)
    {
        if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) return null;
        var names = changes.EnumerateArray()
            .Select(change => change.ValueKind == JsonValueKind.Object ? ReadString(change, "path") : null)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => Path.GetFileName(path!.TrimEnd('/', '\\')))
            .Distinct().ToArray();
        return names.Length == 0 ? null : names.Length <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names[..3])} +{names.Length - 3}";
    }

    private static string? Identifier(JsonElement item, string key) =>
        ReadString(item, key) is { Length: > 0 and <= 64 } name &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? name : null;

    private static string? ReadString(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
