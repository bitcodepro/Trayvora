using System.Text.Json;

namespace VoiceAgent.Desktop;

/// <param name="Note">Something the reader should know about the reply, such as tools the agent was not allowed to use.</param>
/// <param name="Interrupted">The agent was stopped or failed before it finished; Text is what it had written.</param>
internal sealed record DiscussionMessage(DiscussionMember Sender, DiscussionMember? To, string Text, DateTimeOffset Time, DiscussionMember? ForwardedFrom = null, bool Failed = false, DiscussionMember? RelayTo = null,
    DiscussionMember? DiscussWith = null, bool Final = false, string? Note = null, bool Interrupted = false);

/// <summary>A "discuss with …" exchange: the lead works with the partner and reports the decision to Boss.</summary>
internal sealed record DiscussionPlan(DiscussionMember Lead, DiscussionMember Partner, int ExchangesLeft);

/// <summary>A saved session: a viewer of everything sent between Boss, Codex and Claude, bound to one Codex chat and one Claude session.</summary>
internal sealed class DiscussionRoom
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = DiscussionStore.DefaultTitle;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;

    /// <summary>Codex chat used by this session. Equal to <see cref="Id"/> for the session's own Trayvora thread.</summary>
    public Guid CodexChatId { get; set; }

    /// <summary>True when <see cref="CodexChatId"/> is a Codex Desktop chat rather than a Trayvora Codex chat.</summary>
    public bool CodexDesktop { get; set; }
    public string? CodexChatTitle { get; set; }

    /// <summary>Claude Code session resumed by this discussion.</summary>
    public ClaudeChat? Claude { get; set; }

    /// <summary>What Claude may do without asking: Claude Code's permission mode for this session.</summary>
    public ClaudePermissions ClaudePermissions { get; set; } = ClaudePermissions.Ask;

    /// <summary>Folder for a Codex Desktop chat that is created with the next message (CodexChatId is empty until then).</summary>
    public string? NewCodexFolder { get; set; }

    public List<DiscussionMessage> Messages { get; set; } = [];

    public bool OwnCodexThread => !CodexDesktop && CodexChatId == Id;
    public string Preview => Messages.Count == 0 ? "No messages yet"
        : Messages[^1].Failed ? $"{Messages[^1].Sender} did not answer"
        : Messages[^1].Text.ReplaceLineEndings(" ");
}

internal sealed class DiscussionStore(string directory)
{
    public const string DefaultTitle = "New session";
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directory { get; } = directory;

    public DiscussionRoom Create(ClaudeChat? claude)
    {
        var room = new DiscussionRoom { Claude = claude };
        room.CodexChatId = room.Id;
        Save(room);
        return room;
    }

    public IReadOnlyList<DiscussionRoom> Load()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var rooms = new List<DiscussionRoom>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaxFileBytes) continue;
                var room = JsonSerializer.Deserialize<DiscussionRoom>(stream, Json);
                if (room is null || room.Id != id) continue;
                room.Messages ??= [];
                if (room.CodexChatId == Guid.Empty) room.CodexChatId = room.Id;
                rooms.Add(room);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return rooms.OrderByDescending(room => room.Updated).ToArray();
    }

    public void Save(DiscussionRoom room)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var file = FileFor(room.Id);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(room, Json));
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Delete(Guid id)
    {
        var file = FileFor(id);
        if (File.Exists(file)) File.Delete(file);
    }

    private string FileFor(Guid id) => Path.Combine(Directory, $"{id:N}.json");

    public static string TitleFrom(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 48 ? line : line[..48].TrimEnd() + "…";
    }
}
