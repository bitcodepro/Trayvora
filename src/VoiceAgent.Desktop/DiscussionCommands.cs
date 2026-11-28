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
    private readonly ToggleButton _commandClaude = new() { Content = "Command Claude", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ToggleButton _commandCodex = new() { Content = "Command Codex", HorizontalAlignment = HorizontalAlignment.Left };
    private DiscussionMember? _commandAgent;
    private bool InCommandMode => _commandAgent is not null;
    private readonly TextBox _commandInput = new() { PlaceholderText = "Type / for Claude CLI commands", MaxLength = 256 };
    private readonly ListBox _commandChoices = new() { MaxHeight = 168, IsVisible = false };
    private readonly Button _runCommand = new() { Content = "Run command" };
    private IReadOnlyList<ClaudeChat> _resumeChoices = [];
    private CancellationTokenSource? _commandCancel;
    private string? _suggestionsFor;
    private bool _commandEnterDown;
    internal Func<string>? CurrentClaudeProfile { get; set; }
    internal Func<string[], TerminalContext, CancellationToken, Task<(int Code, string Output)>>? RunClaudeCommand { get; set; }

    internal Func<string[], TerminalContext, CancellationToken, Task<(int Code, string Output)>>? RunCodexCommand { get; set; }
    internal Func<DiscussionRoom, CancellationToken, Task<bool>>? VerifyLocalCodexMcp { get; set; }

    private static string ClaudeCommandWorkspace(DiscussionRoom? room) =>
        room?.Claude?.Workspace ?? room?.NewCodexFolder ?? room?.CodexWorkspace ?? AppContext.BaseDirectory;

    private string CodexCommandProfile(DiscussionRoom room) =>
        CodexCommands.Profile(room.CodexDesktop, CurrentAgentProfile?.Invoke(room, DiscussionMember.Codex));

    private string CodexCommandWorkspace(DiscussionRoom room, string profile)
    {
        if (room.CodexDesktop) return WorkspaceFor(room, DiscussionMember.Codex) ?? AppContext.BaseDirectory;
        var workspace = Path.Combine(profile, "workspace");
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private void OpenCommandConsole(DiscussionMember agent, DiscussionMessage? mcpResult = null)
    {
        if (openTerminal is null) return;
        try
        {
            var profile = ClaudeCommands.NormalizeProfile(CurrentClaudeProfile?.Invoke());
            if (agent == DiscussionMember.Codex && _room is { } room)
            {
                var codexProfile = CodexCommandProfile(room);
                if (mcpResult is not null && (mcpResult.McpProfile != codexProfile || mcpResult.Workspace is null))
                {
                    _status.Text = CodexCommands.RefreshMcp;
                    return;
                }
                openTerminal(TerminalKind.Codex, new TerminalContext(
                    mcpResult?.Workspace ?? CodexCommandWorkspace(room, codexProfile), profile, codexProfile, room.CodexDesktop));
            }
            else if (agent == DiscussionMember.Claude)
            {
                if (mcpResult is not null && (mcpResult.McpProfile != profile || mcpResult.Workspace is null))
                {
                    _status.Text = ClaudeCommands.RefreshMcp;
                    return;
                }
                openTerminal(TerminalKind.Claude, new TerminalContext(mcpResult?.Workspace ?? ClaudeCommandWorkspace(_room), profile));
            }
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { _status.Text = "Could not open the terminal: " + error.Message; }
    }

    private Control BuildCommandInput(Control messages)
    {
        TrayvoraTheme.StyleControls(_commandClaude, _commandCodex, _commandInput);
        TrayvoraTheme.Primary(_runCommand);
        ToolTip.SetTip(_commandInput, "Enter inserts a suggestion. Enter again runs the command.");
        Avalonia.Automation.AutomationProperties.SetName(_commandClaude, "Claude command mode");
        Avalonia.Automation.AutomationProperties.SetName(_commandCodex, "Codex command mode");
        Avalonia.Automation.AutomationProperties.SetName(_commandChoices, "Claude command suggestions");
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(_runCommand, 1);
        line.Children.AddRange([_commandInput, _runCommand]);
        var panel = new StackPanel { IsVisible = false, Spacing = 6, Children = { _commandChoices, line } };
        var console = new Button { Content = "Claude console", HorizontalAlignment = HorizontalAlignment.Left };
        if (openTerminal is not null)
        {
            TrayvoraTheme.Ghost(console);
            ToolTip.SetTip(console, "Open the interactive CLI in this session’s workspace.");
            console.Click += (_, _) => { if (_commandAgent is { } agent) OpenCommandConsole(agent); };
            panel.Children.Add(console);
        }
        void SetMode(ToggleButton toggle, DiscussionMember agent)
        {
            if (toggle.IsChecked != true && _commandAgent != agent) return;
            DiscussionMember? next = toggle.IsChecked == true ? agent : null;
            if (next == _commandAgent) return;
            if (next is not null && _room is null)
            {
                NewRoom();
                if (_room is null)
                {
                    toggle.IsChecked = false;
                    return;
                }
            }

            _commandAgent = next;
            _commandClaude.IsChecked = next == DiscussionMember.Claude;
            _commandCodex.IsChecked = next == DiscussionMember.Codex;
            panel.IsVisible = InCommandMode;
            messages.IsVisible = !InCommandMode;
            _commandCancel?.Cancel();
            _resumeChoices = [];
            _commandInput.Clear();
            _suggestionsFor = null;
            var label = (next ?? DiscussionMember.Claude).ToString();
            _commandInput.PlaceholderText = UiLanguage.T($"Type / for {label} CLI commands");
            Avalonia.Automation.AutomationProperties.SetName(_commandChoices, $"{label} command suggestions");
            console.Content = UiLanguage.T($"{label} console");
            SuggestCommands();
            _status.Text = "";
            UpdateComposer();
            if (InCommandMode) _commandInput.Focus(); else _editor.Focus();
        }
        _commandClaude.IsCheckedChanged += (_, _) => SetMode(_commandClaude, DiscussionMember.Claude);
        _commandCodex.IsCheckedChanged += (_, _) => SetMode(_commandCodex, DiscussionMember.Codex);
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

    private void CloseCommandMode()
    {
        _commandClaude.IsChecked = _commandCodex.IsChecked = false;
    }

    private CommandChoice[] CommandMenu => _commandAgent == DiscussionMember.Codex ? CodexCommands.Menu : ClaudeCommands.Menu;

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
                ? CommandMenu.Where(item => item.Command.StartsWith(command, StringComparison.Ordinal))
                : _commandAgent == DiscussionMember.Claude ? command switch
                {
                    "/effort" or "/model" => (command == "/effort" ? ClaudeCommands.Efforts : ClaudeCommands.Models)
                        .Where(value => value.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                        .Select(value => new CommandChoice($"{command} {value}", value == "auto" ? "CLI default" : "")),
                    "/resume" => _resumeChoices.Where(chat => Matches(chat.Title, filter) || chat.Id.ToString().StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                        .Select(chat => new CommandChoice($"/resume {chat.Id}", chat.Title)),
                    _ => []
                } : [];
        }
        var rows = choices.Where(choice => !choice.Command.Equals(input.TrimEnd(), StringComparison.OrdinalIgnoreCase)).ToArray();
        _commandChoices.ItemsSource = rows;
        _commandChoices.SelectedIndex = -1;
        _commandChoices.IsVisible = rows.Length > 0;
    }

    private async Task ExecuteCommandAsync(string? input)
    {
        if (_commandAgent is not { } agent || _room is not { } room || CurrentRun is not null || _commandCancel is not null) return;
        var menu = CommandMenu;
        var parts = (input ?? "").Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        var name = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        if (!menu.Any(item => item.Command == name))
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
        RunningChanged?.Invoke();
        _runCommand.IsEnabled = _commandInput.IsEnabled = _commandChoices.IsEnabled = false;
        UpdateComposer();
        _status.Text = $"Running {name}…";
        string nextInput = "/";
        string? workspace = null;
        string? mcpProfile = null;
        try
        {
            string result;
            if (name == "/help")
            {
                result = string.Join("\n", menu.AsEnumerable());
                if (agent == DiscussionMember.Codex)
                    result += "\n\nOpen Codex console for interactive commands such as /model, /resume and /mcp.";
            }
            else if (agent == DiscussionMember.Codex && name == "/mcp" && !room.CodexDesktop)
            {
                var verify = VerifyLocalCodexMcp ?? throw new IOException("Trayvora Codex is unavailable.");
                var revqora = await verify(room, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                result = UiLanguage.T(revqora
                    ? "revqora · Allowed in this Trayvora session (read-only). Connection is checked when sending a message."
                    : "No MCP servers available in this Trayvora session.");
            }
            else if (agent == DiscussionMember.Codex)
            {
                var runner = RunCodexCommand ?? throw new IOException("Codex CLI is unavailable.");
                var profile = CodexCommandProfile(room);
                workspace = CodexCommandWorkspace(room, profile);
                var context = new TerminalContext(workspace, "", profile, room.CodexDesktop);
                var (code, output) = await runner(CodexCommands.Arguments(name, context.CodexDesktop), context, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (name == "/mcp" && code == 0) mcpProfile = profile;
                result = CodexCommands.Output(name, code, output);
            }
            else if (name == "/resume")
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
            else
            {
                var args = ClaudeCommands.Arguments(name) ?? throw new InvalidOperationException("Unknown command.");
                var runner = RunClaudeCommand ?? throw new IOException("Claude CLI is unavailable.");
                var profile = ClaudeCommands.NormalizeProfile(CurrentClaudeProfile?.Invoke());
                workspace = ClaudeCommandWorkspace(room);
                var (code, output) = await runner(args, new TerminalContext(workspace, profile), operation.Token);
                if (name == "/mcp") mcpProfile = profile;
                operation.Token.ThrowIfCancellationRequested();
                result = ClaudeCommands.Output(name, code, output);
            }
            var label = arg.Length == 0 || name == "/resume" && !Guid.TryParse(arg, out _) ? name : $"{name} {arg}";
            _status.Text = "";
            await Append(room, new(agent, DiscussionMember.Boss, result, DateTimeOffset.Now,
                Workspace: mcpProfile is null ? null : workspace, Command: label, McpProfile: mcpProfile));
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
                await Append(room, new(agent, DiscussionMember.Boss, $"{agent} CLI timed out.", DateTimeOffset.Now, Command: name));
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (!operation.IsCancellationRequested)
            {
                _status.Text = "";
                await Append(room, new(agent, DiscussionMember.Boss, error.Message, DateTimeOffset.Now, Command: name));
            }
        }
        finally
        {
            _commandCancel = null;
            RunningChanged?.Invoke();
            _runCommand.IsEnabled = _commandInput.IsEnabled = _commandChoices.IsEnabled = true;
            UpdateComposer();
            if (InCommandMode) _commandInput.Focus();
        }
    }
}
