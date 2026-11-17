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

    private static string? Identifier(JsonElement item, string key) =>
        ReadString(item, key) is { Length: > 0 and <= 64 } name &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? name : null;

    private static string? ReadString(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
