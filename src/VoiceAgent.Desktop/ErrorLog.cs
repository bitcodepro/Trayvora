using System.Text.Json;

namespace VoiceAgent.Desktop;

/// <summary>One recorded failure: when, where in the app, what it concerned (chat, session) and the exception.</summary>
internal sealed record ErrorEntry(DateTimeOffset Time, string Where, string? Context, string Type, string Message, string Details);

/// <summary>
/// Errors that happen anywhere in Trayvora, kept for 7 days in settings\logs\errors.jsonl and shown on the Error log
/// page. Only the place, the chat or session name and the exception are stored, never message text or keys.
/// </summary>
internal static class ErrorLog
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    private const long MaxBytes = 8L * 1024 * 1024;
    private const int MaxDetails = 16_000;
    private static readonly Lock Gate = new();
    private static string? _folder;

    /// <summary>Raised (on any thread) after an error is written or the log is cleared.</summary>
    public static event Action? Changed;

    /// <summary>Uses another settings folder (tests); null returns to the default.</summary>
    internal static void UseFolder(string? folder) => _folder = folder;

    public static string File => Path.Combine(
        _folder ?? Environment.GetEnvironmentVariable("VOICEAGENT_SETTINGS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "settings"),
        "logs", "errors.jsonl");

    public static void Write(Exception error, string? where = null, string? context = null)
    {
        if (error is OperationCanceledException) return;
        try
        {
            var details = error.ToString();
            if (details.Length > MaxDetails) details = details[..MaxDetails] + "\n…";
            var entry = new ErrorEntry(DateTimeOffset.Now, where ?? Origin(error), context, error.GetType().Name,
                Innermost(error).Message, details);
            lock (Gate)
            {
                var file = File;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (System.IO.File.Exists(file) && new FileInfo(file).Length > MaxBytes) Rewrite(file, Read(file).Take(200));
                System.IO.File.AppendAllText(file, JsonSerializer.Serialize(entry) + "\n");
            }
            Changed?.Invoke();
        }
        catch (Exception) { }
    }

    /// <summary>Entries from the last 7 days, newest first; older ones are removed from the file.</summary>
    public static IReadOnlyList<ErrorEntry> Entries()
    {
        try
        {
            lock (Gate)
            {
                var file = File;
                if (!System.IO.File.Exists(file)) return [];
                var all = Read(file);
                var kept = all.Where(entry => DateTimeOffset.Now - entry.Time <= KeepFor).ToList();
                if (kept.Count != all.Count) Rewrite(file, kept);
                return kept;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    public static void Clear()
    {
        try { lock (Gate) System.IO.File.Delete(File); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    /// <summary>The entry as plain text, for copying into a bug report.</summary>
    public static string Format(ErrorEntry entry) =>
        $"{entry.Time:yyyy-MM-dd HH:mm:ss zzz} · {entry.Where}" + (entry.Context is { Length: > 0 } c ? $" · {c}" : "") +
        $"\n{entry.Type}: {entry.Message}\n\n{entry.Details}";

    private static List<ErrorEntry> Read(string file)
    {
        var entries = new List<ErrorEntry>();
        foreach (var line in System.IO.File.ReadLines(file))
        {
            if (line.Length == 0) continue;
            try { if (JsonSerializer.Deserialize<ErrorEntry>(line) is { } entry) entries.Add(entry); }
            catch (JsonException) { } // a damaged line is skipped, the rest stays readable
        }
        entries.Reverse();
        return entries;
    }

    private static void Rewrite(string file, IEnumerable<ErrorEntry> newestFirst)
    {
        var lines = newestFirst.Reverse().Select(entry => JsonSerializer.Serialize(entry) + "\n");
        var temp = file + ".tmp";
        System.IO.File.WriteAllText(temp, string.Concat(lines));
        System.IO.File.Move(temp, file, overwrite: true);
    }

    private static Exception Innermost(Exception error)
    {
        // Wrappers that only re-label a failure (for example IOException around a Codex error) keep the useful text inside.
        while (error.InnerException is { } inner && (error is AggregateException || error.Message == inner.Message)) error = inner;
        return error;
    }

    private static string Origin(Exception error) =>
        error.TargetSite?.DeclaringType is { } type ? $"{type.Name}.{error.TargetSite.Name}" : "Trayvora";
}
