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
            if (_entries.Count > Limit) _entries.RemoveRange(0, _entries.Count - Limit);
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

    private static List<ActivityEntry> Load(string? file)
    {
        try
        {
            if (file is null || !File.Exists(file)) return [];
            var list = JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(file)) ?? [];
            return list.Count > Limit ? list[^Limit..] : list;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
