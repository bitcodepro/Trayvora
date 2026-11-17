using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VoiceAgent.Desktop;

/// <summary>One thing Trayvora needs, whether it is ready, and how to fix it.</summary>
internal sealed record ReadinessItem(string Title, string Detail, bool Ready, bool Optional = false, string? FixLabel = null, Func<Task>? Fix = null);

/// <summary>
/// "Get started": what is ready and what is missing (microphone, speech model, sign-ins, API key),
/// every shortcut, and the session key phrases — everything a new user needs on one page.
/// </summary>
internal sealed class ReadinessView : ToolPage
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private readonly Func<Task<IReadOnlyList<ReadinessItem>>> _check;
    private readonly StackPanel _items = new() { Spacing = 8 };
    private readonly TextBlock _summary = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _shortcuts = new() { Spacing = 6 };
    private bool _checking;

    private readonly TextBlock _updateStatus = TrayvoraTheme.Hint("");
    private readonly Button _install = new() { IsVisible = false };
    private AvailableUpdate? _available;

    public ReadinessView(Func<Task<IReadOnlyList<ReadinessItem>>> check, Func<Task<string?>>? update = null,
        Func<Task<AvailableUpdate?>>? checkOnline = null, Func<AvailableUpdate, IProgress<string>, Task<string?>>? install = null)
    {
        _check = check;
        var recheck = new Button { Content = "Check again" };
        TrayvoraTheme.StyleControls(recheck);
        recheck.Click += async (_, _) => await RefreshAsync();
        var phrases = new StackPanel { Spacing = 6, Children =
        {
            TrayvoraTheme.Hint("Write or say these anywhere in a session message:"),
            Phrase("Forward the reply", string.Join(" · ", RelayPhrases.Examples.Take(5))),
            Phrase("Agents discuss, lead reports to you", string.Join(" · ", RelayPhrases.DiscussExamples.Take(4))),
            Phrase("Chips", "Codex / Claude / Both choose who gets the message; ⇄ forwards the reply to the other agent."),
        } };
        var body = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                TrayvoraTheme.Card(TrayvoraTheme.Heading("Readiness"), _summary, _items, recheck),
                TrayvoraTheme.Card(TrayvoraTheme.Heading("Keyboard shortcuts"), TrayvoraTheme.Hint("Global shortcuts work from any app. Change them in Settings."), _shortcuts),
                TrayvoraTheme.Card(TrayvoraTheme.Heading("Session key phrases"), phrases),
            },
        };
        if (update is not null)
        {
            var current = GitHubUpdate.CurrentVersion();
            _updateStatus.Text = (current is null ? "Version: development build. " : $"Version {current}. ")
                + "Updating keeps settings, history, sessions, models and sign-ins; old program files are replaced.";
            var updateButton = new Button { Content = "Update from zip…" };
            var checkButton = new Button { Content = "Check for updates", IsVisible = checkOnline is not null };
            TrayvoraTheme.StyleControls(updateButton, checkButton, _install);
            TrayvoraTheme.Primary(_install);
            updateButton.Click += async (_, _) =>
            {
                SetUpdating(true);
                try { if (await update() is { } message) _updateStatus.Text = message; }
                finally { SetUpdating(false); }
            };
            checkButton.Click += async (_, _) =>
            {
                if (checkOnline is null) return;
                SetUpdating(true);
                _updateStatus.Text = "Checking GitHub…";
                try
                {
                    if (await checkOnline() is { } found) ShowAvailable(found);
                    else _updateStatus.Text = $"You have the latest version{(current is null ? "" : $" ({current})")}.";
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    _updateStatus.Text = $"Could not check for updates: {error.Message}";
                }
                finally { SetUpdating(false); }
            };
            _install.Click += async (_, _) =>
            {
                if (install is null || _available is not { } chosen) return;
                SetUpdating(true);
                try
                {
                    var progress = new Progress<string>(text => _updateStatus.Text = text);
                    if (await install(chosen, progress) is { } message) _updateStatus.Text = message;
                }
                finally { SetUpdating(false); }
            };
            void SetUpdating(bool busy)
            {
                foreach (var button in new[] { _install, checkButton, updateButton }) button.IsEnabled = !busy;
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _install, checkButton, updateButton } };
            body.Children.Insert(0, TrayvoraTheme.Card(TrayvoraTheme.Heading("Update"), _updateStatus, buttons));
        }
        Content = Frame("Get started", "What Trayvora needs, and how to use it", body);
    }

    /// <summary>Offers <paramref name="update"/> with an Install button.</summary>
    public void ShowAvailable(AvailableUpdate update)
    {
        _available = update;
        _install.Content = $"Install {update.Version}";
        _install.IsVisible = true;
        var current = GitHubUpdate.CurrentVersion();
        _updateStatus.Text = $"Trayvora {update.Version} is available{(current is null ? "" : $" (you have {current})")}. "
            + "Install downloads it from GitHub, restarts Trayvora and keeps all your settings and data.";
    }

    /// <summary>Shows the global shortcuts followed by the window's own keys.</summary>
    public void SetShortcuts(IEnumerable<(string Name, string Keys)> global)
    {
        _shortcuts.Children.Clear();
        foreach (var (name, keys) in global.Concat([("Send message", "Ctrl+Enter"), ("Cancel, hide or go back", "Esc")]))
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
            var keyText = new Border
            {
                Background = TrayvoraTheme.Elevated, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = keys, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 },
            };
            Grid.SetColumn(keyText, 1);
            grid.Children.AddRange([new TextBlock { Text = name, Foreground = TrayvoraTheme.Muted, VerticalAlignment = VerticalAlignment.Center }, keyText]);
            _shortcuts.Children.Add(grid);
        }
    }

    public async Task RefreshAsync()
    {
        if (_checking) return;
        _checking = true;
        _summary.Text = "Checking…";
        _summary.Foreground = TrayvoraTheme.Muted;
        try
        {
            var items = await _check();
            _items.Children.Clear();
            foreach (var item in items) _items.Children.Add(Row(item));
            var missing = items.Count(item => !item.Ready && !item.Optional);
            _summary.Text = missing == 0 ? "Everything needed is ready." : $"{missing} {(missing == 1 ? "thing needs" : "things need")} attention.";
            _summary.Foreground = missing == 0 ? TrayvoraTheme.Success : TrayvoraTheme.Warning;
        }
        catch (Exception error)
        {
            ErrorLog.Write(error);
            _summary.Text = "Could not check everything.";
        }
        finally { _checking = false; }
    }

    private Control Row(ReadinessItem item)
    {
        var color = item.Ready ? TrayvoraTheme.Success : item.Optional ? TrayvoraTheme.Muted : TrayvoraTheme.Warning;
        var icon = new TextBlock
        {
            Text = item.Ready ? "" : item.Optional ? "" : "", FontFamily = IconFont, FontSize = 16,
            Foreground = color, VerticalAlignment = VerticalAlignment.Center, Width = 22,
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = item.Detail, Foreground = TrayvoraTheme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap },
        } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(text, 1);
        grid.Children.AddRange([icon, text]);
        if (item.Fix is { } fix && !item.Ready)
        {
            var button = new Button { Content = item.FixLabel ?? "Fix", VerticalAlignment = VerticalAlignment.Center };
            if (item.Optional) TrayvoraTheme.StyleControls(button); else TrayvoraTheme.Primary(button);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await fix(); }
                catch (Exception error) { ErrorLog.Write(error); }
                finally { button.IsEnabled = true; }
                await RefreshAsync();
            };
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }
        return new Border
        {
            Background = TrayvoraTheme.Elevated, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Child = grid,
        };
    }

    private static Control Phrase(string title, string examples) => new StackPanel
    {
        Spacing = 1,
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12.5 },
            new TextBlock { Text = examples, Foreground = TrayvoraTheme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap },
        },
    };
}
