using System.Text.RegularExpressions;

namespace VoiceAgent.Desktop;

/// <summary>
/// Where a new chat without a project goes. Codex Desktop puts such chats in
/// &lt;workspaces&gt;\yyyy-MM-dd\new-chat, new-chat-2, …; Trayvora uses the same place so the chat shows up in Codex.
/// </summary>
internal static partial class ChatFolders
{
    /// <summary>The workspaces folder Codex Desktop uses, found from the folders of its recent chats.</summary>
    public static string WorkspacesRoot(string? codexProfile = null)
    {
        var profile = codexProfile;
        if (string.IsNullOrWhiteSpace(profile)) profile = Environment.GetEnvironmentVariable("VOICEAGENT_DESKTOP_CODEX_HOME");
        if (string.IsNullOrWhiteSpace(profile)) profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return FromSessions(Path.Combine(profile, "sessions"))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex", "workspaces");
    }

    internal static string? FromSessions(string sessions)
    {
        try
        {
            if (!Directory.Exists(sessions)) return null;
            var recent = new DirectoryInfo(sessions).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc).Take(60);
            foreach (var file in recent)
            {
                string? first;
                using (var reader = new StreamReader(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                    first = reader.ReadLine();
                if (first is null) continue;
                var match = DatedCwd().Match(first);
                if (match.Success) return Regex.Unescape(match.Groups["root"].Value);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    /// <summary>Creates the next free &lt;root&gt;\yyyy-MM-dd\new-chat[-N] folder.</summary>
    public static string CreateNew(string root, DateTime? today = null)
    {
        var day = Path.Combine(root, (today ?? DateTime.Now).ToString("yyyy-MM-dd"));
        for (var number = 1; ; number++)
        {
            var folder = Path.Combine(day, number == 1 ? "new-chat" : $"new-chat-{number}");
            if (Directory.Exists(folder)) continue;
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    // "cwd":"D:\\CodexData\\workspaces\\2026-11-14\\new-chat-5"
    [GeneratedRegex(@"""cwd""\s*:\s*""(?<root>(?:[^""\\]|\\.)*?)\\\\\d{4}-\d{2}-\d{2}\\\\[^""\\]+""")]
    private static partial Regex DatedCwd();
}
