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

    internal static Dictionary<string, string> CliEnvironment(string profile)
    {
        var env = RealUserFolders().ToDictionary(item => item.Name, item => item.Value);
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
        ClaudePermissions permissions = ClaudePermissions.Ask, string? readableFolder = null, ClaudeToolPrompts.Confirm? confirm = null)
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
        using var process = Process.Start(start) ?? throw new IOException("Could not start Claude Code.");
        var parser = new ClaudeStreamParser(stream);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (confirm is not null)
                await ClaudeToolPrompts.RunAsync(process.StandardInput, process.StandardOutput, prompt, parser, confirm, ct);
            else
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
                process.StandardInput.Close();
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line) parser.Accept(line);
            }
            await process.WaitForExitAsync(ct);
            var errors = await stderr;
            if (process.ExitCode != 0 || parser.Failed || parser.Result is null)
                throw new IOException(FailureMessage(parser.Result, errors, parser.Other));
            return parser.Result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
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
            case "stream_event" when main && root.TryGetProperty("event", out var item) && item.ValueKind == JsonValueKind.Object:
                _partial = true;
                Event(item);
                break;
            case "assistant" when main && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object:
                Message(message);
                break;
            case "system" when Text(root, "subtype") == "init" && root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array:
                foreach (var server in servers.EnumerateArray())
                {
                    if (server.ValueKind != JsonValueKind.Object || Text(server, "name") is not { } name) continue;
                    var status = Text(server, "status");
                    stream?.Activity($"MCP · {name} · {status}");
                    if (status is "failed" or "needs-auth") stream?.Note($"MCP · {name} · {status}");
                }
                break;
            case "result":
                Result = Text(root, "result");
                Failed = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
                Denials(root);
                stream?.Activity(null);
                break;
        }
    }

    private void Event(JsonElement item)
    {
        switch (Text(item, "type"))
        {
            case "message_start":
                stream?.Restart();
                stream?.Activity(null);
                break;
            case "content_block_start" when item.TryGetProperty("content_block", out var block) && block.ValueKind == JsonValueKind.Object:
                stream?.Activity(Text(block, "type") switch
                {
                    "tool_use" or "server_tool_use" => ToolLabel(Text(block, "name"), default),
                    "thinking" or "redacted_thinking" => "Thinking",
                    _ => null,
                });
                break;
            case "content_block_delta" when item.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && Text(delta, "type") == "text_delta":
                stream?.Append(Text(delta, "text"));
                break;
        }
    }

    private void Message(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        // Without partial events (older Claude Code) the text arrives here, one finished block at a time.
        if (!_partial && Text(message, "id") is var id && id != _messageId)
        {
            _messageId = id;
            stream?.Restart();
        }
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            switch (Text(block, "type"))
            {
                case "text" when !_partial:
                    stream?.Append(Text(block, "text"));
                    break;
                case "tool_use" or "server_tool_use":
                    stream?.Activity(ToolLabel(Text(block, "name"), block.TryGetProperty("input", out var input) ? input : default));
                    break;
            }
        }
    }

    private void Denials(JsonElement result)
    {
        if (stream is null || !result.TryGetProperty("permission_denials", out var denials) || denials.ValueKind != JsonValueKind.Array) return;
        var tools = denials.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object ? Identifier(Text(item, "tool_name")) : null)
            .OfType<string>().Distinct(StringComparer.Ordinal).Take(8).ToArray();
        if (tools.Length > 0)
            stream.Note($"Claude Code was not allowed to use: {string.Join(", ", tools)}. Allow it in Claude Code for that chat if the task needs it.");
    }

    /// <summary>"Read · Client.cs": the tool and, when it is safe to show, the file or search it works on.</summary>
    internal static string ToolLabel(string? name, JsonElement input)
    {
        var tool = Identifier(name) ?? "Tool";
        if (input.ValueKind != JsonValueKind.Object) return tool;
        string? detail = null;
        foreach (var key in new[] { "file_path", "notebook_path", "path" })
            if (detail is null && Text(input, key) is { Length: > 0 } file) detail = Path.GetFileName(file.TrimEnd('/', '\\'));
        foreach (var key in new[] { "pattern", "description", "query" })
            if (detail is null && Text(input, key) is { Length: > 0 } value) detail = value;
        if (string.IsNullOrWhiteSpace(detail)) return tool;
        detail = detail.ReplaceLineEndings(" ").Trim();
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
