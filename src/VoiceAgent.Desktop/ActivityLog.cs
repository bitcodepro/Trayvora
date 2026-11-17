using System.Text.Json;

namespace VoiceAgent.Desktop;

/// <summary>One line of the Activity page: when, what kind of step, and what happened (never transcripts or answers).</summary>
internal sealed record ActivityEntry(DateTimeOffset Time, string Kind, string Text);

/// <summary>
/// Keeps the last <see cref="Limit"/> shortcut presses and their outcome (recording, transcription, OpenAI request)
/// in settings\logs\activity.json so it is easy to see whether a shortcut fired and what it did. Older entries are dropped.
/// </summary>
internal sealed class ActivityLog
{
    public const int Limit = 30;
    private readonly Lock _gate = new();
    private readonly List<ActivityEntry> _entries;
    private readonly string? _file;

    public ActivityLog(string? file)
    {
        _file = file;
        _entries = Load(file);
    }

    /// <summary>The app-wide log in the settings folder.</summary>
    public static ActivityLog Shared { get; } = new(Path.Combine(
        Environment.GetEnvironmentVariable("VOICEAGENT_SETTINGS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "settings"),
        "logs", "activity.json"));

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event Action? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ActivityEntry> Entries
    {
        get { lock (_gate) return _entries.AsEnumerable().Reverse().ToArray(); }
    }

    public void Add(string kind, string text) => Add(new ActivityEntry(DateTimeOffset.Now, kind, text));

    internal void Add(ActivityEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Trim(_entries);
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_entries));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The kind that starts a press; every later line until the next one belongs to it.</summary>
    public const string PressKind = "Shortcut";

    /// <summary>Upper bound on lines, so steps started without a shortcut (window buttons) cannot grow the file forever.</summary>
    internal const int MaxLines = Limit * 12;

    /// <summary>
    /// Keeps the last <see cref="Limit"/> presses with all their steps: everything before the oldest kept press is dropped,
    /// so a result never loses the press that started it.
    /// </summary>
    internal static void Trim(List<ActivityEntry> entries)
    {
        var presses = 0;
        var oldestKept = -1;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Kind != PressKind) continue;
            if (++presses <= Limit) { oldestKept = i; continue; }
            entries.RemoveRange(0, oldestKept);
            break;
        }
        if (entries.Count <= MaxLines) return;
        var start = entries.Count - MaxLines;
        // Cut on a press boundary when possible so the first kept line still starts a press.
        var boundary = entries.FindIndex(start, entry => entry.Kind == PressKind);
        entries.RemoveRange(0, boundary >= 0 ? boundary : start);
    }

    private static List<ActivityEntry> Load(string? file)
    {
        try
        {
            if (file is null || !File.Exists(file)) return [];
            var list = JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(file)) ?? [];
            Trim(list);
            return list;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
