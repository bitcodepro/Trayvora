using System.Text.Json;

namespace VoiceAgent.Desktop;

/// <summary>One print-mode Claude turn. Keep stdin open for the CLI's per-call permission protocol.</summary>
internal static class ClaudeToolPrompts
{
    internal delegate Task<bool> Confirm(string name, JsonElement input, CancellationToken ct);

    public static async Task RunAsync(TextWriter stdin, TextReader stdout, string prompt, ClaudeStreamParser parser,
        Confirm confirm, CancellationToken ct)
    {
        async Task WriteAsync(object value)
        {
            await stdin.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        Task Reply(string id, object decision) => WriteAsync(new { type = "control_response", response = new { subtype = "success", request_id = id, response = decision } });
        Task Deny(string id) => Reply(id, new { behavior = "deny", message = "Permission was not granted for this tool call." });

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? dialog = null;
        Task<bool>? decision = null;
        var waiting = new List<(string Id, string Tool, JsonElement Input)>();
        string? pending = null;
        string? pendingTool = null;
        JsonElement arguments = default;
        try
        {
            await WriteAsync(new { type = "user", message = new { role = "user", content = prompt }, parent_tool_use_id = (string?)null, session_id = "" }).ConfigureAwait(false);
            var next = stdout.ReadLineAsync(ct).AsTask();
            while (true)
            {
                if (decision is null && waiting.Count > 0)
                {
                    var first = waiting[0];
                    waiting.RemoveAt(0);
                    pending = first.Id;
                    pendingTool = first.Tool;
                    parser.PermissionStatus(first.Tool, "Waiting for permission");
                    arguments = first.Input;
                    dialog = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    decision = confirm(first.Tool, first.Input, dialog.Token);
                }
                if (decision is not null && await Task.WhenAny(decision, next).ConfigureAwait(false) == decision)
                {
                    var allowed = await decision.ConfigureAwait(false);
                    parser.PermissionStatus(pendingTool!, allowed ? "Permission granted" : "Permission denied");
                    if (allowed)
                        await Reply(pending!, new { behavior = "allow", updatedInput = arguments }).ConfigureAwait(false);
                    else await Deny(pending!).ConfigureAwait(false);
                    decision = null;
                    dialog!.Dispose();
                    dialog = null;
                    pending = null;
                    continue;
                }
                var line = await next.ConfigureAwait(false);
                if (line is null) break;
                JsonElement data;
                try { data = JsonSerializer.Deserialize<JsonElement>(line); }
                catch (JsonException) { parser.Accept(line); next = stdout.ReadLineAsync(ct).AsTask(); continue; }
                var type = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("type", out var kind) ? kind.ToString() : "";
                if (type == "control_request" && data.TryGetProperty("request_id", out var rid))
                {
                    var id = rid.GetString()!;
                    var request = data.GetProperty("request");
                    if (request.GetProperty("subtype").GetString() != "can_use_tool")
                        await WriteAsync(new { type = "control_response", response = new { subtype = "error", request_id = id, error = "Unsupported host request" } }).ConfigureAwait(false);
                    else if (request.TryGetProperty("tool_name", out var tool) &&
                        tool.GetString() is { } name && name.StartsWith("mcp__", StringComparison.Ordinal) &&
                        request.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                    {
                        waiting.Add((id, name, input));
                    }
                    else await Deny(id).ConfigureAwait(false);
                }
                else if (type == "control_cancel_request" && data.TryGetProperty("request_id", out var cancelled))
                {
                    var id = cancelled.GetString();
                    waiting.RemoveAll(item => item.Id == id);
                    if (id == pending && dialog is not null)
                    {
                        parser.PermissionStatus(pendingTool!, "Permission request closed");
                        dialog.Cancel();
                        dialog.Dispose();
                        dialog = null;
                        decision = null;
                        pending = null;
                    }
                }
                else
                {
                    if (data.ValueKind == JsonValueKind.Object) parser.Accept(data);
                    else parser.Accept(line);
                    if (type == "result") break;
                }
                next = stdout.ReadLineAsync(ct).AsTask();
            }
        }
        finally
        {
            lifetime.Cancel();
            dialog?.Dispose();
            stdin.Close();
        }
    }
}
