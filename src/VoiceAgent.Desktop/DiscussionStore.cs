using System.Text.Json;

namespace VoiceAgent.Desktop;

/// <param name="Note">Something the reader should know about the reply, such as tools the agent was not allowed to use.</param>
/// <param name="Interrupted">The agent was stopped or failed before it finished; Text is what it had written.</param>
internal sealed record DiscussionMessage(DiscussionMember Sender, DiscussionMember? To, string Text, DateTimeOffset Time, DiscussionMember? ForwardedFrom = null, bool Failed = false, DiscussionMember? RelayTo = null,
    DiscussionMember? DiscussWith = null, bool Final = false, string? Note = null, bool Interrupted = false, string? Workspace = null, Guid? ThreadId = null, bool DesktopThread = false, Guid? DeliveryId = null, string? DeliveryProfile = null, string? Command = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? McpProfile = null);

/// <summary>A "discuss with …" exchange: the lead works with the partner and reports the decision to Boss.</summary>
internal sealed record DiscussionPlan(DiscussionMember Lead, DiscussionMember Partner, int ExchangesLeft);

/// <summary>A saved session: a viewer of everything sent between Boss, Codex and Claude, bound to one Codex chat and one Claude session.</summary>
internal sealed class DiscussionRoom
{
    [System.Text.Json.Serialization.JsonRequired]
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
    public string? CodexWorkspace { get; set; }
    public string? NewCodexFolder { get; set; }

    public string? ClaudeEffort { get; set; }
    public string? ClaudeModel { get; set; }

    public List<DiscussionMessage> Messages { get; set; } = [];

    public bool OwnCodexThread => !CodexDesktop && CodexChatId == Id;
    internal string? IndexedPreview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Preview => IndexedPreview ?? (Messages.Count == 0 ? "No messages yet"
        : Messages[^1].Failed ? $"{Messages[^1].Sender} did not answer"
        : Messages[^1].Text[..Math.Min(160, Messages[^1].Text.Length)].ReplaceLineEndings(" "));
}

internal sealed class DiscussionStore(string directory)
{
    public const string DefaultTitle = "New session";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    // Reading a session while it is replaced fails on Windows, so reads and writes in this app take turns.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lock> Gates = new(StringComparer.OrdinalIgnoreCase);
    private Lock Gate => Gates.GetOrAdd(Path.GetFullPath(Directory), _ => new Lock());
    internal const int Attempts = 8;

    public string Directory { get; } = directory;

    public DiscussionRoom Create(ClaudeChat? claude)
    {
        var room = new DiscussionRoom { Claude = claude };
        room.CodexChatId = room.Id;
        Save(room);
        return room;
    }

    private sealed record Header(Guid Id, string Title, DateTimeOffset Updated, string Preview, long Size, long Stamp);
    private string HeaderFile(Guid id) => Path.Combine(Directory, "index", $"{id:N}.json");

    private void Index(DiscussionRoom room, FileInfo file)
    {
        try
        {
            var header = new Header(room.Id, room.Title, room.Updated, room.Preview, file.Length, file.LastWriteTimeUtc.Ticks);
            SettingsFile.Write(HeaderFile(room.Id), header);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Write(error, "Indexing session"); }
    }

    public DiscussionRoom ReadRoom(Guid id)
    {
        lock (Gate)
        {
            using var stream = new FileStream(FileFor(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var room = JsonSerializer.Deserialize<DiscussionRoom>(stream, Json);
            if (room?.Id != id || id == Guid.Empty) throw new JsonException("Session ID does not match its filename.");
            room.Messages ??= [];
            if (room.CodexDesktop && room.NewCodexFolder is not null && room.CodexChatId == room.Id) room.CodexChatId = Guid.Empty;
            else if (room.CodexChatId == Guid.Empty && !room.CodexDesktop) room.CodexChatId = room.Id;
            return room;
        }
    }

    public List<DiscussionRoom> ListHeaders()
    {
        lock (Gate)
        {
            var rows = new List<DiscussionRoom>();
            if (!System.IO.Directory.Exists(Directory)) return rows;
            foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out var id)) continue;
                try
                {
                    Header? header = null;
                    try { header = SettingsFile.Read<Header>(HeaderFile(id)); }
                    catch (JsonException) { }
                    if (header is not null && header.Id == id && header.Size == file.Length && header.Stamp == file.LastWriteTimeUtc.Ticks)
                        rows.Add(new DiscussionRoom { Id = id, Title = header.Title, Updated = header.Updated, IndexedPreview = header.Preview });
                    else
                    {
                        var room = ReadRoom(id);
                        Index(room, file);
                        rows.Add(new DiscussionRoom { Id = id, Title = room.Title, Updated = room.Updated, IndexedPreview = room.Preview });
                    }
                }
                catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
                { ErrorLog.Write(error, "Reading session header"); }
            }
            return rows.OrderByDescending(row => row.Updated).ToList();
        }
    }

    public IReadOnlyList<DiscussionRoom> Load()
    {
        lock (Gate) return LoadLocked();
    }

    private IReadOnlyList<DiscussionRoom> LoadLocked()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var rooms = new List<DiscussionRoom>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;
            try
            {
                var room = Retry(() => ReadRoom(id));
                rooms.Add(room);
            }
            catch (FileNotFoundException) { } // deleted meanwhile
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                ErrorLog.Write(error, "Sessions · load", $"file {Path.GetFileName(file)}");
            }
        }
        return rooms.OrderByDescending(room => room.Updated).ToArray();
    }

    /// <summary>Writes the session through a temporary file; throws if it could not be saved after several tries.</summary>
    public void Save(DiscussionRoom room)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var file = FileFor(room.Id);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, room, Json);
            lock (Gate) { Retry(() => { File.Move(temporary, file, overwrite: true); return true; }); Index(room, new FileInfo(file)); }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Delete(Guid id)
    {
        var file = FileFor(id);
        lock (Gate)
        {
            Retry(() => { File.Delete(file); return true; });
            if (File.Exists(HeaderFile(id))) File.Delete(HeaderFile(id));
            var root = Path.Combine(Directory, "attachments");
            var folder = Path.Combine(root, id.ToString("N"));
            if (!System.IO.Directory.Exists(folder) || new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)
                || new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            var otherRooms = LoadLocked();
            foreach (var copy in System.IO.Directory.EnumerateFiles(folder))
            {
                if (!otherRooms.Any(room => room.Messages.Any(message => message.Text.Contains(copy, StringComparison.OrdinalIgnoreCase))))
                    File.Delete(copy);
            }
            if (!System.IO.Directory.EnumerateFileSystemEntries(folder).Any()) System.IO.Directory.Delete(folder);
        }
    }

    /// <summary>Runs <paramref name="action"/> again while the file is briefly locked by someone else.</summary>
    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return action(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                && error is not FileNotFoundException and not DirectoryNotFoundException && attempt < Attempts)
            {
                Thread.Sleep(15 * attempt);
            }
        }
    }

    private string FileFor(Guid id) => Path.Combine(Directory, $"{id:N}.json");

    public static string TitleFrom(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 48 ? line : line[..48].TrimEnd() + "…";
    }
}
