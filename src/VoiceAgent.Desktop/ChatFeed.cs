using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace VoiceAgent.Desktop;

/// <summary>
/// The main window's conversation: your messages and the agent's replies as a feed, newest at the bottom.
/// Each mode or chat keeps its own feed while the app runs; replies are shown as Markdown.
/// </summary>
internal sealed class ChatFeed : UserControl
{
    internal sealed record Entry(string Question, string Answer, string Who, bool Failed = false, string? Workspace = null);

    private const int Shown = 40;
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private readonly Dictionary<string, List<Entry>> _feeds = [];
    private readonly StackPanel _items = new() { Spacing = 14, Margin = new Thickness(2, 4, 10, 8) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Foreground = TrayvoraTheme.Faint, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(20, 40) };
    private readonly DispatcherTimer _throttle = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private List<Entry> _current = [];
    private string _key = "";
    private (Entry Entry, MarkdownView View, TextBlock Waiting, StackPanel Tools, TextBlock Steps)? _live;
    private string _liveText = "";

    /// <summary>Reads a reply aloud. Null hides the button.</summary>
    public Func<string, Task>? Speak { get; set; }

    /// <summary>Puts a reply into the text box as a quote to answer it. Null hides the button.</summary>
    public Action<string, Entry>? OpenFile { get; set; }
    public Action<string>? Reply { get; set; }

