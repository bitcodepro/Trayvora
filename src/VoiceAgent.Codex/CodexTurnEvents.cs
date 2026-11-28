using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VoiceAgent.Codex;

/// <summary>A public turn event suitable for an activity view. Never contains tool arguments or raw reasoning.</summary>
public sealed record CodexActivity(string Id, string Label, string? Status = null);

internal sealed class CodexTurnEvents(IProgress<string>? deltas, IProgress<string>? answer, IProgress<CodexActivity>? activity)
{
    private sealed class Message
    {
        public string? Phase { get; set; }
        public StringBuilder Text { get; } = new();
        public int Reported { get; set; }
    }

    private const int MaxText = 4_000_000;
    private readonly Dictionary<string, Message> _messages = [];
    private readonly Dictionary<string, StringBuilder> _updates = [];
    private int _length;
    private long _publishedAt;
    private bool _answerDirty;

    public string Answer => string.Join("\n\n", _messages.Values.Where(item => item.Phase == "final_answer").Select(item => item.Text.ToString()));
    private Message Find(string id) => _messages.TryGetValue(id, out var message) ? message : _messages[id] = new();

    public bool Observe(string method, JsonElement data)
    {
        switch (method)
        {
            case "item/agentMessage/delta":
                var id = String(data, "itemId") ?? "legacy";
                var part = Find(id);
                if (id == "legacy") part.Phase = "final_answer";
                if (String(data, "phase") is { } phase) part.Phase = phase;
                var delta = String(data, "delta") ?? "";
                if (delta.Length > MaxText - _length) return false;
                _length += delta.Length;
                part.Text.Append(delta);
                UpdateMessage(id, part);
                break;
            case "item/started":
            case "item/completed":
                if (data.TryGetProperty("item", out var item))
                    UpdateItem(item, method == "item/completed");
                break;
            case "item/plan/delta":
            case "item/reasoning/summaryTextDelta":
                var kind = method == "item/plan/delta" ? "Plan" : "Summary";
                var key = kind + ":" + (String(data, "itemId") ?? "") + ":" + (data.TryGetProperty("summaryIndex", out var index) ? index.ToString() : "");
                if (!_updates.TryGetValue(key, out var update))
                {
                    if (_updates.Count >= 128) break;
                    _updates[key] = update = new();
                }
                var text = String(data, "delta") ?? "";
                update.Append(text.AsSpan(0, Math.Min(text.Length, Math.Max(0, 1800 - update.Length))));
                activity?.Report(new(key, $"{kind} · {update}"));
                break;
            case "turn/plan/updated":
                if (data.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Array)
                    foreach (var (step, position) in plan.EnumerateArray().Take(30).Select((step, position) => (step, position)))
                        if (String(step, "step") is { } task)
                            activity?.Report(new($"plan:{position}", $"Plan · {task}", String(step, "status") == "inProgress" ? "running" : String(step, "status")));
                break;
        }
        return true;
    }

    public void UpdateItem(JsonElement item, bool completed)
    {
        if (String(item, "id") is not { } id) return;
        var type = String(item, "type");
        if (type == "agentMessage")
        {
            var part = Find(id);
            part.Phase = String(item, "phase") ?? part.Phase;
            if (completed)
            {
                part.Phase ??= "final_answer";
                if (String(item, "text") is { } text)
                    part.Text.Clear().Append(text.AsSpan(0, Math.Min(text.Length, MaxText)));
            }
            UpdateMessage(id, part);
            if (completed) FlushAnswer();
            return;
        }

        var label = type switch
        {
            "commandExecution" => "Command",
            "fileChange" => "File change",
            "webSearch" => "Web search",
            "mcpToolCall" => $"MCP · {Identifier(String(item, "server")) ?? "server"} · {Identifier(String(item, "tool")) ?? "tool"}",
            "dynamicToolCall" => $"Tool · {Identifier(String(item, "tool")) ?? "call"}",
            _ => null
        };
        if (label is not null)
        {
            var status = String(item, "status") switch
            {
                "failed" or "declined" or "interrupted" => String(item, "status"),
                "inProgress" or "running" => "running",
                _ => completed ? "completed" : "running",
            };
            activity?.Report(new("tool:" + id, label, status));
        }
        else if (completed && type == "plan" && String(item, "text") is { } plan)
            activity?.Report(new("Plan:" + id + ":", "Plan · " + plan));
        else if (completed && type == "reasoning" && item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
            foreach (var (part, index) in summary.EnumerateArray().Take(20).Select((part, index) => (part, index)))
                if (part.ValueKind == JsonValueKind.String)
                    activity?.Report(new($"Summary:{id}:{index}", $"Summary · {part.GetString()}"));
    }

    private void UpdateMessage(string id, Message part)
    {
        if (part.Phase == "commentary")
        {
            if (part.Text.Length > 0)
                activity?.Report(new("commentary:" + id, "Commentary · " + part.Text.ToString(0, Math.Min(part.Text.Length, 1800))));
        }
        else if (part.Phase == "final_answer")
        {
            if (answer is not null)
            {
                _answerDirty = true;
                if (_publishedAt == 0 || Stopwatch.GetElapsedTime(_publishedAt).TotalMilliseconds >= 70) FlushAnswer();
            }
            else if (part.Text.Length > part.Reported)
                deltas?.Report(part.Text.ToString(part.Reported, part.Text.Length - part.Reported));
            part.Reported = part.Text.Length;
        }
    }

    public void FlushAnswer()
    {
        if (!_answerDirty) return;
        _answerDirty = false;
        _publishedAt = Stopwatch.GetTimestamp();
        answer?.Report(Answer);
    }

    internal static string? String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Identifier(string? name) => name is { Length: > 0 and <= 100 }
        && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.') ? name : null;
}
