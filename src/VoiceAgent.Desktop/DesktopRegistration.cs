using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceAgent.Desktop;

/// <summary>
/// Makes a chat that Trayvora created visible in Codex Desktop: Codex Desktop lists a chat without a project only if
/// its id is in "projectless-thread-ids" of .codex-global-state.json. Codex Desktop rewrites that file while it runs,
/// so the id is added only while Codex Desktop is closed; until then it waits in settings\codex-pending-chats.json.
/// </summary>
internal static class CodexDesktopChats
{
    private const string Key = "projectless-thread-ids";
    private static readonly Lock Gate = new();

    public static string StateFile(string profile) => Path.Combine(profile, ".codex-global-state.json");

    /// <summary>True while the Codex Desktop app (not the codex CLI) is running.</summary>
    public static bool DesktopRunning()
    {
        foreach (var process in Process.GetProcessesByName("Codex"))
        {
            using (process)
            {
                if (!string.Equals(process.ProcessName, "Codex", StringComparison.Ordinal)) continue;
                try
                {
                    if (process.MainModule?.FileName is { } path && !path.Contains(@"\OpenAI.Codex", StringComparison.OrdinalIgnoreCase)) continue;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                return true;
            }
        }
        return false;
    }

    /// <summary>Lists <paramref name="threadId"/> among Codex Desktop's chats now, or as soon as Codex Desktop is closed.</summary>
    /// <returns>True if Codex Desktop shows it the next time it starts; false if it waits until Codex Desktop is closed.</returns>
    public static bool Register(string profile, string settingsDirectory, string threadId)
    {
        lock (Gate)
        {
            var pending = ReadPending(settingsDirectory);
            if (!pending.Contains(threadId)) pending.Add(threadId);
            WritePending(settingsDirectory, pending);
        }
        return ApplyPending(profile, settingsDirectory, running: DesktopRunning());
    }

    /// <summary>Adds waiting chats to Codex Desktop's list when it is not running. Returns true when nothing is left waiting.</summary>
    public static bool ApplyPending(string profile, string settingsDirectory, bool running)
    {
        lock (Gate)
        {
            var pending = ReadPending(settingsDirectory);
            if (pending.Count == 0) return true;
            if (running) return false;
            if (!AddToState(StateFile(profile), pending)) return false;
            WritePending(settingsDirectory, []);
            return true;
        }
    }

    /// <summary>Adds ids to the projectless list, keeping every other value in the file as it is.</summary>
    internal static bool AddToState(string file, IEnumerable<string> ids)
    {
        try
        {
            if (!File.Exists(file)) return false;
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (root is null) return false;
            if (root[Key] is not JsonArray list)
            {
                list = [];
                root[Key] = list;
            }
            var known = list.Select(item => item?.GetValue<string>()).ToHashSet();
            var changed = false;
            foreach (var id in ids.Where(id => !known.Contains(id)))
            {
                list.Add(id);
                changed = true;
            }
            if (!changed) return true;
            var backup = file + ".trayvora-backup";
            if (!File.Exists(backup)) File.Copy(file, backup);
            var temporary = file + ".trayvora.tmp";
            File.WriteAllText(temporary, root.ToJsonString());
            File.Move(temporary, file, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            ErrorLog.Write(error);
            return false;
        }
    }

    private static string PendingFile(string settingsDirectory) => Path.Combine(settingsDirectory, "codex-pending-chats.json");

    private static List<string> ReadPending(string settingsDirectory)
    {
        try { return SettingsFile.Read<List<string>>(PendingFile(settingsDirectory)) ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static void WritePending(string settingsDirectory, List<string> ids)
    {
        try { SettingsFile.Write(PendingFile(settingsDirectory), ids); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Makes a Claude Code chat that Trayvora started visible in Claude Desktop's Code tab, which lists one
/// local_&lt;id&gt;.json per session in Claude-Data\…\claude-code-sessions\&lt;account&gt;\&lt;org&gt;.
/// The record is copied from the newest existing one, so it has every field Claude Desktop expects.
/// </summary>
internal static class ClaudeDesktopSessions
{
    private static readonly HashSet<Guid> Registered = [];

    /// <summary>Folders holding Claude Desktop session records, newest first.</summary>
    internal static IEnumerable<string> Folders(string? localAppData = null)
    {
        var data = Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
        if (!Directory.Exists(data)) return [];
        return Directory.EnumerateDirectories(data)
            .Select(profile => Path.Combine(profile, "claude-code-sessions"))
            .Where(Directory.Exists)
            .SelectMany(sessions => Directory.EnumerateDirectories(sessions).SelectMany(Directory.EnumerateDirectories))
            .OrderByDescending(Directory.GetLastWriteTimeUtc);
    }

    /// <summary>Adds <paramref name="chat"/> to Claude Desktop unless it is already listed. Returns true if it is listed.</summary>
    public static bool Register(ClaudeChat chat, string? localAppData = null)
    {
        lock (Registered)
        {
            if (Registered.Contains(chat.Id)) return true;
            try
            {
                var folder = Folders(localAppData).FirstOrDefault();
                if (folder is null) return false;
                JsonObject? template = null;
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("local_*.json").OrderByDescending(file => file.LastWriteTimeUtc))
                {
                    if (JsonNode.Parse(File.ReadAllText(file.FullName)) is not JsonObject record) continue;
                    if (record["cliSessionId"]?.GetValue<string>() is { } id && Guid.TryParse(id, out var known) && known == chat.Id)
                    {
                        Registered.Add(chat.Id);
                        return true;
                    }
                    template ??= record;
                }
                if (template is null) return false;
                var created = Record(template, chat, DateTimeOffset.Now);
                File.WriteAllText(Path.Combine(folder, created["sessionId"]!.GetValue<string>() + ".json"), created.ToJsonString());
                Registered.Add(chat.Id);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
            {
                ErrorLog.Write(error);
                return false;
            }
        }
    }

    /// <summary>A session record for <paramref name="chat"/> built from an existing one.</summary>
    internal static JsonObject Record(JsonObject template, ClaudeChat chat, DateTimeOffset now)
    {
        var record = (JsonObject)template.DeepClone();
        var name = Path.GetFileName(Path.GetFullPath(chat.Workspace)) is { Length: > 0 } leaf ? leaf : "Trayvora";
        var stamp = now.ToUnixTimeMilliseconds();
        var sessionId = "local_" + Guid.NewGuid();
        record["sessionId"] = sessionId;
        record["cliSessionId"] = chat.Id.ToString();
        record["cwd"] = chat.Workspace;
        record["originCwd"] = chat.Workspace;
        record["folderPlace"] = new JsonObject { ["cwd"] = chat.Workspace, ["realPath"] = chat.Workspace };
        record["createdAt"] = stamp;
        record["lastActivityAt"] = stamp;
        record["lastFocusedAt"] = stamp;
        record["lastTurnEdgeAt"] = stamp;
        record["isArchived"] = false;
        record["title"] = chat.Title.StartsWith("New Claude chat", StringComparison.Ordinal) ? $"Trayvora · {name}" : chat.Title;
        record["titleSource"] = "user";
        record.Remove("previousTitles");
        record.Remove("gitAnchors");
        record.Remove("gitAnchorsLookupOnly");
        // Values that describe the template's own turns.
        foreach (var key in new[] { "postTurnSummary", "postTurnSummaryFor", "lastAssistantUuid", "latestUserFrameAt", "titleTurn", "promptAppendSnapshot", "toolSurfaceSnapshot", "spawnSeed" })
            record.Remove(key);
        record["completedTurns"] = 1;
        return record;
    }
}