    public ChatFeed()
    {
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { _empty, _items } },
        };
        Content = _scroll;
        _throttle.Tick += (_, _) =>
        {
            _throttle.Stop();
            Render();
        };
    }

    public IReadOnlyList<Entry> Entries => _current;
    public bool Live => _live is not null;

    /// <summary>Switches to the feed of another mode or chat. <paramref name="seed"/> fills a feed seen for the first time.</summary>
    public void Use(string key, string emptyHint, Func<IEnumerable<Entry>>? seed = null)
    {
        _empty.Text = emptyHint;
        if (key == _key && _feeds.ContainsKey(key)) { _empty.IsVisible = _current.Count == 0; return; }
        Abandon();
        _key = key;
        if (!_feeds.TryGetValue(key, out var feed))
        {
            feed = [];
            try { if (seed is not null) feed.AddRange(seed()); }
            catch (Exception error) { ErrorLog.Write(error); }
            _feeds[key] = feed;
        }
        _current = feed;
        Rebuild();
    }

    /// <summary>Empties the current feed (a new conversation).</summary>
    public void Clear()
    {
        Abandon();
        _current.Clear();
        Rebuild();
    }

    /// <summary>Shows a sent message and an empty reply from <paramref name="who"/> that fills while it is written.</summary>
    public void Begin(string question, string who, string? workspace = null)
    {
        Finish(null);
        var entry = new Entry(question.Trim(), "", who, Workspace: workspace);
        _current.Add(entry);
        _empty.IsVisible = false;
        if (entry.Question.Length > 0) _items.Children.Add(UserBubble(entry.Question));
        var (bubble, view, waiting, tools, steps) = ReplyBubble(entry);
        _items.Children.Add(bubble);
        _live = (entry, view, waiting, tools, steps);
        _liveText = "";
        ScrollToEnd(force: true);
    }

    /// <summary>The reply so far; drawn at most every 120 ms.</summary>
    public void Update(string? text)
    {
        if (_live is null) return;
        _liveText = text ?? "";
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    /// <summary>Shows what the agent is doing right now (commands, files, searches) in the live reply.</summary>
    public void Steps(string text)
    {
        if (_live is not { } live || (live.Steps.Text ?? "") == text) return;
        var near = NearBottom();
        live.Steps.Text = text;
        live.Steps.IsVisible = text.Length > 0;
        if (near && !live.View.HasSelection) ScrollToEnd(force: true);
    }

    /// <summary>Ends the live reply with its final text, or a note when nothing came back.</summary>
    public void Finish(string? failure)
    {
        if (_live is not { } live) return;
        _throttle.Stop();
        Render();
        var index = _current.IndexOf(live.Entry);
        var answer = _liveText.Trim();
        var done = live.Entry with { Answer = answer.Length > 0 ? _liveText : failure ?? "No reply.", Failed = answer.Length == 0 };
        if (index >= 0) _current[index] = done;
        _live = null;
        live.Waiting.IsVisible = false;
        live.Steps.Text = live.Steps.Text?.Replace("▸ ", "✓ ");
        if (done.Failed)
        {
            live.View.IsVisible = true;
            live.View.Opacity = 0.75;
            live.View.Show(done.Answer);
        }
        FillTools(live.Tools, done, live.View);
        ScrollToEnd(force: false);
    }

    private void Abandon()
    {
        if (_live is not null) Finish("Stopped.");
    }

    private void Render()
    {
        if (_live is not { } live) return;
        var near = NearBottom();
        live.Waiting.IsVisible = _liveText.Length == 0;
        live.View.IsVisible = _liveText.Length > 0;
        live.View.Show(_liveText);
        if (near && !live.View.HasSelection) ScrollToEnd(force: true);
    }

    private void Rebuild()
    {
        _items.Children.Clear();
        _empty.IsVisible = _current.Count == 0;
        foreach (var entry in _current.Skip(Math.Max(0, _current.Count - Shown)))
        {
            if (entry.Question.Length > 0) _items.Children.Add(UserBubble(entry.Question));
            var (bubble, view, waiting, tools, _) = ReplyBubble(entry);
            waiting.IsVisible = false;
            view.IsVisible = true;
            if (entry.Failed) view.Opacity = 0.75;
            view.Show(entry.Answer);
            FillTools(tools, entry, view);
            _items.Children.Add(bubble);
        }
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private bool NearBottom() => _scroll.Extent.Height - _scroll.Offset.Y - _scroll.Viewport.Height < 80;

    private void ScrollToEnd(bool force)
    {
        if (force || NearBottom()) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private static Control UserBubble(string text) => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        MaxWidth = 620,
        Margin = new Thickness(60, 0, 0, 0),
        Background = TrayvoraTheme.AccentSoft,
        CornerRadius = new CornerRadius(16, 16, 4, 16),
        Padding = new Thickness(14, 9),
        Child = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 },
    };

    private (Control Bubble, MarkdownView View, TextBlock Waiting, StackPanel Tools, TextBlock Steps) ReplyBubble(Entry entry)
    {
        var color = entry.Who.StartsWith("Claude", StringComparison.Ordinal) ? TrayvoraTheme.ClaudeColor
            : entry.Who.StartsWith("Codex", StringComparison.Ordinal) ? TrayvoraTheme.CodexColor : TrayvoraTheme.Accent;
        var view = new MarkdownView(14) { IsVisible = false, OpenFile = path => OpenFile?.Invoke(path, entry) };
        var waiting = new TextBlock { Text = "working…", FontSize = 12.5, Foreground = TrayvoraTheme.Muted, FontStyle = FontStyle.Italic };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Left };
        var name = new TextBlock { Text = entry.Who, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = color };
        var steps = new TextBlock { FontSize = 11.5, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false, LineHeight = 17 };
        var body = new StackPanel { Spacing = 6, Children = { name, steps, waiting, view, tools } };
        var bubble = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 40, 0),
            Background = TrayvoraTheme.Surface,
            BorderBrush = TrayvoraTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4, 16, 16, 16),
            Padding = new Thickness(14, 10, 14, 8),
            Child = body,
        };
        bubble.PointerEntered += (_, _) => tools.Opacity = 1;
        bubble.PointerExited += (_, _) => tools.Opacity = 0;
        return (bubble, view, waiting, tools, steps);
    }

    private void FillTools(StackPanel tools, Entry entry, MarkdownView? view = null)
    {
        tools.Children.Clear();
        if (entry.Failed || entry.Answer.Length == 0) return;
        if (view is not null)
            tools.Children.Add(Tool("", "Select plain text across the whole reply (Esc to return)", _ =>
            {
                view.ToggleSelecting();
                return Task.CompletedTask;
            }, "Select"));
        tools.Children.Add(Tool("", "Copy", async button =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, view?.CopyText ?? MarkdownView.ReadableText(entry.Answer));
                ToolTip.SetTip(button, "Copied");
            }
        }));
        if (Reply is { } reply)
            tools.Children.Add(Tool("", "Reply: quote it in the text box to edit and answer", _ =>
            {
                reply($"> {entry.Who} wrote:\n" + string.Join("\n", entry.Answer.Trim().ReplaceLineEndings("\n").Split('\n').Select(line => "> " + line)) + "\n\n");
                return Task.CompletedTask;
            }, "Reply"));
        if (Speak is { } speak)
            tools.Children.Add(Tool("", "Read aloud", _ => speak(entry.Answer)));
        if (entry.Question.Length > 0)
            tools.Children.Add(Tool("", "Copy question", async _ =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, entry.Question);
            }, "Q"));
    }

    private static Button Tool(string glyph, string tip, Func<Button, Task> act, string? badge = null)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        object content = badge is null ? icon : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { icon, new TextBlock { Text = badge, FontSize = 11 } } };
        var button = new Button { Content = content, Padding = new Thickness(7, 3), Cursor = new Cursor(StandardCursorType.Hand) };
        TrayvoraTheme.Ghost(button);
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) =>
        {
            try { await act(button); }
            catch (Exception error) { ErrorLog.Write(error); }
        };
        return button;
    }
}
