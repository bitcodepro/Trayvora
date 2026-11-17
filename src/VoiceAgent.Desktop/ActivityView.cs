using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VoiceAgent.Desktop;

/// <summary>Shows the last shortcut presses and what came of them, newest first.</summary>
internal sealed class ActivityView : ToolPage
{
    private readonly ActivityLog _log;
    private readonly StackPanel _list = new() { Spacing = 6 };

    public ActivityView(ActivityLog log)
    {
        _log = log;
        var clear = new Button { Content = "Clear" };
        TrayvoraTheme.StyleControls(clear);
        TrayvoraTheme.Ghost(clear);
        clear.Click += (_, _) => _log.Clear();
        Content = Frame("Activity",
            $"The last {ActivityLog.Limit} shortcut presses and what happened: recording, recognized speech, OpenAI requests. Older entries are removed.",
            new StackPanel { Spacing = 12, Children = { clear, _list } });
        _log.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        _list.Children.Clear();
        var entries = _log.Entries;
        if (entries.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "Nothing yet. Press a shortcut and it shows up here.", Foreground = TrayvoraTheme.Muted });
            return;
        }
        foreach (var entry in entries)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 12 };
            var time = new TextBlock { Text = entry.Time.ToString("dd.MM HH:mm:ss"), Foreground = TrayvoraTheme.Muted, FontFamily = new FontFamily("Consolas") };
            var kind = new TextBlock { Text = entry.Kind, FontWeight = FontWeight.SemiBold, MinWidth = 90 };
            var text = new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(kind, 1);
            Grid.SetColumn(text, 2);
            row.Children.AddRange([time, kind, text]);
            _list.Children.Add(row);
        }
    }
}
