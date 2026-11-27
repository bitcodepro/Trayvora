using System.Text;

namespace VoiceAgent.Desktop;

/// <summary>
/// A reply that is still being written. The agent adapters append to it from any thread; the session view reads
/// snapshots on a timer, so a burst of small deltas costs one redraw.
/// </summary>
internal sealed class ReplyStream
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Receipt => _received.Task;

    public void ConfirmReceipt() => _received.TrySetResult();
    private readonly List<string> _notes = [];
    private string? _activity;
    private readonly List<string> _steps = [];
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
            _version++;
        }
    }

    /// <summary>Reports on the calling thread (no SynchronizationContext hop), so updates keep their order.</summary>
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

    public string? WorkLog
    {
        get
        {
            lock (_gate) return _steps.Count == 0 ? null : FormatWorkLog(_steps, _stepCount);
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
