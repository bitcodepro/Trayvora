using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;

namespace VoiceAgent.Desktop;

/// <summary>Errors from the last 7 days, newest first, each with where it happened, the chat and the full details.</summary>
internal sealed class ErrorLogView : ToolPage
{
    private readonly StackPanel _list = new() { Spacing = 8 };

    public ErrorLogView()
    {
        var clear = new Button { Content = "Clear" };
        var copy = new Button { Content = "Copy all" };
        TrayvoraTheme.StyleControls(clear, copy);
        TrayvoraTheme.Ghost(clear);
        TrayvoraTheme.Ghost(copy);
        clear.Click += (_, _) => ErrorLog.Clear();
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(string.Join("\n\n----\n\n", ErrorLog.Entries().Select(ErrorLog.Format)));
        };
        Content = Frame("Error log",
            "Errors from anywhere in Trayvora during the last 7 days: when, where, which chat or session, and the details. " +
            "Older entries are removed. Message text and keys are not recorded.",
            new StackPanel
            {
                Spacing = 12,
                Children = { new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { copy, clear } }, _list },
            });
        ErrorLog.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        _list.Children.Clear();
        var entries = ErrorLog.Entries();
        if (entries.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No errors in the last 7 days.", Foreground = TrayvoraTheme.Muted });
            return;
        }
        foreach (var entry in entries)
        {
            var details = new SelectableTextBlock
            {
                Text = entry.Details, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Foreground = TrayvoraTheme.Muted,
            };
            var copy = new Button { Content = "Copy", Padding = new Thickness(8, 2), MinHeight = 0 };
            TrayvoraTheme.StyleControls(copy);
            TrayvoraTheme.Ghost(copy);
            var current = entry;
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(ErrorLog.Format(current));
            };
            var header = new TextBlock
            {
                Text = $"{entry.Time:dd.MM HH:mm:ss} · {entry.Where}" + (entry.Context is { Length: > 0 } c ? $" · {c}" : ""),
                Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap,
            };
            _list.Children.Add(new Border
            {
                Background = TrayvoraTheme.Elevated, BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { header, Place(copy, 1) } },
                        new SelectableTextBlock { Text = $"{entry.Type}: {entry.Message}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new Expander { Header = "Details", Content = details },
                    },
                },
            });
        }
    }

    private static Control Place(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
