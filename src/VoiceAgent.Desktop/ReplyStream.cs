using System.Text;

namespace VoiceAgent.Desktop;

/// <summary>
/// A reply that is still being written. The agent adapters append to it from any thread; the session view reads
/// snapshots on a timer, so a burst of small deltas costs one redraw.
/// </summary>
internal sealed class ReplyStream(CancellationTokenSource? emergency = null)
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Receipt => _received.Task;
    public CancellationToken Emergency => emergency?.Token ?? CancellationToken.None;
    public void Abort() => emergency?.Cancel();
    public string? StopOutcome { get; set; }
    public bool StopConfirmed { get; set; }
    public Guid? PostId { get; init; }
    public Guid? DesktopDeliveryId { get; set; }
    public bool DesktopDeliveryFinished { get; set; }
    public Func<string, CancellationToken, Task>? Steer { get; set; }
    public Func<Task>? BeforeDispatch { get; set; }

    public void ConfirmReceipt() => _received.TrySetResult();
    private readonly List<string> _notes = [];
    private string? _activity;
    private readonly List<string> _steps = [];
    private readonly Dictionary<string, (string Label, string? State, DateTimeOffset Started)> _entries = [];

    public const int StepsShown = 8;
    private int _stepCount;
    private int _version;

    /// <summary>Adds text the agent has just produced.</summary>
    public void Append(string? delta) => Append(delta, confirmReceipt: true);

    public void Append(string? delta, bool confirmReceipt)
    {
        if (string.IsNullOrEmpty(delta)) return;
        if (confirmReceipt) ConfirmReceipt();
        lock (_gate)
        {
            _text.Append(delta);
            _activity = null;
            _version++;
        }
    }

    /// <summary>A new message of the same turn begins (for example after a tool call): earlier narration is dropped.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            if (_text.Length == 0) return;
            _text.Clear();
            _version++;
        }
    }

    /// <summary>The whole reply so far, for sources that report full text rather than deltas.</summary>
    public void Replace(string text)
    {
        if (text.Length > 0) ConfirmReceipt();
        lock (_gate)
        {
            if (_text.Length == text.Length && _text.ToString() == text) return;
            _text.Clear().Append(text);
            if (text.Length > 0) _activity = null;
            _version++;
        }
    }

    /// <summary>Reports on the calling thread (no SynchronizationContext hop), so updates keep their order.</summary>
    public sealed class ActivityReporter(Action<VoiceAgent.Codex.CodexActivity> report) : IProgress<VoiceAgent.Codex.CodexActivity>
    {
        public void Report(VoiceAgent.Codex.CodexActivity value) => report(value);
    }

    public sealed class Reporter(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>What the agent is doing besides writing ("Read · Client.cs"), or null when it is only writing.</summary>
    public void Activity(string? activity) => Activity(activity, record: true);

    public void Activity(string? activity, bool record)
    {
        lock (_gate)
        {
            if (_activity == activity) return;
            _activity = activity;
            if (record && activity is not null) AddStep(activity);
            _version++;
        }
    }

    public void RecordStep(string step)
    {
        lock (_gate)
        {
            AddStep(step);
            _version++;
        }
    }

    public void Observe(VoiceAgent.Codex.CodexActivity entry)
    {
        var label = ErrorLog.Redact(entry.Label);
        if (label.Length > 1800) label = label[..1800] + "…";
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Id, out var previous) && previous.Label == label && previous.State == entry.Status) return;
            if (_entries.Count >= 120 && !_entries.ContainsKey(entry.Id)) _entries.Remove(_entries.Keys.First());
            _entries[entry.Id] = (label, entry.Status, previous.Started == default ? DateTimeOffset.Now : previous.Started);
            _activity = entry.Status == "running" ? label : _entries.Values.LastOrDefault(step => step.State == "running").Label;
            _version++;
        }
    }

    public void FinishActivity(bool stopConfirmed)
    {
        lock (_gate)
        {
            foreach (var (id, entry) in _entries.ToArray())
                if (entry.State == "running") _entries[id] = (entry.Label, stopConfirmed ? "interrupted" : "result unknown", entry.Started);
            _activity = null;
            _version++;
        }
    }

    public string? WorkLog
    {
        get
        {
            lock (_gate)
            {
                if (_entries.Count == 0) return _steps.Count == 0 ? null : FormatWorkLog(_steps, _stepCount);
                var entries = _entries.Values.Select(item => $"{item.Started:HH:mm:ss}  {item.Label}" + (item.State is null ? "" : $" · {item.State}"));
                return string.Join("\n", _steps.Concat(entries));
            }
        }
    }

    public static string FormatWorkLog(IReadOnlyList<string> recent, int total)
    {
        if (recent.Count == 0) return "";
        var log = string.Join("\n", recent);
        return total > recent.Count ? $"… {total - recent.Count} earlier steps\n{log}" : log;
    }

    /// <summary>Something the reader should know about the finished reply, such as tools that were not permitted.</summary>
    public void Note(string note)
    {
        lock (_gate)
        {
            if (!_notes.Contains(note)) _notes.Add(note);
        }
    }

    /// <summary>Adds a step to the visible list; a more detailed label of the same tool replaces the bare one.</summary>
    private void AddStep(string step)
    {
        if (_steps.Count > 0 && (_steps[^1] == step || step.StartsWith(_steps[^1] + " ·", StringComparison.Ordinal)))
            _steps[^1] = step;
        else
        {
            _steps.Add(step);
            _stepCount++;
        }
        if (_steps.Count > 200) _steps.RemoveRange(0, _steps.Count - 200);
    }

    /// <summary>The last steps (oldest first) and how many there were in total.</summary>
    public (IReadOnlyList<string> Recent, int Total) Steps
    {
        get { lock (_gate) return (_steps.Skip(Math.Max(0, _steps.Count - StepsShown)).ToArray(), _stepCount); }
    }

    /// <summary>Formats steps for a muted list: done steps get ✓, the current one an arrow.</summary>
    public static string FormatSteps(IReadOnlyList<string> recent, int total, bool running)
    {
        if (recent.Count == 0) return "";
        var lines = new List<string>();
        if (total > recent.Count) lines.Add($"… {total - recent.Count} earlier steps");
        for (var i = 0; i < recent.Count; i++)
            lines.Add((running && i == recent.Count - 1 ? "▸ " : "✓ ") + recent[i]);
        return string.Join("\n", lines);
    }

    private string? _snapshot;
    private int _snapshotVersion = -1;
    public (string Text, string? Activity, int Version) Snapshot()
    {
        lock (_gate)
        {
            if (_snapshotVersion != _version) { _snapshot = _text.ToString(); _snapshotVersion = _version; }
            return (_snapshot ?? "", _activity, _version);
        }
    }

    public string Text
    {
        get { lock (_gate) return _text.ToString(); }
    }

    public string? Notes
    {
        get { lock (_gate) return _notes.Count == 0 ? null : string.Join(" ", _notes); }
    }
}
