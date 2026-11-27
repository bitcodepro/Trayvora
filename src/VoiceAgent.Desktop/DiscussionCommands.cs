using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace VoiceAgent.Desktop;

internal sealed partial class DiscussionWindow
{
    private readonly ToggleButton _commandMode = new() { Content = "/  Command mode", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _commandInput = new() { PlaceholderText = "Type / for Claude CLI commands", MaxLength = 256 };
    private readonly ListBox _commandChoices = new() { MaxHeight = 168, IsVisible = false };
    private readonly Button _runCommand = new() { Content = "Run command" };
    private IReadOnlyList<ClaudeChat> _resumeChoices = [];
    private CancellationTokenSource? _commandCancel;
    private string? _suggestionsFor;
    private bool _commandEnterDown;
    internal Func<string[], string, CancellationToken, Task<(int Code, string Output)>>? RunClaudeCommand { get; set; }

    private static string CommandWorkspace(DiscussionRoom? room) =>
        room?.Claude?.Workspace ?? room?.NewCodexFolder ?? room?.CodexWorkspace ?? AppContext.BaseDirectory;

    private void OpenCommandConsole(string? workspace = null)
    {
        try { openTerminal?.Invoke(workspace ?? CommandWorkspace(_room)); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { _status.Text = "Could not open the terminal: " + error.Message; }
    }

    private Control BuildCommandInput(Control messages)
    {
        TrayvoraTheme.StyleControls(_commandMode, _commandInput);
        TrayvoraTheme.Primary(_runCommand);
        ToolTip.SetTip(_commandInput, "Enter inserts a suggestion. Enter again runs the command.");
        Avalonia.Automation.AutomationProperties.SetName(_commandMode, "Claude command mode");
        Avalonia.Automation.AutomationProperties.SetName(_commandChoices, "Claude command suggestions");
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(_runCommand, 1);
        line.Children.AddRange([_commandInput, _runCommand]);
        var panel = new StackPanel { IsVisible = false, Spacing = 6, Children = { _commandChoices, line } };
        if (openTerminal is not null)
        {
            var console = new Button { Content = "Claude console", HorizontalAlignment = HorizontalAlignment.Left };
            TrayvoraTheme.Ghost(console);
            ToolTip.SetTip(console, "Open interactive Claude Code in this session’s workspace.");
            console.Click += (_, _) => OpenCommandConsole();
            panel.Children.Add(console);
        }
        _commandMode.IsCheckedChanged += (_, _) =>
        {
            var enabled = _commandMode.IsChecked == true;
            if (enabled && _room is null)
            {
                NewRoom();
                _commandMode.IsChecked = _room is not null;
                if (_room is null) return;
            }
            panel.IsVisible = enabled;
            messages.IsVisible = !enabled;
            _commandCancel?.Cancel();
            _resumeChoices = [];
            _commandInput.Clear();
            _status.Text = "";
            UpdateComposer();
            if (enabled) _commandInput.Focus(); else _editor.Focus();
        };
        _commandInput.TextChanged += (_, _) => RefreshCommandSuggestions();
        _commandInput.AddHandler(KeyDownEvent, CommandKeyDown, RoutingStrategies.Tunnel);
        _commandChoices.AddHandler(KeyDownEvent, CommandKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            owner.AddHandler(KeyUpEvent, CommandKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
            owner.Deactivated += ResetCommandEnter;
        };
        DetachedFromVisualTree += (_, e) =>
        {
            if (e.RootVisual is Window owner)
            {
                owner.RemoveHandler(KeyUpEvent, CommandKeyUp);
                owner.Deactivated -= ResetCommandEnter;
            }
            _commandEnterDown = false;
        };
        _commandChoices.Tapped += (_, e) =>
        {
            if (e.Source is Control source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Content is CommandChoice choice)
                InsertCommand(choice);
        };
        _runCommand.Click += async (_, _) => await ExecuteCommandAsync(_commandInput.Text);
        return panel;
    }

    private void ResetCommandEnter(object? sender, EventArgs args) => _commandEnterDown = false;

    private void CommandKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _commandEnterDown = false;
    }

    private async void CommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.Enter)) return;
        RefreshCommandSuggestions();
        var count = _commandChoices.Items.Count;
        if (e.Key is Key.Up or Key.Down)
        {
            if (count == 0) return;
            e.Handled = true;
            var current = _commandChoices.SelectedIndex;
            var next = e.Key == Key.Down ? current + 1 : current < 0 ? count - 1 : current - 1;
            _commandChoices.SelectedIndex = Math.Clamp(next, 0, count - 1);
            _commandChoices.ScrollIntoView(_commandChoices.SelectedIndex);
            return;
        }

