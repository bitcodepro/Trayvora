using System.Diagnostics;
using System.Text.Json;

namespace VoiceAgent.Desktop;

internal sealed record ClaudeChat(Guid Id, string Workspace, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Runs Claude Code in the selected workspace and routes MCP permission requests to the host.</summary>
internal enum ClaudePermissions { Ask, AllowEdits, AllowAll }

internal static class ClaudeChats
{
    internal static string Mode(ClaudePermissions permissions) => permissions switch
    {
        ClaudePermissions.AllowEdits => "acceptEdits",
        ClaudePermissions.AllowAll => "bypassPermissions",
        _ => "default",
    };

    private sealed record CachedChat(long Size, DateTime Changed, ClaudeChat Chat);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedChat> Cached = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ClaudeChat> Find(string profile)
    {
        var projects = Path.Combine(profile, "projects");
        if (!Directory.Exists(projects)) return [];
        var chats = new List<(ClaudeChat Chat, DateTime Changed)>();
        foreach (var directory in Directory.EnumerateDirectories(projects))
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.jsonl"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out var id)) continue;
            if (Cached.TryGetValue(file.FullName, out var previous) && previous.Size == file.Length && previous.Changed == file.LastWriteTimeUtc && Directory.Exists(previous.Chat.Workspace))
            {
                chats.Add((previous.Chat, previous.Changed));
                continue;
            }
            string? workspace = null;
            string? title = null;
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                for (var line = 0; line < 64 && reader.ReadLine() is { } text; line++)
                {
                    using var item = JsonDocument.Parse(text);
                    var root = item.RootElement;
                    if (root.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                        workspace ??= cwd.GetString();
                    if (root.TryGetProperty("type", out var type) && type.GetString() == "user" && !Flag(root, "isMeta") &&
                        !Flag(root, "isSidechain") && root.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var content))
                        title ??= UserText(content);
                    if (workspace is not null && title is not null) break;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            if (workspace is null || !Directory.Exists(workspace)) continue;
            // A name given in Claude Desktop (or with /rename) wins over the first message.
            if (CustomTitle(file.FullName) is { } named) title = named;
            title = string.IsNullOrWhiteSpace(title) ? $"{Path.GetFileName(workspace)} · {id.ToString()[..8]}" : title.ReplaceLineEndings(" ");
            var chat = new ClaudeChat(id, workspace, title[..Math.Min(title.Length, 110)]);
            Cached[file.FullName] = new(file.Length, file.LastWriteTimeUtc, chat);
            chats.Add((chat, file.LastWriteTimeUtc));
        }
        // The chat used most recently comes first.
        return chats.OrderByDescending(item => item.Changed).Select(item => item.Chat).ToArray();
    }

    /// <summary>Whether Claude Code already saved this chat in the profile.</summary>
    internal static bool Exists(string profile, Guid id)
    {
        var projects = Path.Combine(profile, "projects");
        try { return Directory.Exists(projects) && Directory.EnumerateFiles(projects, $"{id}.jsonl", SearchOption.AllDirectories).Any(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    private const string TitleRecord = "{\"type\":\"custom-title\"";

    /// <summary>
    /// The latest "custom-title" record in a chat file. Claude appends one on every rename and repeats it later,
    /// so the end of the file usually has it; older files are scanned whole.
    /// </summary>
    internal static string? CustomTitle(string path)
    {
        static string? Last(IEnumerable<string> lines)
        {
            string? found = null;
            foreach (var line in lines)
            {
                if (!line.StartsWith(TitleRecord, StringComparison.Ordinal)) continue;
                try
                {
                    using var item = JsonDocument.Parse(line);
                    if (item.RootElement.TryGetProperty("customTitle", out var value) && value.ValueKind == JsonValueKind.String &&
                        value.GetString()?.Trim() is { Length: > 0 } text) found = text;
                }
                catch (JsonException) { }
            }
            return found;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int tail = 1 << 20;
            if (stream.Length > tail)
            {
                stream.Seek(-tail, SeekOrigin.End);
                using var end = new StreamReader(stream, leaveOpen: true);
                end.ReadLine(); // skip the partial first line
                if (Last(ReadLines(end)) is { } recent) return recent;
                stream.Seek(0, SeekOrigin.Begin);
            }
            using var reader = new StreamReader(stream);
            return Last(ReadLines(reader));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }

        static IEnumerable<string> ReadLines(StreamReader reader)
        {
            while (reader.ReadLine() is { } line) yield return line;
        }
    }

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The text a person typed: skips command output, reminders and other wrapped blocks Claude Code stores as user turns.</summary>
    internal static string? UserText(JsonElement content)
    {
        static string? Typed(string? text)
        {
            text = text?.Trim();
            return string.IsNullOrEmpty(text) || text.StartsWith('<') || text.StartsWith("Caveat:", StringComparison.Ordinal) ? null : text;
        }

        if (content.ValueKind == JsonValueKind.String) return Typed(content.GetString());
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var kind) && kind.GetString() == "text" &&
                block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && Typed(text.GetString()) is { } typed)
                return typed;
        }
        return null;
    }

    public static string DefaultProfile()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR", EnvironmentVariableTarget.User);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
    }

    internal static string FindExecutable(string configured)
    {
        static bool Exists(string path) => Path.IsPathFullyQualified(path) && File.Exists(path)
            && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

        if (Exists(configured)) return configured;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => path.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, "claude.exe"))
            .Append(Path.Combine(home, ".local", "bin", "claude.exe"))
            // The launcher redirects LOCALAPPDATA, so look under the real profile folder.
            .Concat(DesktopBundled(Path.Combine(home, "AppData", "Local", "Claude-Data")));
        return candidates.FirstOrDefault(Exists)
            ?? throw new FileNotFoundException("Claude Code was not found. Install Claude Desktop or the Claude Code CLI, or set the claude.exe path under Claude Code.");
    }

    /// <summary>
    /// Claude Code copies bundled with Claude Desktop: Claude-Data\&lt;package&gt;\claude-code\&lt;version&gt;\&lt;build&gt;\claude.exe, newest version first.
    /// </summary>
    internal static IEnumerable<string> DesktopBundled(string root)
    {
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateDirectories(root)
                .Select(package => Path.Combine(package, "claude-code"))
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateDirectories)
                .Select(folder => (Folder: folder, Version: Version.TryParse(Path.GetFileName(folder), out var version) ? version : null))
                .Where(item => item.Version is not null)
                .OrderByDescending(item => item.Version)
                .SelectMany(item => Directory.EnumerateDirectories(item.Folder).Select(build => Path.Combine(build, "claude.exe")))
                .Where(File.Exists)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The user's real profile folders, read from Windows rather than from the (redirected) environment.</summary>
    internal static IEnumerable<(string Name, string Value)> RealUserFolders()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (Path.IsPathFullyQualified(roaming)) yield return ("APPDATA", roaming);
        if (Path.IsPathFullyQualified(local))
        {
            yield return ("LOCALAPPDATA", local);
            // TEMP and TMP remain in the portable app’s temporary folder.
        }
    }

    internal static Dictionary<string, string?> CliEnvironment(string profile)
    {
        var env = RealUserFolders().ToDictionary(item => item.Name, item => (string?)item.Value);
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (Path.IsPathFullyQualified(profile) && (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is not null ||
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR", EnvironmentVariableTarget.User) is not null ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)), standard, StringComparison.OrdinalIgnoreCase)))
            env["CLAUDE_CONFIG_DIR"] = profile;
        return env;
    }

    /// <summary>Short, readable reason from Claude Code's own output (its result text or the last stderr lines).</summary>
    internal static string FailureMessage(string? reported, string stderr, string stdout)
    {
        static string Clip(string text) => text.Length <= 400 ? text : text[..400] + "…";
        if (!string.IsNullOrWhiteSpace(reported))
        {
            var text = "Claude Code: " + Clip(reported.Trim());
            // The sign-in is shared with Claude Desktop and other Claude Code windows; a refresh there can end this one.
            return reported.Contains("authenticat", StringComparison.OrdinalIgnoreCase) || reported.Contains("OAuth", StringComparison.OrdinalIgnoreCase)
                ? text + "\nSign in again: open a terminal, run claude, then /login. Then send the message again."
                : text;
        }
        var lines = stderr.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 0) return "Claude Code: " + Clip(string.Join(" ", lines.TakeLast(3)));
        if (!string.IsNullOrWhiteSpace(stdout)) return "Claude Code: " + Clip(stdout.Trim());
        return "Claude Code exited without an answer. Check the sign-in and the selected chat in Claude Code.";
    }

    /// <summary>
    /// Sends one message to the selected Claude Code session and returns its final answer. While Claude works,
    /// <paramref name="stream"/> receives the text as it is written and the tool in use.
    /// </summary>
    public static async Task<string> SendAsync(string executable, string profile, ClaudeChat chat, string prompt, CancellationToken ct, ReplyStream? stream = null,
        ClaudePermissions permissions = ClaudePermissions.Ask, string? readableFolder = null, ClaudeToolPrompts.Confirm? confirm = null,
        string? effort = null, string? model = null)
    {
        executable = FindExecutable(executable);

        if (!Path.IsPathFullyQualified(profile))
            throw new IOException("Set the Claude Code profile folder under Claude Code.");
        using var turn = await VoiceAgent.Codex.AgentTurnQueue.EnterAsync(profile, chat.Id, ct);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = chat.Workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        // stream-json prints one event per line; --verbose is required for it in print mode.
        foreach (var arg in new[] { "--print", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-mode", Mode(permissions) })
            start.ArgumentList.Add(arg);
        ClaudeCommands.AddTurnOptions(start.ArgumentList, effort, model);
        // Resume the selected session with its own tools, MCP servers and permission settings.
        // A chat created in Trayvora has no file until its first message; that message starts it under the same ID.
        var mode = new[] { Exists(profile, chat.Id) ? "--resume" : "--session-id", chat.Id.ToString() };
        foreach (var arg in mode) start.ArgumentList.Add(arg);
        // Long messages arrive as a .txt file outside the chat folder; let Claude read that folder too.
        if (readableFolder is not null && Directory.Exists(readableFolder))
        {
            start.ArgumentList.Add("--add-dir");
            start.ArgumentList.Add(readableFolder);
        }
        foreach (var (name, value) in CliEnvironment(profile)) start.Environment[name] = value;
        if (confirm is not null)
        {
            start.ArgumentList.Add("--input-format");
            start.ArgumentList.Add("stream-json");
            start.ArgumentList.Add("--permission-prompt-tool");
            start.ArgumentList.Add("stdio");
        }
        stream?.Activity("Starting Claude Code", record: false);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Claude Code.");
        var parser = new ClaudeStreamParser(stream);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (stream?.BeforeDispatch is { } dispatch) await dispatch();
            ct.ThrowIfCancellationRequested();
            if (confirm is not null && stream is not null)
            {
                using var hardStop = CancellationTokenSource.CreateLinkedTokenSource(stream.Emergency);
                using var deadline = ct.Register(() => hardStop.CancelAfter(TimeSpan.FromSeconds(8)));
                if (await ClaudeToolPrompts.RunAsync(process.StandardInput, process.StandardOutput, prompt, parser, confirm, hardStop.Token, ct))
                {
                    stream.StopConfirmed = true;
                    stream.StopOutcome = "Claude confirmed the stop.";
                    if (!parser.Failed && !string.IsNullOrWhiteSpace(parser.Result)) stream.Replace(parser.Result);
                    await process.WaitForExitAsync(hardStop.Token);
                    throw new OperationCanceledException(ct);
                }
                await process.WaitForExitAsync(hardStop.Token);
            }
            else if (confirm is not null)
                await ClaudeToolPrompts.RunAsync(process.StandardInput, process.StandardOutput, prompt, parser, confirm, ct);
            else
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
                process.StandardInput.Close();
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line) parser.Accept(line);
            }
            if (!process.HasExited) await process.WaitForExitAsync(ct);
            var errors = await stderr;
            if (process.ExitCode != 0 || parser.Failed || parser.Result is null)
                throw new IOException(ErrorLog.Redact(FailureMessage(parser.Result, errors, parser.Other)));
            return parser.Result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                if (ct.IsCancellationRequested && stream is not null && stream.StopOutcome is null)
                    stream.StopOutcome = "Claude process stopped. Remote work may still be running.";
            }

        }
    }
}

