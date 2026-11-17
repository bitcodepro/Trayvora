using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VoiceAgent.Infrastructure;

namespace VoiceAgent.Desktop;

internal sealed class HistoryWindow : Window
{
    private readonly RequestHistory _history;
    private readonly ListBox _items = new() { Height = 170 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _prompt = ReadOnlyBox(78);
    private readonly TextBox _reply = ReadOnlyBox(150);
    private readonly TextBox _tools = ReadOnlyBox(85);
    private readonly Button _delete = new() { Content = "Delete selected entry", IsEnabled = false };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly CheckBox _allChats = new() { Content = "Show requests from all chats" };
    private Guid _chatId;
    private bool _loading;
    private int _selectionVersion;
    private int _reloadVersion;

    public HistoryWindow(RequestHistory history)
    {
        _history = history;
        Title = "Trayvora · History";
        Width = 680;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _items.SelectionChanged += async (_, _) =>
        {
            if (!_loading) await ShowSelectionAsync();
        };
        _refresh.Click += async (_, _) => await ReloadAsync();
        _allChats.IsCheckedChanged += async (_, _) => await ReloadAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                Close();
                args.Handled = true;
            }
        };

        Title = "Trayvora · History";
        Width = 1000;
        Height = 680;
        MinWidth = 640;
        MinHeight = 460;
        TrayvoraTheme.Apply(this);
        TrayvoraTheme.StyleControls(_refresh, _delete, _prompt, _reply, _tools);
        TrayvoraTheme.DangerButton(_delete);
        _status.Foreground = TrayvoraTheme.Muted;
        _status.FontSize = 12;
        _items.Height = double.NaN;
        _items.Background = Brushes.Transparent;
        _prompt.Height = double.NaN;
        _prompt.MinHeight = 70;
        _reply.Height = double.NaN;
        _reply.MinHeight = 200;
        _tools.Height = double.NaN;
        _tools.MinHeight = 60;

        var listHeader = new StackPanel { Spacing = 10, Children =
        {
            new TextBlock { Text = "History", FontSize = 20, FontWeight = FontWeight.SemiBold },
            _allChats,
            TrayvoraTheme.Caption("Recent requests"),
        } };
        var listButtons = TrayvoraTheme.Row(_refresh, _delete);
        var list = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 10 };
        Grid.SetRow(_items, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(listButtons, 3);
        list.Children.AddRange([listHeader, _items, _status, listButtons]);
        var sidebar = new Border
        {
            Width = 320, Background = TrayvoraTheme.Sidebar, BorderBrush = TrayvoraTheme.Border,
            BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(16, 18), Child = list,
        };
        var detail = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Margin = new Thickness(24, 20),
                Spacing = 14,
                Children =
                {
                    TrayvoraTheme.Card(new Label { Content = "Request", Target = _prompt }, _prompt),
                    TrayvoraTheme.Card(new Label { Content = "Saved response", Target = _reply }, _reply),
                    TrayvoraTheme.Card(new Label { Content = "Tool activity", Target = _tools }, _tools),
                },
            },
        };
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(detail, 1);
        shell.Children.AddRange([sidebar, detail]);
        Content = shell;
    }

    public Task SetChatAsync(Guid chatId)
    {
        _chatId = chatId;
        return ReloadAsync();
    }

    public async Task ReloadAsync(Guid? select = null)
    {
        var version = ++_reloadVersion;
        _loading = true;
        _refresh.IsEnabled = false;
        _delete.IsEnabled = false;
        try
        {
            select ??= (_items.SelectedItem as RequestSummary)?.Id;
            var chatId = _allChats.IsChecked == true ? (Guid?)null : _chatId;
            var entries = await Task.Run(() => _history.Recent(chatId));
            if (version != _reloadVersion) return;
            _items.ItemsSource = entries;
            _items.SelectedItem = entries.FirstOrDefault(item => item.Id == select) ?? entries.FirstOrDefault();
            await ShowSelectionAsync();
            if (entries.Count == 0 && version == _reloadVersion)
            {
                _status.Text = "No saved requests yet.";
                _prompt.Text = _reply.Text = _tools.Text = "";
            }
        }
        catch (Exception)
        {
            if (version == _reloadVersion)
                _status.Text = "History is unavailable. Check the history folder and access permissions.";
        }
        finally
        {
            if (version == _reloadVersion)
            {
                _loading = false;
                _refresh.IsEnabled = true;
                _delete.IsEnabled = _items.SelectedItem is RequestSummary { Status: not RequestStatus.Running };
            }
        }
    }

    private async Task ShowSelectionAsync()
    {
        var version = ++_selectionVersion;
        _prompt.Text = _reply.Text = _tools.Text = "";
        _delete.IsEnabled = false;
        if (_items.SelectedItem is not RequestSummary summary) return;

        try
        {
            var entry = await Task.Run(() => _history.Find(summary.Id));
            if (version != _selectionVersion) return;
            if (entry is null)
            {
                _status.Text = "This request is no longer in history.";
                return;
            }

            _status.Text = $"{entry.Summary.Status} · {entry.Summary.Started.ToLocalTime():g}"
                + (entry.Finished is { } end ? $" → {end.ToLocalTime():g}" : "")
                + (entry.Detail is null ? "" : $" · {entry.Detail}");
            _prompt.Text = entry.Prompt;
            _reply.Text = entry.Answer;
            _tools.Text = entry.ToolSummary.Length == 0 ? "No tool activity recorded." : entry.ToolSummary;
            _delete.IsEnabled = !_loading && entry.Summary.Status != RequestStatus.Running;
        }
        catch (Exception)
        {
            if (version == _selectionVersion) _status.Text = "Could not open this request.";
        }
    }

    private async Task DeleteAsync()
    {
        if (_loading || _items.SelectedItem is not RequestSummary summary) return;
        _delete.IsEnabled = false;
        try
        {
            await Task.Run(() => _history.Delete(summary.Id));
            await ReloadAsync();
        }
        catch (Exception)
        {
            _status.Text = "Could not delete this request.";
            _delete.IsEnabled = true;
        }
    }

    private static TextBox ReadOnlyBox(double height) => new()
    {
        Height = height, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
    };
}