        e.Handled = true;
        if (_commandEnterDown) return;
        _commandEnterDown = true;
        var choice = _commandChoices.SelectedItem as CommandChoice
            ?? (count == 1 ? _commandChoices.Items[0] as CommandChoice : null);
        if (choice is not null) InsertCommand(choice);
        else await ExecuteCommandAsync(_commandInput.Text);
    }

    private void RefreshCommandSuggestions()
    {
        if (_suggestionsFor != _commandInput.Text) SuggestCommands();
    }

    private void InsertCommand(CommandChoice choice)
    {
        _commandInput.Text = choice.Command;
        _commandInput.Focus();
        _commandInput.ClearSelection();
        _commandInput.CaretIndex = choice.Command.Length;
        _status.Text = "";
        SuggestCommands();
    }

    private void SuggestCommands()
    {
        _suggestionsFor = _commandInput.Text;
        var input = (_suggestionsFor ?? "").TrimStart();
        IEnumerable<CommandChoice> choices = [];
        if (input.StartsWith('/'))
        {
            var parts = input.Split(' ', 2);
            var command = parts[0].ToLowerInvariant();
            var filter = parts.Length == 2 ? parts[1].Trim() : "";
            choices = parts.Length == 1
                ? ClaudeCommands.Menu.Where(item => item.Command.StartsWith(command, StringComparison.Ordinal))
                : command switch
                {
                    "/effort" or "/model" => (command == "/effort" ? ClaudeCommands.Efforts : ClaudeCommands.Models)
                        .Where(value => value.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                        .Select(value => new CommandChoice($"{command} {value}", value == "auto" ? "CLI default" : "")),
                    "/resume" => _resumeChoices.Where(chat => Matches(chat.Title, filter) || chat.Id.ToString().StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                        .Select(chat => new CommandChoice($"/resume {chat.Id}", chat.Title)),
                    _ => []
                };
        }
        var rows = choices.Where(choice => !choice.Command.Equals(input.TrimEnd(), StringComparison.OrdinalIgnoreCase)).ToArray();
        _commandChoices.ItemsSource = rows;
        _commandChoices.SelectedIndex = -1;
        _commandChoices.IsVisible = rows.Length > 0;
    }

    private async Task ExecuteCommandAsync(string? input)
    {
        if (_commandMode.IsChecked != true || _room is not { } room || CurrentRun is not null || _commandCancel is not null) return;
        var parts = (input ?? "").Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        var name = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        if (!ClaudeCommands.Menu.Any(item => item.Command == name))
        {
            _status.Text = "Choose a command from the / menu.";
            return;
        }
        if (arg.Length > 0 && name is not ("/resume" or "/model" or "/effort"))
        {
            _status.Text = $"{name} takes no arguments.";
            return;
        }

        using var operation = new CancellationTokenSource();
        _commandCancel = operation;
        _runCommand.IsEnabled = _commandInput.IsEnabled = _commandChoices.IsEnabled = false;
        UpdateComposer();
        _status.Text = $"Running {name}…";
        string nextInput = "/";
        var workspace = CommandWorkspace(room);
        try
        {
            string result;
            if (name == "/resume")
            {
                var chats = await claudeChats();
                operation.Token.ThrowIfCancellationRequested();
                _resumeChoices = chats;
                if (Guid.TryParse(arg, out var id) && chats.FirstOrDefault(chat => chat.Id == id) is { } selected)
                {
                    room.Claude = selected;
                    _claudeItems = chats;
                    FillClaudeChoices();
                    UpdateHeader();
                    result = $"Selected {selected.Title}\n{selected.Workspace}\n{selected.Id}";
                }
                else
                {
                    nextInput = "/resume " + arg;
                    result = chats.Count == 0 ? "No saved Claude chats in this profile." : "Choose a Claude chat below. Type part of its name to filter.";
                }
            }
            else if (name is "/effort" or "/model")
            {
                var effort = name == "/effort";
                var choices = effort ? ClaudeCommands.Efforts : ClaudeCommands.Models;
                var value = arg.ToLowerInvariant();
                if (value.Length > 0 && !choices.Contains(value))
                {
                    _status.Text = $"Choose one of: {string.Join(", ", choices)}.";
                    return;
                }
                if (value.Length == 0) nextInput = name + " ";
                else if (effort) room.ClaudeEffort = value == "auto" ? null : value;
                else room.ClaudeModel = value == "auto" ? null : value;
                result = $"Claude {(effort ? "effort" : "model")} for this Trayvora session: {(effort ? room.ClaudeEffort : room.ClaudeModel) ?? "CLI default"}.";
            }
            else if (name == "/help") result = string.Join("\n", ClaudeCommands.Menu.AsEnumerable());
            else
            {
                var args = ClaudeCommands.Arguments(name) ?? throw new InvalidOperationException("Unknown command.");
                var runner = RunClaudeCommand ?? throw new IOException("Claude CLI is unavailable.");
                var (code, output) = await runner(args, workspace, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                result = ClaudeCommands.Output(name, code, output);
            }
            var label = arg.Length == 0 || name == "/resume" && !Guid.TryParse(arg, out _) ? name : $"{name} {arg}";
            _status.Text = "";
            await Append(room, new(DiscussionMember.Claude, DiscussionMember.Boss, result, DateTimeOffset.Now, Workspace: name == "/mcp" ? workspace : null, Command: label));
            if (!operation.IsCancellationRequested)
            {
                _commandInput.Text = nextInput;
                _commandInput.CaretIndex = nextInput.Length;
                SuggestCommands();
            }
        }
        catch (OperationCanceledException)
        {
            if (!operation.IsCancellationRequested)
            {
                _status.Text = "";
                await Append(room, new(DiscussionMember.Claude, DiscussionMember.Boss, "Claude CLI timed out.", DateTimeOffset.Now, Command: name));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!operation.IsCancellationRequested)
            {
                _status.Text = "";
                await Append(room, new(DiscussionMember.Claude, DiscussionMember.Boss, error.Message, DateTimeOffset.Now, Command: name));
            }
        }
        finally
        {
            _commandCancel = null;
            _runCommand.IsEnabled = _commandInput.IsEnabled = _commandChoices.IsEnabled = true;
            UpdateComposer();
            if (_commandMode.IsChecked == true) _commandInput.Focus();
        }
    }
}