/// <summary>
/// Reads Claude Code's stream-json output (one JSON event per line): text deltas and tool use go to the live
/// reply, the final "result" event carries the answer. Subagent events are ignored.
/// </summary>
internal sealed class ClaudeStreamParser(ReplyStream? stream)
{
    private readonly System.Text.StringBuilder _other = new();
    private bool _partial;
    private string? _messageId;
    private readonly Dictionary<string, string> _tools = new(StringComparer.Ordinal);

    /// <summary>The final answer, once the result event has arrived.</summary>
    public string? Result { get; private set; }
    public bool Failed { get; private set; }

    /// <summary>Output that was not a JSON event; shown when Claude Code exits without a result.</summary>
    public string Other => _other.ToString();

    public void Accept(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException)
        {
            Keep(line);
            return;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Keep(line);
                return;
            }
            Accept(root);
        }
    }

    internal void Accept(JsonElement root)
    {
        var main = !root.TryGetProperty("parent_tool_use_id", out var parent) || parent.ValueKind == JsonValueKind.Null;
        switch (Text(root, "type"))
        {
            case "stream_event" when root.TryGetProperty("event", out var item) && item.ValueKind == JsonValueKind.Object:
                if (main) _partial = true;
                Event(item, main);
                break;
            case "assistant" when root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object:
                var agentReply = main && (!root.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null);
                if (agentReply) stream?.ConfirmReceipt();
                Message(message, main, agentReply);
                break;
            case "user" when root.TryGetProperty("message", out var user) && user.ValueKind == JsonValueKind.Object:
                if (user.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (block.ValueKind == JsonValueKind.Object && Text(block, "type") == "tool_result") CompleteTool(block);
                break;
            case "tool_progress":
                ToolProgress(root);
                break;
            case "system":
                SystemEvent(root);
                break;
            case "result":
                Result = Text(root, "result");
                Failed = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
                Denials(root);
                stream?.Activity(null);
                break;
        }
    }

    private void Event(JsonElement item, bool main)
    {
        switch (Text(item, "type"))
        {
            case "message_start" when main:
                stream?.ConfirmReceipt();
                stream?.Restart();
                stream?.Activity(null, record: false);
                break;
            case "content_block_start" when item.TryGetProperty("content_block", out var block) && block.ValueKind == JsonValueKind.Object:
                ObserveBlock(block, main);
                break;
            case "content_block_delta" when main && item.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && Text(delta, "type") == "text_delta":
                stream?.Activity(null, record: false);
                stream?.Append(Text(delta, "text"));
                break;
        }
    }

    private void Message(JsonElement message, bool main, bool agentReply)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        if (main && !_partial && Text(message, "id") is var id && id != _messageId)
        {
            _messageId = id;
            stream?.Restart();
        }
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (main && !_partial && Text(block, "type") == "text") stream?.Append(Text(block, "text"), confirmReceipt: agentReply);
            else ObserveBlock(block, main);
        }
    }

    private void ObserveBlock(JsonElement block, bool main)
    {
        var type = Text(block, "type");
        if (type is "tool_use" or "server_tool_use")
        {
            var label = ToolLabel(Text(block, "name"), block.TryGetProperty("input", out var input) ? input : default);
            if (Identifier(Text(block, "id")) is { } id)
            {
                if (_tools.TryGetValue(id, out var previous) && (previous == label || previous.StartsWith(label + " ·", StringComparison.Ordinal))) return;
                if (_tools.Count >= 512) _tools.Remove(_tools.Keys.First());
                _tools[id] = label;
                stream?.Observe(new("tool:" + id, label, "running"));
            }
            else stream?.Activity(label);
        }
        else if (type is "thinking" or "redacted_thinking")
        {
            if (main) stream?.Activity("Thinking", record: false);
        }
        else if (type?.EndsWith("_tool_result", StringComparison.Ordinal) == true) CompleteTool(block);
        else if (main && type == "text") stream?.Activity(null, record: false);
    }

    private void ToolProgress(JsonElement item)
    {
        if (Identifier(Text(item, "tool_name")) is not { } name) return;
        var label = name;
        if (Identifier(Text(item, "tool_use_id")) is { } id)
        {
            if (!_tools.TryGetValue(id, out var saved))
            {
                if (_tools.Count >= 512) _tools.Remove(_tools.Keys.First());
                _tools[id] = label;
                stream?.Observe(new("tool:" + id, label, "running"));
            }
            else label = saved;
        }
        var elapsed = item.TryGetProperty("elapsed_time_seconds", out var time) && time.ValueKind == JsonValueKind.Number
            && time.TryGetDouble(out var seconds) && seconds is >= 0 and < 31536000 ? $" · {(int)seconds} s" : "";
        stream?.Activity($"Running · {label}{elapsed}", record: false);
    }

    private void CompleteTool(JsonElement result)
    {
        if (Identifier(Text(result, "tool_use_id")) is not { } id || !_tools.Remove(id, out var label)) return;
        var failed = result.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
        stream?.Observe(new("tool:" + id, label, failed ? "failed" : "completed"));
        stream?.Activity(_tools.Count == 0 ? "Waiting for Claude" : _tools.Values.Last(), record: false);
    }

    private void SystemEvent(JsonElement item)
    {
        switch (Text(item, "subtype"))
        {
            case "init":
                if (item.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                    foreach (var server in servers.EnumerateArray())
                    {
                        if (server.ValueKind != JsonValueKind.Object || Identifier(Text(server, "name")) is not { } name) continue;
                        var status = Identifier(Text(server, "status")) ?? "unknown";
                        stream?.RecordStep($"MCP · {name} · {status}");
                        if (status is "failed" or "needs-auth") stream?.Note($"MCP · {name} · {status}");
                    }
                stream?.Activity("Waiting for Claude", record: false);
                break;
            case "status" when Text(item, "status") == "compacting":
                stream?.Activity("Compacting the chat context");
                break;
            case "compact_boundary":
                stream?.RecordStep("Chat context compacted");
                stream?.Activity("Waiting for Claude", record: false);
                break;
            case "api_retry":
                stream?.Activity("Retrying the Claude API");
                break;
            case "task_started":
                if (Identifier(Text(item, "task_id")) is { } startedTask) stream?.Observe(new("task:" + startedTask, "Background task", "running"));
                else stream?.Activity("Background task started");
                break;
            case "task_progress":
                var last = Identifier(Text(item, "last_tool_name"));
                stream?.Activity(last is null ? "Background task running" : $"Background task running · {last}", record: false);
                break;
            case "task_notification" when Text(item, "status") is "completed" or "failed" or "stopped":
                if (Identifier(Text(item, "task_id")) is { } endedTask)
                    stream?.Observe(new("task:" + endedTask, "Background task", Text(item, "status")));
                else stream?.RecordStep($"Background task · {Text(item, "status")}");
                stream?.Activity("Waiting for Claude", record: false);
                break;
        }
    }

    internal void PermissionStatus(string name, string status) =>
        stream?.Activity($"{status} · {Identifier(name) ?? "MCP tool"}");

    private void Denials(JsonElement result)
    {
        if (stream is null || !result.TryGetProperty("permission_denials", out var denials) || denials.ValueKind != JsonValueKind.Array) return;
        var tools = denials.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object ? Identifier(Text(item, "tool_name")) : null)
            .OfType<string>().Distinct(StringComparer.Ordinal).Take(8).ToArray();
        if (tools.Length > 0)
            stream.Note($"Claude Code was not allowed to use: {string.Join(", ", tools)}. Allow it in Claude Code for that chat if the task needs it.");
    }

    /// <summary>Names structured file tools use in the log. Free-text arguments and tool output are excluded.</summary>
    internal static string ToolLabel(string? name, JsonElement input)
    {
        var tool = Identifier(name) ?? "Tool";
        if (input.ValueKind != JsonValueKind.Object || tool is not ("Read" or "Edit" or "Write" or "NotebookEdit" or "Glob" or "Grep"))
            return tool;
        var path = Text(input, "file_path") ?? Text(input, "notebook_path") ?? Text(input, "path");
        if (string.IsNullOrWhiteSpace(path)) return tool;
        var file = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
        var detail = ErrorLog.Redact(new string(file.Where(ch => !char.IsControl(ch)).ToArray())).Trim();
        if (detail.Length == 0) return tool;
        return $"{tool} · {(detail.Length <= 60 ? detail : detail[..60] + "…")}";
    }

    private static string? Identifier(string? name) =>
        name is { Length: > 0 and <= 64 } && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? name : null;

    private void Keep(string line)
    {
        if (_other.Length < 4000) _other.AppendLine(line.Length <= 1000 ? line : line[..1000]);
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
