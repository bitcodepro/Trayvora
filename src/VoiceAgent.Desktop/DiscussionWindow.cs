using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VoiceAgent.Codex;
using VoiceAgent.Infrastructure;

namespace VoiceAgent.Desktop;

internal enum DiscussionMember { Boss, Codex, Claude }

internal sealed record DesktopDelivery(string Profile, Guid ThreadId, Guid Id);

internal sealed record CodexChatOption(Guid Id, string Title, bool Desktop)
{
    public override string ToString() => Title;
}

/// <summary>
/// Sessions window. A session is a saved viewer of everything sent between Boss, Codex and Claude.
/// Each agent works in the chat bound to the session and only receives messages addressed to it.
/// Replies are shown while they are written; messages to different agents are delivered at the same time.
/// Several sessions can run at the same time.
/// </summary>
internal sealed partial class DiscussionWindow(
    DiscussionStore store,
    Func<DiscussionMember, string, DiscussionRoom, ReplyStream, IReadOnlyList<string>, CancellationToken, Task<string?>> send,
    Func<IReadOnlyList<Conversation>> codexChats,
    Func<CancellationToken, Task<IReadOnlyList<DesktopChat>>>? desktopChats,
    Func<Task<IReadOnlyList<ClaudeChat>>> claudeChats,
    Func<ClaudeChat?> defaultClaude,
    Func<CancellationToken, CancellationToken, IProgress<double>, Task<string?>>? dictate = null,
    Func<int>? discussRounds = null,
    Action<DiscussionRoom?>? openTerminal = null) : UserControl
{
    private readonly Button _newCodex = new() { Content = "New…" };
    private readonly Button _newClaude = new() { Content = "New…" };
    private readonly Button _terminal = new() { Content = "Terminal" };

    private async Task<string?> PickFolderAsync(string title, string? start)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (start is not null && Directory.Exists(start)) options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(new Uri(start));
        var picked = await storage.OpenFolderPickerAsync(options);
        return picked.Count > 0 ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(picked[0]) : null;
    }

    /// <summary>Raised when the user presses Back to return to the main view.</summary>
    public event Action? BackRequested;
    private readonly Button _back = new() { Content = "←  Back" };
    private Control? _header;
    private const int MaxAgentReplies = 4;
    // Codex accepts 16,000 characters per turn; the rest is left for the routing instructions Trayvora adds.
    internal const int MaxLength = 12000;
    private const double BubbleWidth = 860;
    private const string OwnThreadTitle = "Own Trayvora thread for this session";

    private sealed record Delivery(DiscussionMember To, DiscussionMember From, string Text, DiscussionMember? ForwardedFrom, bool Relay = false, DiscussionPlan? Discuss = null, IReadOnlyList<string>? Files = null);

    /// <summary>How many times the lead may hand the plan to the partner (Settings → Sessions, default 5).</summary>
    private int DiscussExchanges => Math.Clamp(discussRounds?.Invoke() ?? DiscussionOptions.DefaultRounds, DiscussionOptions.MinRounds, DiscussionOptions.MaxRounds);

    private sealed class RoomRun(CancellationTokenSource cancel)
    {
        public CancellationTokenSource Cancel { get; } = cancel;

        /// <summary>Agents answering right now and the reply each is writing.</summary>
        public Dictionary<DiscussionMember, ReplyStream> Live { get; } = [];
    }

    /// <summary>The bubble of a reply that is still being written.</summary>
    private sealed class LiveBubble(ReplyStream stream, Control row, MarkdownView view, TextBlock activity, TextBlock waiting, TextBlock steps)
    {
        public ReplyStream Stream { get; } = stream;
        public Control Row { get; } = row;
        public MarkdownView View { get; } = view;
        public TextBlock Activity { get; } = activity;
        public TextBlock Steps { get; } = steps;
        public TextBlock Waiting { get; } = waiting;
        public DateTime Started { get; } = DateTime.Now;
        public int Version { get; set; } = -1;
    }

    private readonly Dictionary<DiscussionMember, LiveBubble> _live = [];
    private DispatcherTimer? _liveTimer;
    private bool _listPending;
    private bool _stick = true;
    private readonly Button _jump = new() { Content = "↓  New messages", IsVisible = false };

    private readonly Dictionary<Guid, RoomRun> _runs = [];

    // Session list
    // Plain rows instead of a virtualized ListBox: no container recycling while sessions update.
    private readonly StackPanel _roomRows = new() { Spacing = 2 };
    private readonly ScrollViewer _rooms = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Button _newRoom = new() { Content = "+  New session", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _roomSearch = new() { PlaceholderText = "Search sessions and messages" };
    private readonly Button _export = new() { Content = "Export" };
    private readonly Avalonia.Controls.Primitives.ToggleButton _voiceMode = new() { Content = "Voice mode" };

    /// <summary>Raised when a session finishes a run of replies, with the status shown to the user.</summary>
    public event Action<DiscussionRoom, string?>? RunFinished;

    /// <summary>Reads a reply aloud in voice mode. Null hides voice mode.</summary>
    public Func<string, Task>? Speak { get; set; }

    private PromptTemplates Templates => new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.Directory)) ?? store.Directory, "templates.json"));
    private readonly TextBlock _roomsEmpty = new() { Text = "No sessions yet.", Foreground = TrayvoraTheme.Muted, FontSize = 12, Margin = new Thickness(6, 4) };
    private List<DiscussionRoom> _items = [];
    private DiscussionRoom? _room;
    private bool _switching;

    // Header
    private readonly TextBox _title = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, MaxLength = 80 };
    private readonly ComboBox _codexChat = new() { MinWidth = 240, MaxDropDownHeight = 360 };
    private readonly Button _loadDesktop = new() { Content = "Load Desktop chats" };
    private readonly ComboBox _claudeChat = new() { MinWidth = 240, PlaceholderText = "Choose a Claude chat", MaxDropDownHeight = 360 };
    private readonly Button _loadClaude = new() { Content = "Load Claude chats" };
    private static readonly string[] PermissionNames = ["Ask (no edits or commands)", "Allow file edits", "Allow everything"];
    private readonly ComboBox _claudeRights = new() { ItemsSource = PermissionNames, SelectedIndex = 0, Width = 200 };
    private readonly TextBox _codexSearch = new() { PlaceholderText = "Search Codex chats", Width = 170 };
    private readonly TextBox _claudeSearch = new() { PlaceholderText = "Search Claude chats", Width = 170 };
    private readonly Button _delete = new() { Content = "Delete" };
    private readonly Border _confirm = new() { IsVisible = false };
    private IReadOnlyList<DesktopChat> _desktopItems = [];
    private IReadOnlyList<ClaudeChat> _claudeItems = [];
    private readonly Dictionary<DiscussionMember, (Border Dot, TextBlock State)> _presence = [];

    // Messages and composer
    private readonly StackPanel _messages = new() { Spacing = 14, Margin = new Thickness(24, 20, 24, 8) };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 160,
        VerticalContentAlignment = VerticalAlignment.Top,
    };
    private readonly Button _send = new() { Content = new TextBlock { Text = TrayvoraTheme.IconSend, FontFamily = IconFont, FontSize = 16 } };
    private readonly Button _attachButton = new() { Content = new TextBlock { Text = "\uE723", FontFamily = IconFont, FontSize = 15 } };
    private ComposerAttachments? _attachments;
    private ComposerAttachments Attachments => _attachments ??= new ComposerAttachments(
        () => _room is { } room ? AttachmentsFolder(room) : null);

    private string AttachmentsFolder(DiscussionRoom room) => Path.Combine(store.Directory, "attachments", room.Id.ToString("N"));
    internal const double EditorMinHeight = 44, EditorDefaultMaxHeight = 160;
    private readonly Button _pause = new() { Content = "Pause", IsVisible = false };
    private readonly Button _mic = new();
    private readonly ComboBox _recognizer = new() { ItemsSource = new[] { "Recognition: default", "Recognition: Whisper", "Recognition: OpenAI" }, SelectedIndex = 0, IsVisible = false };
    private Action<bool?>? _recognitionChanged;

    /// <summary>Shows the dictation recognizer chooser (default / local Whisper / OpenAI) and saves changes through <paramref name="changed"/>.</summary>
    public void UseRecognition(bool? current, Action<bool?> changed)
    {
        _recognitionChanged = null;
        _recognizer.SelectedIndex = Recognizers.Index(current);
        _recognitionChanged = changed;
        _recognizer.IsVisible = _mic.IsVisible;
    }
    private readonly ProgressBar _level = new() { Minimum = 0, Maximum = 100, IsVisible = false, Margin = new Thickness(4, 0) };
    private CancellationTokenSource? _dictationStop;
    private CancellationTokenSource? _dictationCancel;
    private bool _dictationTranscribing;
    private readonly Button _toCodex = TrayvoraTheme.Chip("Codex");
    private readonly Button _toClaude = TrayvoraTheme.Chip("Claude");
    private readonly Button _toBoth = TrayvoraTheme.Chip("Both");
    // Relay toggles: when on, the agent's reply is passed to the other agent (and shown here).
    private readonly Button _relayCodex = RelayToggle(DiscussionMember.Claude);
    private readonly Button _relayClaude = RelayToggle(DiscussionMember.Codex);
    private bool _relayFromCodex;
    private bool _relayFromClaude;
    private readonly Button _collapse = new();
    private readonly StackPanel _details = new() { Spacing = 12 };
    private readonly TextBlock _summary = new() { FontSize = 12, Foreground = TrayvoraTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
    private static bool _headerCollapsed;

    // Rendering: only the latest page of a session is drawn; older messages load on request.
    private const int PageSize = 60;
    private readonly Dictionary<DiscussionMessage, Control> _rendered = new(ReferenceEqualityComparer.Instance);
    private readonly Button _earlier = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private int _shownFrom;

    // Session files are written in order on a background queue so the UI never waits for the disk.
    private Task _saves = Task.CompletedTask;
    internal Task PendingSaves => _saves;
    private readonly TextBlock _counter = new() { FontSize = 11.5, Foreground = TrayvoraTheme.Faint, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Foreground = TrayvoraTheme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private DiscussionTarget _target = DiscussionTarget.Codex;

    private static FontFamily IconFont => new("Segoe Fluent Icons, Segoe MDL2 Assets");
    internal enum DiscussionTarget { Codex, Claude, Both }

    private RoomRun? CurrentRun => _room is { } room && _runs.TryGetValue(room.Id, out var run) ? run : null;

    public void Initialize()
    {
        TrayvoraTheme.StyleControls(_back);
        TrayvoraTheme.Ghost(_back);
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        _back.Click += (_, _) => BackRequested?.Invoke();
        Avalonia.Automation.AutomationProperties.SetName(_back, "Back to Trayvora");
        TrayvoraTheme.Primary(_send);
        _send.Classes.Add("round");
        TrayvoraTheme.StyleControls(_mic);
        _mic.Classes.Add("round");
        _mic.IsVisible = dictate is not null;
        _mic.Click += async (_, _) => await ToggleDictationAsync();
        UpdateMic();
        Avalonia.Automation.AutomationProperties.SetName(_send, "Send");
        Avalonia.Automation.AutomationProperties.SetName(_pause, "Pause");
        TrayvoraTheme.Primary(_newRoom);
        TrayvoraTheme.StyleControls(_pause, _delete, _loadClaude, _loadDesktop, _codexChat, _claudeChat, _codexSearch, _claudeSearch);
        Avalonia.Automation.AutomationProperties.SetName(_codexSearch, "Search Codex chats");
        Avalonia.Automation.AutomationProperties.SetName(_claudeSearch, "Search Claude chats");
        TrayvoraTheme.StyleControls(_claudeRights, _newCodex, _newClaude, _terminal);
        ToolTip.SetTip(_newCodex, "Create a new Codex Desktop chat in the same place Codex puts its own new chats, so it shows up in Codex Desktop after your first message. Right-click to choose a project folder instead.");
        ToolTip.SetTip(_newClaude, "Create a new Claude chat. It uses the folder of this session's new Codex chat (or a new one) and is saved by Claude Code with your first message. Right-click to choose a project folder instead.");
        ToolTip.SetTip(_terminal, "Open PowerShell in this session's folder with claude and codex ready, to sign in, add MCP servers or change settings.");
        Avalonia.Automation.AutomationProperties.SetName(_newCodex, "New Codex Desktop chat");
        Avalonia.Automation.AutomationProperties.SetName(_newClaude, "New Claude chat");
        Avalonia.Automation.AutomationProperties.SetName(_terminal, "Open terminal");
        TrayvoraTheme.StyleControls(_claudeRights);
        Avalonia.Automation.AutomationProperties.SetName(_claudeRights, "Claude permissions");
        ToolTip.SetTip(_claudeRights,
            "Trayvora can ask before an MCP tool call. Ask: other tools that require approval are declined. " +
            "Allow file edits: creates and changes files, commands are still refused. Allow everything: any command without asking — use only for trusted tasks.");
        _claudeRights.SelectionChanged += (_, _) =>
        {
            if (_switching || _room is not { } room || _claudeRights.SelectedIndex < 0) return;
            var chosen = (ClaudePermissions)_claudeRights.SelectedIndex;
            if (chosen == room.ClaudePermissions) return;
            room.ClaudePermissions = chosen;
            _ = Persist(room);
            _status.Text = $"Claude in this session: {PermissionNames[_claudeRights.SelectedIndex]}.";
        };
        _codexSearch.TextChanged += (_, _) => FillCodexChoices();
        _claudeSearch.TextChanged += (_, _) => FillClaudeChoices();
        // Enter opens the filtered list.
        _codexSearch.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) _codexChat.IsDropDownOpen = true; };
        _claudeSearch.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) _claudeChat.IsDropDownOpen = true; };
        TrayvoraTheme.DangerButton(_pause);
        TrayvoraTheme.Ghost(_delete);
        _loadDesktop.IsVisible = desktopChats is not null;
        _editor.Classes.Add("bare");
        _title.Classes.Add("bare");
        _scroll.Content = _messages;
        // Follow new text only while the reader is at the bottom; otherwise offer a jump button.
        _scroll.ScrollChanged += (_, args) =>
        {
            if (args.OffsetDelta.Y == 0) return;
            _stick = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 48;
            if (_stick) _jump.IsVisible = false;
        };
        TrayvoraTheme.StyleControls(_jump);
        _jump.Classes.Add("chip");
        _jump.HorizontalAlignment = HorizontalAlignment.Center;
        _jump.VerticalAlignment = VerticalAlignment.Bottom;
        _jump.Margin = new Thickness(0, 0, 0, 10);
        _jump.Click += (_, _) => ScrollToEnd(force: true);
        TrayvoraTheme.StyleControls(_earlier);
        TrayvoraTheme.Ghost(_earlier);
        _earlier.Click += (_, _) => ShowEarlier();

        _rooms.Content = _roomRows;
        _newRoom.Click += (_, _) => NewRoom();
        ToolTip.SetTip(_editor, "Enter sends · Shift+Enter starts a new line");
        TrayvoraTheme.StyleControls(_roomSearch, _export, _voiceMode);
        _roomSearch.TextChanged += async (_, _) => await SearchRoomsAsync();
        _export.Click += async (_, _) => await ExportAsync();
        ToolTip.SetTip(_export, "Save this session as a Markdown file");
        ToolTip.SetTip(_voiceMode, "Voice mode: dictated messages are sent at once and replies addressed to you are read aloud.");
        _voiceMode.IsCheckedChanged += (_, _) => _status.Text = _voiceMode.IsChecked == true
            ? "Voice mode on: press the microphone, speak, press it again — the message is sent and replies are read aloud." : "Voice mode off.";
        _send.Click += async (_, _) => await PostAsync();
        _pause.Click += (_, _) => { if (Attachments.Copying) Attachments.CancelCopy(); else CurrentRun?.Cancel.Cancel(); };
        _delete.Click += (_, _) => _confirm.IsVisible = true;
        _toCodex.Click += (_, _) => SetTarget(DiscussionTarget.Codex);
        _toClaude.Click += (_, _) => SetTarget(DiscussionTarget.Claude);
        _toBoth.Click += (_, _) => SetTarget(DiscussionTarget.Both);
        _relayCodex.Click += (_, _) => { _relayFromCodex = !_relayFromCodex; SetTarget(DiscussionTarget.Codex); };
        _relayClaude.Click += (_, _) => { _relayFromClaude = !_relayFromClaude; SetTarget(DiscussionTarget.Claude); };
        TrayvoraTheme.StyleControls(_collapse);
        TrayvoraTheme.Ghost(_collapse);
        _collapse.Click += (_, _) => { _headerCollapsed = !_headerCollapsed; UpdateHeader(); };
        _title.LostFocus += (_, _) => RenameRoom();
        _title.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; _editor.Focus(); } };
        _codexChat.SelectionChanged += (_, _) => BindCodex();
        _claudeChat.SelectionChanged += (_, _) => BindClaude();
        _loadDesktop.Click += async (_, _) => await LoadDesktopAsync();
        _newCodex.IsVisible = desktopChats is not null;
        // New chats go where Codex Desktop puts its own chats (…\workspaces\<date>\new-chat-N), so they show up there;
        // "Choose folder…" in the button's menu picks a project folder instead.
        async Task<string?> NewFolderAsync(bool pick, string title, string? start)
        {
            if (pick) return await PickFolderAsync(title, start);
            try { return await Task.Run(() => ChatFolders.CreateNew(ChatFolders.WorkspacesRoot())); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _status.Text = "Could not create the chat folder: " + error.Message;
                return null;
            }
        }
        async Task NewCodexAsync(bool pick)
        {
            if (_room is not { } room || await NewFolderAsync(pick, "Folder for the new Codex chat", room.Claude?.Workspace) is not { } folder) return;
            room.CodexDesktop = true;
            room.CodexChatId = Guid.Empty;
            room.CodexWorkspace = folder;
            room.NewCodexFolder = folder;
            room.CodexChatTitle = $"New Codex Desktop chat · {Path.GetFileName(folder)}";
            _ = Persist(room);
            FillCodexChoices();
            UpdateHeader();
            _status.Text = "The Codex Desktop chat is created with your next message to Codex.";
        }
        async Task NewClaudeAsync(bool pick)
        {
            if (_room is null) return;
            // Without a choice Claude shares the folder of this session's new Codex chat, or gets its own new chat folder.
            var folder = !pick && _room.NewCodexFolder is { } shared && Directory.Exists(shared) ? shared
                : await NewFolderAsync(pick, "Folder for the new Claude chat", _room.Claude?.Workspace ?? _room.NewCodexFolder);
            if (_room is not { } room || folder is null) return;
            room.Claude = new ClaudeChat(Guid.NewGuid(), folder, $"New Claude chat · {Path.GetFileName(folder)}");
            _ = Persist(room);
            FillClaudeChoices();
            UpdateHeader();
            _status.Text = "The Claude chat is created with your next message to Claude.";
        }
        Avalonia.Controls.Primitives.FlyoutBase ChooseMenu(Func<bool, Task> create) => new MenuFlyout { Items =
        {
            NewMenuItem("New chat", () => create(false)),
            NewMenuItem("Choose folder…", () => create(true)),
        } };
        static MenuItem NewMenuItem(string header, Func<Task> act)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await act();
            return item;
        }
        _newCodex.Click += async (_, _) => await NewCodexAsync(false);
        _newClaude.Click += async (_, _) => await NewClaudeAsync(false);
        _newCodex.ContextFlyout = ChooseMenu(NewCodexAsync);
        _newClaude.ContextFlyout = ChooseMenu(NewClaudeAsync);
        _terminal.Click += (_, _) =>
        {
            try { openTerminal?.Invoke(_room); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { _status.Text = "Could not open the terminal: " + error.Message; }
        };
        _loadClaude.Click += async (_, _) => await LoadClaudeAsync();
        _editor.TextChanged += (_, _) => UpdateComposer();
        _editor.AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.Key != Key.Enter || args.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            args.Handled = true;
            await PostAsync();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && _commandMode.IsChecked == true) { _commandMode.IsChecked = false; args.Handled = true; }
            else if (args.Key == Key.Escape && _dictationCancel is { } dictation) { dictation.Cancel(); args.Handled = true; }
            else if (args.Key == Key.Escape && CurrentRun is { } run) { run.Cancel.Cancel(); args.Handled = true; }
            else if (args.Key == Key.Escape) { BackRequested?.Invoke(); args.Handled = true; }
        };

        var chat = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = BuildHeader();
        _header = header;
        var composer = BuildComposer();
        var body = new Grid { Children = { _scroll, _jump } };
        Grid.SetRow(body, 1);
        Grid.SetRow(composer, 2);
        chat.Children.AddRange([header, body, composer]);

        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var sidebar = BuildSidebar();
        Grid.SetColumn(chat, 1);
        shell.Children.AddRange([sidebar, chat]);
        Content = shell;

        SetTarget(DiscussionTarget.Codex);
        _ = ReloadRoomsAsync();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => _editor.Focus(), DispatcherPriority.Background);
    }

    public Action<string, string?, string?, Action<DiscussionTarget, string>>? PreviewFile { get; set; }
    public Func<string, Guid, Guid, CancellationToken, Task<DesktopTurn?>>? CheckDelivery { get; set; }

    public async Task<DesktopTurn?> SubmitDesktopDeliveryAsync(DiscussionRoom room, string profile, Func<DesktopDelivery, Task<DesktopTurn?>> submit)
    {
        var delivery = new DesktopDelivery(profile, room.CodexChatId, Guid.NewGuid());
        var saved = await Append(room, new DiscussionMessage(DiscussionMember.Codex, DiscussionMember.Boss,
            $"Submitting to {room.CodexChatTitle ?? "Codex Desktop"}…", DateTimeOffset.Now, DeliveryId: delivery.Id, DeliveryProfile: delivery.Profile));
        if (!saved)
        {
            room.Messages.RemoveAll(message => message.DeliveryId == delivery.Id);
            _ = Persist(room);
            if (ReferenceEquals(room, _room)) RefreshTimeline(room);
            throw new IOException("Could not save the delivery record. The request was not sent.");
        }
        var turn = await submit(delivery);
        if (turn is not null) FinishDelivery(room, delivery.Id, $"Codex Desktop · {turn.Status}");
        return turn;
    }

    private void FinishDelivery(DiscussionRoom room, Guid id, string status)
    {
        var index = room.Messages.FindIndex(message => message.DeliveryId == id);
        if (index < 0) return;
        room.Messages[index] = room.Messages[index] with { Text = status, DeliveryId = null };
        _ = Persist(room);
        if (ReferenceEquals(room, _room)) RefreshTimeline(room);
    }

    private void RefreshTimeline(DiscussionRoom room)
    {
        _messages.Children.Clear();
        _rendered.Clear();
        _live.Clear();
        RenderLatest(room);
        ShowRunState();
    }

    public Action? StopSpeaking { get; set; }
    public bool BusyForUpdate => IsRunning || _dictationStop is not null || _attachments?.Copying == true || !_saves.IsCompleted;
    public bool IsRunning => _runs.Count > 0 || _commandCancel is not null;
    public DiscussionRoom? CurrentRoom => _room;

    /// <summary>Stops every running session (used on shutdown).</summary>
    public void Pause()
    {
        _commandCancel?.Cancel();
        foreach (var run in _runs.Values) run.Cancel.Cancel();
    }

    // "Claude:", "**Claude:**", "**Claude**:", "@Claude:" at the very start of a message.
    private static readonly System.Text.RegularExpressions.Regex Address = new(
        @"^\s*[*_]{0,2}@?(?<name>codex|claude|boss)[*_]{0,2}\s*:[*_]{0,2}",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static DiscussionMember? Recipient(string text)
    {
        var match = Address.Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(text[match.Length..])) return null;
        return match.Groups["name"].Value.ToUpperInvariant() switch
        {
            "CODEX" => DiscussionMember.Codex,
            "CLAUDE" => DiscussionMember.Claude,
            _ => DiscussionMember.Boss,
        };
    }

    internal static string StripAddress(string text) =>
        Recipient(text) is null ? text.Trim() : text[Address.Match(text).Length..].Trim();

    /// <summary>Long replies passed between agents are cut so the receiving agent accepts the turn.</summary>
    /// <summary>
    /// The text an agent gets. Up to <see cref="MaxLength"/> characters it goes in the message; a longer one is saved
    /// whole as a .txt file in <paramref name="folder"/> and the agent gets its path, a short preview and an
    /// instruction to read the file, so nothing is cut and the message stays under Codex's per-turn limit.
    /// </summary>
    /// <summary>Which session and chat a failure concerns, for the Error log.</summary>
    internal static string ChatContext(DiscussionRoom room, DiscussionMember agent)
    {
        var chat = agent == DiscussionMember.Claude
            ? room.Claude is { } claude ? $"Claude chat \"{claude.Title}\" ({claude.Id})" : "no Claude chat"
            : room.CodexDesktop ? $"Codex Desktop chat \"{room.CodexChatTitle}\" ({room.CodexChatId})"
            : $"Trayvora Codex chat {room.CodexChatId}";
        return $"session \"{room.Title}\" · {chat}";
    }

    internal static string ForAgent(string text, string folder, string name) => ForAgentWithFile(text, folder, name).Text;

    /// <summary>The text an agent receives and, for a long message, the file holding the whole of it.</summary>
    internal static (string Text, string? File) ForAgentWithFile(string text, string folder, string name)
    {
        if (text.Length <= MaxLength) return (text, null);
        Directory.CreateDirectory(folder);
        var safe = string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        var stem = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{safe}-{Guid.NewGuid():N}";
        var file = Path.Combine(folder, stem[..Math.Min(60, stem.Length)] + ".txt");
        File.WriteAllText(file, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var preview = text[..Math.Min(PreviewLength, text.Length)].TrimEnd();
        return ($"[Trayvora: this message is long ({text.Length:N0} characters), so the full text is attached as a file. " +
            $"Read the whole file before you answer: {file}]\n\nBeginning of the message:\n{preview}\n[… continues in the file]", file);
    }

    internal const int PreviewLength = 1500;

    /// <summary>Messages past this size are refused (they would make the session file too large to keep).</summary>
    internal const int MaxTypedLength = 2_000_000;

    /// <summary>Text the agent receives: only this message, labelled with its sender and how to hand off.</summary>
    internal static string Envelope(DiscussionRoom room, DiscussionMember to, DiscussionMember from, string text,
        DiscussionMember? forwardedFrom, bool relay = false, DiscussionPlan? discuss = null)
    {
        // Discussions need the rules of the exchange; everything else goes as plain text to save tokens.
        if (discuss is { } plan)
            return $"[Trayvora session \"{room.Title}\" · message from {from} · discussion]\n{text}\n\n{DiscussInstructions(to, from, plan)}";
        // Boss's own words go unchanged; Trayvora itself passes replies on, so the agent needs no routing rules.
        if (from == DiscussionMember.Boss && forwardedFrom is null) return text;
        return $"{forwardedFrom ?? from} wrote:\n{text}";
    }

    /// <summary>What the lead and the partner are told at each step of a "discuss with …" request.</summary>
    internal static string DiscussInstructions(DiscussionMember to, DiscussionMember from, DiscussionPlan plan)
    {
        var lead = plan.Lead;
        var partner = plan.Partner;
        var exchanges = plan.ExchangesLeft == 1 ? "1 more exchange" : $"{plan.ExchangesLeft} more exchanges";
        if (to == partner)
            return $"[{lead} is working out a task from Boss and asks for your review. Look into it yourself where needed, then answer {lead}: " +
                   $"point out problems, risks and better options, answer the questions, and say clearly whether you agree with the plan. " +
                   $"Be concise. Your reply goes back to {lead} automatically; {lead} reports the final decision to Boss, so do not address Boss.]";
        if (from == DiscussionMember.Boss)
            return $"[Boss asked you to work this out together with {partner}. You lead the discussion:\n" +
                   $"1. First do your own part: look at the code, files or facts the task mentions and form an opinion.\n" +
                   $"2. Send {partner} your findings, a concrete plan and the questions you want reviewed. Start that reply with \"{partner}:\".\n" +
                   $"3. {partner}'s answer comes back to you. Then decide yourself whether the plan is ready (or the work is done correctly).\n" +
                   $"4. If it is ready, reply to Boss with the final decision and plan. Start with \"Boss:\" and do not write to {partner} again. If not, send {partner} your next question.\n" +
                   $"You can hand the plan to {partner} at most {plan.ExchangesLeft} times.]";
        return plan.ExchangesLeft > 0
            ? $"[{partner}'s answer in your discussion. Think it over and decide yourself: is the plan ready, or is the work done correctly?\n" +
              $"- If yes: reply to Boss with the final decision and plan, starting with \"Boss:\". Do not write to {partner} again.\n" +
              $"- If not: reply to {partner} with your next concrete question or the revised plan, starting with \"{partner}:\" ({exchanges} allowed).]"
            : $"[That was the last exchange with {partner}. Reply to Boss now with the final decision and plan, starting with \"Boss:\". Do not write to {partner}.]";
    }

    // ── Layout ───────────────────────────────────────────────────────────

    private Control BuildSidebar()
    {
        var top = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _back,
                new TextBlock { Text = "Sessions", FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 0, 0, 0) },
                _newRoom,
                _roomSearch,
                TrayvoraTheme.Caption("Saved sessions"),
                _roomsEmpty,
            },
        };
        var footer = TrayvoraTheme.Hint("Sessions are saved on this computer until you delete them. Several sessions can run at the same time.");
        footer.Margin = new Thickness(4, 8, 4, 0);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 4 };
        Grid.SetRow(_rooms, 1);
        Grid.SetRow(footer, 2);
        grid.Children.AddRange([top, _rooms, footer]);
        return new Border
        {
            Width = 270,
            Background = TrayvoraTheme.Sidebar,
            BorderBrush = TrayvoraTheme.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(12, 18, 12, 14),
            Child = grid,
        };
    }

    private Control RoomItem(DiscussionRoom room)
    {
        Control state = _runs.TryGetValue(room.Id, out var run)
            ? new TextBlock { Text = run.Live.Count > 0 ? string.Join(", ", run.Live.Keys) + "…" : "working…", FontSize = 11, Foreground = TrayvoraTheme.Warning, VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock { Text = When(room.Updated), FontSize = 11, Foreground = TrayvoraTheme.Faint, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(state, 1);
        return new StackPanel
        {
            Spacing = 2, Margin = new Thickness(2, 3),
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6,
                    Children =
                    {
                        new TextBlock { Text = room.Title, FontSize = 13, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis },
                        state,
                    },
                },
                new TextBlock { Text = room.Preview, FontSize = 11.5, Foreground = TrayvoraTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
    }

    private static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm")
            : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : local.ToString("dd.MM.yy");
    }

    private Control BuildHeader()
    {
        var participants = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        foreach (var member in new[] { DiscussionMember.Boss, DiscussionMember.Codex, DiscussionMember.Claude })
        {
            var dot = TrayvoraTheme.Dot(TrayvoraTheme.Success, 7);
            var state = new TextBlock { Text = member == DiscussionMember.Boss ? "you" : "ready", FontSize = 11, Foreground = TrayvoraTheme.Muted };
            _presence[member] = (dot, state);
            participants.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    TrayvoraTheme.Avatar(Initials(member), MemberColor(member), 30),
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center, Spacing = 1,
                        Children =
                        {
                            new TextBlock { Text = DisplayName(member), FontSize = 13, FontWeight = FontWeight.SemiBold },
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { dot, state } },
                        },
                    },
                },
            });
        }

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var actions = openTerminal is null ? TrayvoraTheme.Row(_export, _pause, _delete, _collapse) : TrayvoraTheme.Row(_terminal, _export, _pause, _delete, _collapse);
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actions, 1);
        _title.Margin = new Thickness(-4, 0, 0, 0);
        ToolTip.SetTip(_title, "Click to rename");
        var titleBlock = new StackPanel { Spacing = 0, Children = { _title, _summary } };
        _summary.Margin = new Thickness(0, -2, 0, 0);
        top.Children.AddRange([titleBlock, actions]);

        static Control Binding(string who, IBrush color, Control picker, Control extra, Control search, Control create) => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                TrayvoraTheme.Dot(color, 8),
                new TextBlock { Text = who, FontSize = 12, Foreground = TrayvoraTheme.Muted, VerticalAlignment = VerticalAlignment.Center, Width = 48 },
                search,
                picker,
                extra,
                create,
            },
        };
        var bindings = new WrapPanel { ItemSpacing = 22, LineSpacing = 8 };
        _details.Children.AddRange([bindings, participants]);
        bindings.Children.Add(Binding("Codex", TrayvoraTheme.CodexColor, _codexChat, _loadDesktop, _codexSearch, _newCodex));
        bindings.Children.Add(Binding("Claude", TrayvoraTheme.ClaudeColor, _claudeChat, _loadClaude, _claudeSearch, _newClaude));
        bindings.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = "Claude may", FontSize = 12, Foreground = TrayvoraTheme.Muted, VerticalAlignment = VerticalAlignment.Center }, _claudeRights },
        });

        var yes = new Button { Content = "Delete session" };
        var no = new Button { Content = "Keep" };
        TrayvoraTheme.DangerButton(yes);
        TrayvoraTheme.StyleControls(no);
        yes.Click += (_, _) => DeleteRoom();
        no.Click += (_, _) => _confirm.IsVisible = false;
        var confirmGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var confirmText = new TextBlock
        {
            Text = "Delete this session and its saved messages? The Codex and Claude chats themselves are kept.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#FFC2CC"),
        };
        var confirmButtons = TrayvoraTheme.Row(no, yes);
        Grid.SetColumn(confirmButtons, 1);
        confirmGrid.Children.AddRange([confirmText, confirmButtons]);
        _confirm.Background = Brush.Parse("#2A1A22");
        _confirm.BorderBrush = Brush.Parse("#5A2635");
        _confirm.BorderThickness = new Thickness(1);
        _confirm.CornerRadius = new CornerRadius(10);
        _confirm.Padding = new Thickness(12, 8);
        _confirm.Child = confirmGrid;

        return new Border
        {
            Background = TrayvoraTheme.Sidebar,
            BorderBrush = TrayvoraTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 14, 24, 14),
            Child = new StackPanel { Spacing = 12, Children = { top, _details, _confirm } },
        };
    }

    private void UpdateHeader()
    {
        _details.IsVisible = !_headerCollapsed;
        _summary.IsVisible = _headerCollapsed;
        _collapse.Content = new TextBlock { Text = _headerCollapsed ? "\uE70D" : "\uE70E", FontFamily = IconFont, FontSize = 12 };
        var tip = _headerCollapsed ? "Show chat settings" : "Hide chat settings";
        ToolTip.SetTip(_collapse, tip);
        Avalonia.Automation.AutomationProperties.SetName(_collapse, tip);
        if (_room is { } room)
            _summary.Text = $"Codex: {(room.OwnCodexThread ? "own thread" : room.CodexChatTitle ?? "chat")}   ·   Claude: {room.Claude?.Title ?? "not selected"}";
    }

    private Control BuildComposer()
    {
        var toRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { new TextBlock { Text = "To", Foreground = TrayvoraTheme.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) }, Joined(_toCodex, _relayCodex), Joined(_toClaude, _relayClaude), _toBoth, RelayHint() },
        };
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_counter, 1);
        bottom.Children.AddRange([toRow, _counter]);

        var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), ColumnSpacing = 8 };
        var templates = Templates.Button(() => _editor.Text, text => PromptTemplates.InsertInto(_editor, text));
        templates.VerticalAlignment = VerticalAlignment.Bottom;
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        _mic.VerticalAlignment = VerticalAlignment.Bottom;
        _attachButton.VerticalAlignment = VerticalAlignment.Bottom;
        TrayvoraTheme.Ghost(_attachButton);
        ToolTip.SetTip(_attachButton, "Attach files (or drop them here, or paste a picture with Ctrl+V)");
        Avalonia.Automation.AutomationProperties.SetName(_attachButton, "Attach files");
        _attachButton.Click += async (_, _) => await ChooseAttachmentsAsync();
        Grid.SetColumn(_attachButton, 1);
        Grid.SetColumn(templates, 2);
        Grid.SetColumn(_mic, 3);
        Grid.SetColumn(_send, 4);
        input.Children.AddRange([_editor, _attachButton, templates, _mic, _send]);
        Attachments.Problem += problem => _status.Text = problem;
        Attachments.Changed += UpdateComposer;
        Attachments.HandlePaste(_editor);
        _voiceMode.IsVisible = dictate is not null;
        _voiceMode.FontSize = 11.5;
        _voiceMode.Padding = new Thickness(10, 3);
        toRow.Children.Add(_voiceMode);
        _recognizer.FontSize = 11.5;
        _recognizer.MinHeight = 26;
        _recognizer.Padding = new Thickness(10, 2);
        ToolTip.SetTip(_recognizer, "How the 🎤 dictation in sessions is recognized: local Whisper (free, offline) or OpenAI (needs the API key in Settings).");
        _recognizer.SelectionChanged += (_, _) =>
        {
            try { _recognitionChanged?.Invoke(Recognizers.Value(_recognizer.SelectedIndex)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        };
        TrayvoraTheme.StyleControls(_recognizer);
        toRow.Children.Add(_recognizer);

        var box = new Border
        {
            Background = TrayvoraTheme.Surface,
            BorderBrush = TrayvoraTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 10, 10, 10),
            Child = new StackPanel { Spacing = 8, Children = { ResizeGrip(), Attachments.Chips, input, _level, bottom } },
        };
        _editor.GotFocus += (_, _) => box.BorderBrush = TrayvoraTheme.Accent;
        _editor.LostFocus += (_, _) => box.BorderBrush = TrayvoraTheme.Border;
        Attachments.AcceptDrops(this, on => box.BorderBrush = on ? TrayvoraTheme.Accent : _editor.IsFocused ? TrayvoraTheme.Accent : TrayvoraTheme.Border);
        return new Border
        {
            Padding = new Thickness(20, 6, 20, 16),
            Child = CommandComposer(box),
        };
    }

    /// <summary>A handle above the text box: drag it up or down to make the box taller or shorter; double-click resets.</summary>
    private Control ResizeGrip()
    {
        var grip = new Border
        {
            Height = 10, Margin = new Thickness(0, -6, 0, -4), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Child = new Border { Width = 36, Height = 4, CornerRadius = new CornerRadius(2), Background = TrayvoraTheme.Border, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTip.SetTip(grip, "Drag to resize the text box · double-click to reset");
        double? startY = null;
        double startHeight = 0;
        grip.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            if (args.ClickCount == 2)
            {
                _editor.Height = double.NaN;
                _editor.MaxHeight = EditorDefaultMaxHeight;
                startY = null;
                args.Handled = true;
                return;
            }
            startY = args.GetPosition(this).Y;
            startHeight = _editor.Bounds.Height;
            args.Pointer.Capture(grip);
            args.Handled = true;
        };
        grip.PointerMoved += (_, args) =>
        {
            if (startY is not { } from) return;
            var limit = Math.Max(EditorMinHeight, Bounds.Height * 0.65);
            var height = EditorHeight(startHeight, from - args.GetPosition(this).Y, limit);
            _editor.MaxHeight = double.PositiveInfinity;
            _editor.Height = height;
        };
        grip.PointerReleased += (_, args) => { startY = null; args.Pointer.Capture(null); };
        grip.PointerCaptureLost += (_, _) => startY = null;
        return grip;
    }

    /// <summary>The new text box height after dragging <paramref name="up"/> pixels upwards, kept between the minimum and <paramref name="limit"/>.</summary>
    internal static double EditorHeight(double start, double up, double limit) => Math.Clamp(start + up, EditorMinHeight, Math.Max(EditorMinHeight, limit));

    private async Task ChooseAttachmentsAsync()
    {
        if (_room is null) NewRoom();
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "Attach files", AllowMultiple = true });
        await Attachments.AddFilesAsync(files.Select(file => Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file)).OfType<string>());
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    private async Task ReloadRoomsAsync()
    {
        IsEnabled = false;
        try { _items = await Task.Run(store.ListHeaders); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _items = [];
            _status.Text = "Could not read saved sessions.";
        }
        await OpenSavedAsync(_items.FirstOrDefault());
        IsEnabled = true;
    }

    /// <summary>Rebuilds the session list once after a burst of changes.</summary>
    private void RefreshList()
    {
        if (_listPending) return;
        _listPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _listPending = false;
            RefreshListNow();
        }, DispatcherPriority.Background);
    }

    private HashSet<Guid> _searchHits = [];
    private int _searchVersion;
    private async Task SearchRoomsAsync()
    {
        var version = ++_searchVersion;
        var query = _roomSearch.Text?.Trim();
        _searchHits = [];
        RefreshList();
        if (string.IsNullOrEmpty(query)) return;
        await Task.Delay(180);
        if (version != _searchVersion) return;
        var ids = _items.Where(room => room.IndexedPreview is not null).Select(room => room.Id).ToArray();
        var hits = await Task.Run(() =>
        {
            var found = new HashSet<Guid>();
            foreach (var id in ids)
            {
                if (Volatile.Read(ref _searchVersion) != version) break;
                try { if (RoomMatches(store.ReadRoom(id), query)) found.Add(id); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { ErrorLog.Write(error, "Searching sessions"); }
            }
            return found;
        });
        if (version == _searchVersion) { _searchHits = hits; RefreshList(); }
    }

    /// <summary>A session matches a search by its title or the text of any message.</summary>
    internal static bool RoomMatches(DiscussionRoom room, string? query) =>
        string.IsNullOrWhiteSpace(query) || Matches(room.Title, query)
        || room.Messages.Any(message => message.Text.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase));

    /// <summary>The session as Markdown: title, bindings, then every message with sender, recipient and time.</summary>
    internal static string ToMarkdown(DiscussionRoom room)
    {
        var text = new System.Text.StringBuilder();
        text.Append("# ").AppendLine(room.Title).AppendLine();
        text.AppendLine($"- Created: {room.Created.ToLocalTime():g}");
        if (room.CodexChatTitle is { } codex) text.AppendLine($"- Codex chat: {codex}");
        if (room.Claude is { } claude) text.AppendLine($"- Claude session: {claude.Title}");
        foreach (var message in room.Messages)
        {
            if (message.Command is { } command)
            {
                text.AppendLine().AppendLine($"## Claude CLI → You · {command}").AppendLine().AppendLine(message.Text);
                continue;
            }
            var to = message.To is { } target ? $" → {target}" : message.Sender == DiscussionMember.Boss ? " → Both" : "";
            var flags = message.Failed ? " (did not answer)" : message.Interrupted ? " (interrupted)" : message.ForwardedFrom is { } from ? $" (forwarded from {from})" : "";
            text.AppendLine().AppendLine($"## {message.Sender}{to}{flags} · {message.Time.ToLocalTime():g}").AppendLine().AppendLine(message.Text.TrimEnd());
        }
        return text.ToString();
    }

    private async Task ExportAsync()
    {
        if (_room is not { } room || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var name = string.Concat(room.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export session", SuggestedFileName = (name.Length == 0 ? "session" : name) + ".md", DefaultExtension = "md",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Markdown") { Patterns = ["*.md"] }, new Avalonia.Platform.Storage.FilePickerFileType("Text") { Patterns = ["*.txt"] }],
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(ToMarkdown(room));
            _status.Text = $"Exported to {file.Name}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _status.Text = "Could not save the file."; }
    }

    private void RefreshListNow()
    {
        _items =_items.OrderByDescending(room => room.Updated).ToList();
        _roomRows.Children.Clear();
        var query = _roomSearch.Text;
        foreach (var room in _items)
        {
            if (room.IndexedPreview is null ? !RoomMatches(room, query) && !ReferenceEquals(room, _room) : !string.IsNullOrWhiteSpace(query) && !Matches(room.Title, query) && !_searchHits.Contains(room.Id)) continue;
            var row = new Button { Content = RoomItem(room) };
            row.Classes.Add("nav");
            TrayvoraTheme.SetSelected(row, ReferenceEquals(room, _room));
            Avalonia.Automation.AutomationProperties.SetName(row, room.Title);
            row.Click += async (_, _) => { if (!ReferenceEquals(room, _room)) await OpenSavedAsync(room); };
            var rename = new MenuItem { Header = "Rename", InputGesture = new KeyGesture(Key.F2) };
            rename.Click += (_, _) => StartRename(room, row);
            row.ContextMenu = new ContextMenu { Items = { rename } };
            row.KeyDown += (_, args) =>
            {
                if (args.Key != Key.F2) return;
                args.Handled = true;
                StartRename(room, row);
            };
            _roomRows.Children.Add(row);
        }
        _roomsEmpty.IsVisible = _items.Count == 0;
    }

    private void NewRoom()
    {
        try
        {
            var room = store.Create(defaultClaude());
            _items.Insert(0, room);
            if (_room is null) _drafts[room.Id] = new(_editor.Text ?? "", Attachments.Files.ToArray(), _target, _relayFromCodex, _relayFromClaude);
            Open(room);
            RefreshList();
            _title.Focus();
            _title.SelectAll();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _status.Text = "Could not create a session. Check access to the settings folder."; }
    }

    private int _openVersion;
    private async Task OpenSavedAsync(DiscussionRoom? row)
    {
        var opening = ++_openVersion;
        if (row?.IndexedPreview is null) { Open(row); return; }
        try
        {
            var full = await Task.Run(() => store.ReadRoom(row.Id));
            var index = _items.IndexOf(row);
            if (opening != _openVersion || index < 0) return;
            _items[index] = full;
            Open(full);
        }
        catch (Exception error) { _status.Text = error.Message; }
    }

    private sealed record Draft(string Text, string[] Files, DiscussionTarget To, bool RelayCodex, bool RelayClaude);
    private readonly Dictionary<Guid, Draft> _drafts = [];

    private void Open(DiscussionRoom? room)
    {
        _openVersion++;
        _commandMode.IsChecked = false;
        if (_room is { } previous && _items.Contains(previous))
            _drafts[previous.Id] = new(_editor.Text ?? "", Attachments.Files.ToArray(), _target, _relayFromCodex, _relayFromClaude);
        _room = room;
        var draft = room is null ? null : _drafts.GetValueOrDefault(room.Id);
        _editor.Text = draft?.Text ?? "";
        Attachments.Restore(draft?.Files ?? []);
        _relayFromCodex = draft?.RelayCodex ?? false;
        _relayFromClaude = draft?.RelayClaude ?? false;
        SetTarget(draft?.To ?? DiscussionTarget.Codex);
        _confirm.IsVisible = false;
        _status.Text = "";
        _live.Clear();
        _messages.Children.Clear();
        _rendered.Clear();
        _title.Text = room?.Title ?? "";
        if (_header is not null) _header.IsVisible = room is not null;
        UpdateHeader();
        _title.IsEnabled = room is not null;
        RefreshList();
        FillCodexChoices();
        FillClaudeChoices();
        if (room is null || room.Messages.Count == 0) ShowEmptyState(room is null);
        else RenderLatest(room);
        ShowRunState();
        ScrollToEnd(force: true);
    }

    private void RenderLatest(DiscussionRoom room)
    {
        _shownFrom = Math.Max(0, room.Messages.Count - PageSize);
        UpdateEarlier();
        for (var index = _shownFrom; index < room.Messages.Count; index++) Render(room.Messages[index], scroll: false);
    }

    private void UpdateEarlier()
    {
        _messages.Children.Remove(_earlier);
        if (_shownFrom <= 0) return;
        _earlier.Content = $"Show earlier messages ({_shownFrom:N0})";
        _messages.Children.Insert(0, _earlier);
    }

    private void ShowEarlier()
    {
        if (_room is not { } room || _shownFrom <= 0) return;
        var from = Math.Max(0, _shownFrom - PageSize);
        var insertAt = _messages.Children.IndexOf(_earlier) + 1;
        for (var index = from; index < _shownFrom; index++, insertAt++)
            Render(room.Messages[index], scroll: false, insertAt: insertAt);
        _shownFrom = from;
        UpdateEarlier();
    }

    private Task<bool> Persist(DiscussionRoom room)
    {
        var snapshot = new DiscussionRoom
        {
            Id = room.Id, Title = room.Title, Created = room.Created, Updated = room.Updated,
            CodexChatId = room.CodexChatId, CodexDesktop = room.CodexDesktop, CodexChatTitle = room.CodexChatTitle,
            ClaudeEffort = room.ClaudeEffort, ClaudeModel = room.ClaudeModel,
            Claude = room.Claude, Messages = [.. room.Messages],
            ClaudePermissions = room.ClaudePermissions, NewCodexFolder = room.NewCodexFolder, CodexWorkspace = room.CodexWorkspace,
        };
        return Enqueue(() => store.Save(snapshot), "Could not save this session. Check access to the settings folder.");
    }

    private Task<bool> Enqueue(Action write, string failure)
    {
        var result = _saves.ContinueWith(_ =>
        {
            try { write(); return true; }
            catch (Exception error)
            {
                // Never silent: the reason goes to the Error log and the window says the session was not saved.
                ErrorLog.Write(error, "Sessions · save");
                Dispatcher.UIThread.Post(() => _status.Text = failure);
                return false;
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _saves = result;
        return result;
    }

    /// <summary>Turns a session row into a text box: Enter or leaving it saves the name, Escape keeps the old one.</summary>
    private async void StartRename(DiscussionRoom room, Button row)
    {
        if (room.IndexedPreview is not null)
        {
            try
            {
                var full = await Task.Run(() => store.ReadRoom(room.Id));
                var i = _items.IndexOf(room);
                if (i < 0) return;
                _items[i] = room = full;
            }
            catch (Exception error) { _status.Text = error.Message; return; }
        }
        var box = new TextBox { Text = room.Title, MaxLength = 120, MinWidth = 120 };
        TrayvoraTheme.StyleControls(box);
        Avalonia.Automation.AutomationProperties.SetName(box, "Session name");
        var done = false;
        void Finish(bool save)
        {
            if (done) return;
            done = true;
            var title = (box.Text ?? "").Trim();
            if (save && title.Length > 0 && title != room.Title)
            {
                room.Title = title;
                _ = Persist(room);
                if (ReferenceEquals(room, _room)) _title.Text = title;
            }
            RefreshListNow();
        }
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { args.Handled = true; Finish(true); }
            else if (args.Key == Key.Escape) { args.Handled = true; Finish(false); }
        };
        box.LostFocus += (_, _) => Finish(true);
        // Swap the row for the box (not inside the button, which would treat Space and Enter as clicks).
        var index = _roomRows.Children.IndexOf(row);
        if (index < 0) return;
        _roomRows.Children[index] = box;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void RenameRoom()
    {
        if (_room is not { } room) return;
        var title = (_title.Text ?? "").Trim();
        if (title.Length == 0) { _title.Text = room.Title; return; }
        if (title == room.Title) return;
        room.Title = title;
        _ = Persist(room);
        RefreshList();
    }

    private void DeleteRoom()
    {
        if (_room is not { } room || CurrentRun is not null) return;
        var id = room.Id;
        _ = Enqueue(() => store.Delete(id), "Could not delete this session.");
        _items.Remove(room);
        _drafts.Remove(room.Id);
        RefreshList();
        _ = OpenSavedAsync(_items.FirstOrDefault());
    }

    // ── Bindings ─────────────────────────────────────────────────────────

    private void FillCodexChoices()
    {
        _switching = true;
        try
        {
            if (_room is not { } room) { _codexChat.ItemsSource = null; return; }
            var options = new List<CodexChatOption> { new(room.Id, OwnThreadTitle, false) };
            try { options.AddRange(codexChats().Select(chat => new CodexChatOption(chat.Id, $"Trayvora · {chat.Title}", false))); }
            catch (Exception) { }
            options.AddRange(_desktopItems.Select(chat => new CodexChatOption(chat.Id, $"Codex Desktop · {chat.Title}", true)));
            var selected = options.FirstOrDefault(option => option.Id == room.CodexChatId && option.Desktop == room.CodexDesktop);
            if (selected is not null && selected.Id != room.Id && selected.Title != room.CodexChatTitle)
            {
                room.CodexChatTitle = selected.Title;
                _ = Persist(room);
            }
            options = options.Where(option => option == selected || Matches(option.Title, _codexSearch.Text) || option.Id.ToString().Equals(_codexSearch.Text?.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (selected is null)
            {
                selected = new CodexChatOption(room.CodexChatId,
                    room.CodexChatTitle ?? (room.CodexDesktop ? "Codex Desktop chat" : "Saved Codex chat"), room.CodexDesktop);
                options.Insert(1, selected);
            }
            _codexChat.ItemsSource = options;
            _codexChat.SelectedItem = selected;
        }
        finally { _switching = false; }
    }

    /// <summary>Search by title: every word must appear, in any order and case.</summary>
    internal static bool Matches(string title, string? query) =>
        string.IsNullOrWhiteSpace(query) ||
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => title.Contains(word, StringComparison.CurrentCultureIgnoreCase));

    private void FillClaudeChoices()
    {
        _switching = true;
        try
        {
            if (_room is not { } room) { _claudeChat.ItemsSource = null; return; }
            _claudeRights.SelectedIndex = (int)room.ClaudePermissions;
            // A chat renamed in Claude Desktop keeps its id; take the new title for the saved binding.
            if (room.Claude is { } saved && _claudeItems.FirstOrDefault(chat => chat.Id == saved.Id) is { } fresh && fresh != saved)
            {
                room.Claude = fresh;
                _ = Persist(room);
            }
            var items = _claudeItems.Where(chat => Matches(chat.Title, _claudeSearch.Text) || chat.Id == room.Claude?.Id).ToList();
            if (room.Claude is { } bound && items.All(chat => chat.Id != bound.Id)) items.Insert(0, bound);
            _claudeChat.ItemsSource = items;
            _claudeChat.SelectedItem = room.Claude is { } selected ? items.First(chat => chat.Id == selected.Id) : null;
        }
        finally { _switching = false; }
    }

    private void BindCodex()
    {
        if (_switching || _room is not { } room || _codexChat.SelectedItem is not CodexChatOption option
            || (option.Id == room.CodexChatId && option.Desktop == room.CodexDesktop)) return;
        room.NewCodexFolder = null;
        room.CodexWorkspace = _desktopItems.FirstOrDefault(item => item.Id == option.Id)?.Workspace;
        room.CodexChatId = option.Id;
        room.CodexDesktop = option.Desktop;
        room.CodexChatTitle = option.Id == room.Id ? null : option.Title;
        _ = Persist(room);
        _status.Text = $"Codex in this session now uses: {option.Title}.";
        UpdateHeader();
    }

    private void BindClaude()
    {
        if (_switching || _room is not { } room || _claudeChat.SelectedItem is not ClaudeChat chat || chat.Id == room.Claude?.Id) return;
        room.Claude = chat;
        _ = Persist(room);
        _status.Text = $"Claude in this session now uses: {chat.Title}.";
        UpdateHeader();
    }

    private async Task LoadDesktopAsync()
    {
        if (desktopChats is null) return;
        _loadDesktop.IsEnabled = false;
        _loadDesktop.Content = "Loading…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _desktopItems = await desktopChats(timeout.Token);
            FillCodexChoices();
            _status.Text = $"Found {_desktopItems.Count} Codex Desktop chats.";
            _codexChat.IsDropDownOpen = _desktopItems.Count > 0;
        }
        catch (Exception)
        { _status.Text = "Could not list Codex Desktop chats. Check the Desktop profile folder in the main window."; }
        finally
        {
            _loadDesktop.Content = "Load Desktop chats";
            UpdateComposer();
        }
    }

    private async Task LoadClaudeAsync()
    {
        _loadClaude.IsEnabled = false;
        _loadClaude.Content = "Loading…";
        try
        {
            _claudeItems = await claudeChats();
            FillClaudeChoices();
            _status.Text = $"Found {_claudeItems.Count} Claude chats.";
            _claudeChat.IsDropDownOpen = _claudeItems.Count > 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _status.Text = error is IOException ? error.Message : "Could not read Claude chats."; }
        finally
        {
            _loadClaude.Content = "Load Claude chats";
            UpdateComposer();
        }
    }

    // ── Composer ─────────────────────────────────────────────────────────

    /// <summary>"?" next to the chips: the key phrases that pass a reply on without the ⇄ toggle.</summary>
    private static Control RelayHint()
    {
        var hint = new TextBlock
        {
            Text = "\uE946", FontFamily = IconFont, FontSize = 14, Foreground = TrayvoraTheme.Muted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        };
        ToolTip.SetTip(hint, "Pass the reply on without the ⇄ toggle: end your message with a phrase such as\n"
            + string.Join("\n", KeyPhrases.Current.ForwardLines.Select(example => "  • " + example))
            + "\n\nLet them work it out together and send you one final answer:\n"
            + string.Join("\n", KeyPhrases.Current.DiscussLines.Select(example => "  • " + example))
            + "\n\nWorks for typed and dictated messages to Codex or Claude.");
        Avalonia.Automation.AutomationProperties.SetName(hint, "Relay phrases");
        return hint;
    }

    private static Button RelayToggle(DiscussionMember to)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "\uE8AB", FontFamily = IconFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = to.ToString(), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Classes.Add("chip");
        var tip = $"Pass the reply on to {to}";
        ToolTip.SetTip(button, $"{tip}. Or end your message with \"{(to == DiscussionMember.Claude ? "перешли Клоду" : "перешли Кодексу")}\".");
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    private static Control Joined(Button main, Button relay)
    {
        main.CornerRadius = new CornerRadius(14, 0, 0, 14);
        relay.CornerRadius = new CornerRadius(0, 14, 14, 0);
        relay.Margin = new Thickness(-1, 0, 0, 0);
        relay.Padding = new Thickness(8, 4, 10, 4);
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { main, relay } };
    }

    private bool RelayFor(DiscussionMember agent) => agent == DiscussionMember.Codex ? _relayFromCodex : _relayFromClaude;

    /// <summary>The ⇄ toggles that stay on after choosing <paramref name="target"/>: only the chosen agent's own.</summary>
    internal static (bool Codex, bool Claude) RelayAfterSwitch(DiscussionTarget target, bool codex, bool claude) =>
        (target == DiscussionTarget.Codex && codex, target == DiscussionTarget.Claude && claude);

    private void SetTarget(DiscussionTarget target)
    {
        _target = target;
        // Only the chosen recipient can pass its reply on: switching clears the other ⇄ toggle.
        (_relayFromCodex, _relayFromClaude) = RelayAfterSwitch(target, _relayFromCodex, _relayFromClaude);
        TrayvoraTheme.SetSelected(_toCodex, target == DiscussionTarget.Codex);
        TrayvoraTheme.SetSelected(_toClaude, target == DiscussionTarget.Claude);
        TrayvoraTheme.SetSelected(_toBoth, target == DiscussionTarget.Both);
        TrayvoraTheme.SetSelected(_relayCodex, _relayFromCodex);
        TrayvoraTheme.SetSelected(_relayClaude, _relayFromClaude);
        var relay = target switch
        {
            DiscussionTarget.Codex when _relayFromCodex => " — reply goes on to Claude",
            DiscussionTarget.Claude when _relayFromClaude => " — reply goes on to Codex",
            _ => "",
        };
        _editor.PlaceholderText = target switch
        {
            DiscussionTarget.Claude => $"Message Claude{relay}…",
            DiscussionTarget.Both => "Message Codex and Claude…",
            _ => $"Message Codex{relay}…",
        };
    }

    private void UpdateComposer()
    {
        var length = _editor.Text?.Length ?? 0;
        _counter.Text = length == 0 ? "" : length > MaxLength ? $"{length:N0} · sent as a .txt file" : $"{length:N0}";
        _counter.Foreground = length > MaxTypedLength ? TrayvoraTheme.Danger : TrayvoraTheme.Faint;
        var idle = CurrentRun is null && _commandCancel is null;
        _commandMode.IsEnabled = CurrentRun is null && _dictationStop is null && !Attachments.Copying;
        var open = _room is not null;
        var files = _attachments?.Files.Count ?? 0;
        if (Attachments.Copying) _counter.Text = "Copying attachment…";
        _send.IsEnabled = idle && !Attachments.Copying && length <= MaxTypedLength && (files > 0 || (length > 0 && !string.IsNullOrWhiteSpace(_editor.Text)));
        _pause.Content = Attachments.Copying ? "Cancel copy" : "Pause";
        _pause.IsVisible = CurrentRun is not null || Attachments.Copying;
        _delete.IsEnabled = idle && open;
        _toCodex.IsEnabled = _toClaude.IsEnabled = _toBoth.IsEnabled = _relayCodex.IsEnabled = _relayClaude.IsEnabled = idle;
        _codexChat.IsEnabled = _claudeChat.IsEnabled = _codexSearch.IsEnabled = _claudeSearch.IsEnabled = _claudeRights.IsEnabled = _newCodex.IsEnabled = _newClaude.IsEnabled = idle && open;
        _loadDesktop.IsEnabled = idle && open && !Equals(_loadDesktop.Content, "Loading…");
        _loadClaude.IsEnabled = idle && open && !Equals(_loadClaude.Content, "Loading…");
    }

    private void ShowEmptyState(bool noRooms)
    {
        Control Tip(string glyph, string title, string text) => new Border
        {
            Background = TrayvoraTheme.Surface, BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), Width = 220,
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 18, Foreground = TrayvoraTheme.Accent },
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13 },
                    new TextBlock { Text = text, FontSize = 12, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
        var avatars = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = -8, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                TrayvoraTheme.Avatar("Y", TrayvoraTheme.BossColor, 44),
                TrayvoraTheme.Avatar("Cx", TrayvoraTheme.CodexColor, 44),
                TrayvoraTheme.Avatar("Cl", TrayvoraTheme.ClaudeColor, 44),
            },
        };
        var tips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, ItemSpacing = 10, LineSpacing = 10 };
        tips.Children.AddRange([
            Tip(TrayvoraTheme.IconChat, "One view of everything", "The session shows every message to and from Codex and Claude, saved until you delete it."),
            Tip(TrayvoraTheme.IconCode, "Separate context", "Each agent works in its own chat and only sees what is sent to it. Hover a message to forward it."),
            Tip(TrayvoraTheme.IconGroup, "Parallel sessions", "Pick different Codex and Claude chats in another session and talk in both at once."),
        ]);
        _messages.Children.Add(new StackPanel
        {
            Spacing = 14, Margin = new Thickness(0, 50, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                avatars,
                new TextBlock { Text = noRooms ? "Start your first session" : "Start the conversation", FontSize = 18, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = noRooms ? "Type a message below — a new saved session is created automatically." : "Choose the Codex and Claude chats above, then write a message.", Foreground = TrayvoraTheme.Muted, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                tips,
            },
        });
    }

    private void UpdateMic()
    {
        _commandMode.IsEnabled = CurrentRun is null && _dictationStop is null && _attachments?.Copying != true;
        var recording = _dictationStop is not null && !_dictationTranscribing;
        _mic.Content = new TextBlock
        {
            Text = recording ? TrayvoraTheme.IconStop : TrayvoraTheme.IconMic,
            FontFamily = IconFont, FontSize = 16,
        };
        if (recording && !_mic.Classes.Contains("recording")) _mic.Classes.Add("recording");
        if (!recording) _mic.Classes.Remove("recording");
        _mic.IsEnabled = !_dictationTranscribing;
        var tip = _dictationTranscribing ? "Transcribing…" : recording ? "Stop and insert the transcript" : "Dictate a message";
        ToolTip.SetTip(_mic, tip);
        Avalonia.Automation.AutomationProperties.SetName(_mic, tip);
    }

    private async Task ToggleDictationAsync()
    {
        if (dictate is null || _dictationTranscribing) return;
        if (_dictationStop is { } stop)
        {
            stop.Cancel();
            return;
        }

        StopSpeaking?.Invoke();
        if (_room is null) NewRoom();
        if (_room is not { } origin) return;
        var target = _target;
        var relays = (_relayFromCodex, _relayFromClaude);
        var codex = origin.CodexChatId;
        var claude = origin.Claude?.Id;
        var voiceSend = _voiceMode.IsChecked == true;
        using var stopRecording = new CancellationTokenSource();
        using var cancel = new CancellationTokenSource();
        _dictationStop = stopRecording;
        _dictationCancel = cancel;
        var started = DateTime.Now;
        _level.Value = 0;
        _level.IsVisible = true;
        _status.Text = "Listening… press the microphone again to stop (Esc cancels).";
        UpdateMic();
        var level = new Progress<double>(peak =>
        {
            if (!ReferenceEquals(_dictationStop, stopRecording) || _dictationTranscribing) return;
            _level.Value = peak * 100;
            _status.Text = $"Listening · {DateTime.Now - started:mm\\:ss} · press the microphone again to stop.";
        });
        stopRecording.Token.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_dictationStop, stopRecording)) return;
            _dictationTranscribing = true;
            _level.IsVisible = false;
            _status.Text = "Transcribing…";
            UpdateMic();
        }));
        try
        {
            var text = await dictate(stopRecording.Token, cancel.Token, level);
            if (string.IsNullOrWhiteSpace(text))
            {
                _status.Text = "No speech detected.";
                return;
            }
            if (!ReferenceEquals(origin, _room))
            {
                if (_items.Contains(origin))
                {
                    var draft = _drafts.GetValueOrDefault(origin.Id) ?? new Draft("", [], target, false, false);
                    _drafts[origin.Id] = draft with { Text = (draft.Text + " " + text).Trim() };
                    _status.Text = $"Transcript saved in {origin.Title}.";
                }
                return;
            }
            var current = _editor.Text ?? "";
            _editor.Text = string.IsNullOrWhiteSpace(current) ? text : current.TrimEnd() + " " + text;
            _editor.CaretIndex = _editor.Text.Length;
            _status.Text = "Transcript inserted. Edit it if needed, then press Enter to send.";
            _editor.Focus();
            if (voiceSend && _send.IsEnabled && relays == (_relayFromCodex, _relayFromClaude) && target == _target && origin.CodexChatId == codex && origin.Claude?.Id == claude) await PostAsync();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        { _status.Text = "Dictation cancelled."; }
        catch (IOException error) { _status.Text = error.Message; } // recognition failures are logged by the recognizer
        catch (Exception error)
        {
            ErrorLog.Write(error, "Sessions · dictation");
            _status.Text = "Could not record. Check the microphone in the main window.";
        }
        finally
        {
            _dictationStop = null;
            _dictationCancel = null;
            _dictationTranscribing = false;
            _level.IsVisible = false;
            UpdateMic();
        }
    }

    private async Task PostAsync()
    {
        if (_commandMode.IsChecked == true || _commandCancel is not null) return;
        var attached = Attachments.Files.ToArray();
        if (Attachments.Copying || CurrentRun is not null || (string.IsNullOrWhiteSpace(_editor.Text) && attached.Length == 0)) return;
        var text = (_editor.Text ?? "").Trim();
        if (text.Length == 0) text = attached.All(ComposerAttachments.IsImage) ? "Look at the attached image(s)." : "Look at the attached file(s).";
        if (text.Length > MaxTypedLength)
        {
            _status.Text = $"Keep each message under {MaxTypedLength:N0} characters.";
            return;
        }
        if (_room is null) NewRoom();
        if (_room is not { } room) return;

        // An explicit "Claude:" / "Codex:" prefix overrides the chip selection.
        var targets = new List<DiscussionMember>();
        var addressed = Recipient(text);
        if (addressed is DiscussionMember.Codex or DiscussionMember.Claude)
        {
            targets.Add(addressed.Value);
            text = StripAddress(text);
        }
        else if (_target == DiscussionTarget.Both) targets.AddRange([DiscussionMember.Codex, DiscussionMember.Claude]);
        else targets.Add(_target == DiscussionTarget.Claude ? DiscussionMember.Claude : DiscussionMember.Codex);
        // Pass the reply on to the other agent: a key phrase at the end ("… перешли Клоду") or the ⇄ toggle.
        DiscussionMember? relayTo = null;
        DiscussionPlan? discuss = null;
        var sent = text;
        if (targets.Count == 1)
        {
            var other = targets[0] == DiscussionMember.Codex ? DiscussionMember.Claude : DiscussionMember.Codex;
            if (RelayPhrases.DetectDiscussion(text) == other)
                discuss = new DiscussionPlan(targets[0], other, DiscussExchanges);
            else if (RelayPhrases.Detect(text) is { } phrase && phrase.Target == other)
            {
                relayTo = other;
                sent = phrase.Text;
            }
            else if (addressed is null && RelayFor(targets[0])) relayTo = other;
        }
        if (!Ready(room, (relayTo ?? discuss?.Partner) is { } partnerAgent ? [.. targets, partnerAgent] : targets)) return;

        if (room.Title == DiscussionStore.DefaultTitle && room.Messages.All(m => m.Command is not null))
        {
            room.Title = DiscussionStore.TitleFrom(text);
            _title.Text = room.Title;
        }
        // Files attached while another session was open are moved into this session's folder first.
        try { attached = [.. Attachments.MoveInto(AttachmentsFolder(room))]; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Could not move the attachments into this session: " + error.Message;
            return;
        }
        _editor.Clear();
        text = ComposerAttachments.WithFiles(text, attached);
        sent = ComposerAttachments.WithFiles(sent, attached);
        Attachments.Clear();
        _ = Append(room, new DiscussionMessage(DiscussionMember.Boss, targets.Count == 2 ? null : targets[0], text, DateTimeOffset.Now,
            RelayTo: relayTo, DiscussWith: discuss?.Partner));
        await RunAsync(room, targets.Select(agent => new Delivery(agent, DiscussionMember.Boss, sent, null, relayTo is not null, discuss, attached)));
    }

    private async Task ForwardAsync(DiscussionMessage message, DiscussionMember target)
    {
        if (_room is not { } room) return;
        if (CurrentRun is not null)
        {
            _status.Text = "Wait for this session to finish, or pause it, before forwarding.";
            return;
        }
        if (!Ready(room, [target])) return;
        _ = Append(room, new DiscussionMessage(DiscussionMember.Boss, target, message.Text, DateTimeOffset.Now, message.Sender));
        await RunAsync(room, [new Delivery(target, DiscussionMember.Boss, message.Text, message.Sender,
            Files: ComposerAttachments.FilesIn(message.Text, AttachmentsFolder(room)))]);
    }

    private bool Ready(DiscussionRoom room, IEnumerable<DiscussionMember> targets)
    {
        if (_commandMode.IsChecked == true || _commandCancel is not null)
        {
            _status.Text = "Exit command mode to send a message.";
            return false;
        }
        if (!targets.Contains(DiscussionMember.Claude) || room.Claude is not null) return true;
        _status.Text = "Choose a Claude chat for this session first (Load Claude chats).";
        return false;
    }

    private async Task RunAsync(DiscussionRoom room, IEnumerable<Delivery> deliveries)
    {
        var queue = new Queue<Delivery>(deliveries);
        using var cancel = new CancellationTokenSource();
        var run = new RoomRun(cancel);
        _runs[room.Id] = run;
        if (ReferenceEquals(room, _room)) _status.Text = "";
        Changed(room);
        var replies = 0;
        string? outcome = null;
        // A discussion needs room for every exchange (lead → partner → lead) plus the final answer.
        var discussRoundsForRun = queue.Select(item => item.Discuss?.ExchangesLeft ?? 0).DefaultIfEmpty().Max();
        var limit = discussRoundsForRun > 0 ? 2 * discussRoundsForRun + 2 : MaxAgentReplies;
        var partnerAsked = false;
        var stop = false;
        try
        {
            while (queue.Count > 0 && replies < limit && !stop && !cancel.IsCancellationRequested)
            {
                // One wave: the next messages for different agents are delivered at the same time.
                var wave = new List<Delivery>();
                while (queue.Count > 0 && replies < limit && wave.All(item => item.To != queue.Peek().To))
                {
                    wave.Add(queue.Dequeue());
                    replies++;
                }
                await Task.WhenAll(wave.Select(Deliver).ToArray());
            }
            if (cancel.IsCancellationRequested) outcome = "Paused. Send a message to continue.";
            else if (queue.Count > 0 && outcome is null) outcome = "Automatic round ended. Reply or forward to continue.";
        }
        finally
        {
            _runs.Remove(room.Id);
            Changed(room);
            RunFinished?.Invoke(room, outcome);
            if (ReferenceEquals(room, _room))
            {
                _status.Text = outcome ?? "";
                _editor.Focus();
            }
        }

        async Task Deliver(Delivery delivery)
        {
            var agent = delivery.To;
            var stream = new ReplyStream();
            run.Live[agent] = stream;
            Changed(room);
            string? answer;
            try
            {
                var (body, file) = await Task.Run(() => ForAgentWithFile(delivery.Text, AttachmentsFolder(room), $"{delivery.From}-to-{agent}"));
                // The agent gets the files themselves, not paths parsed back out of the text.
                IReadOnlyList<string> files = file is null ? delivery.Files ?? [] : [file, .. delivery.Files ?? []];
                answer = await send(agent, Envelope(room, agent, delivery.From, body, delivery.ForwardedFrom, delivery.Relay, delivery.Discuss),
                    room, stream, files, cancel.Token);
            }
            catch (Exception error)
            {
                // Keep what the agent had already written, then record why it stopped (a pause is not a failure).
                var partial = stream.Text.Trim();
                // Claude Code prints its own error (for example a sign-in failure) as reply text; show it once, as the failure.
                var echo = error is IOException && partial.Length > 0 && error.Message.Contains(partial, StringComparison.Ordinal);
                if (partial.Length > 0 && !echo)
                    _ = Append(room, new DiscussionMessage(agent, DiscussionMember.Boss, partial, DateTimeOffset.Now, Note: stream.Notes, Interrupted: true));
                if (!cancel.IsCancellationRequested)
                {
                    var reason = error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException
                        or InvalidDataException or TimeoutException
                        ? error.Message : $"The request failed ({error.GetType().Name}). Check the selected chat, profile and sign-in.";
                    _ = Append(room, new DiscussionMessage(agent, null, reason, DateTimeOffset.Now, Failed: true));
                    ErrorLog.Write(error, $"Sessions · {agent}", $"room {room.Id}, chat {(agent == DiscussionMember.Claude ? room.Claude?.Id : room.CodexChatId)}");
                }
                return;
            }
            finally
            {
                run.Live.Remove(agent);
                Changed(room);
            }
            if (answer is null)
            {
                // Codex Desktop has not finished in time: say so in the session, not only in the status line.
                var chatName = room.CodexChatTitle ?? "the selected Codex Desktop chat";
                var partial = stream.Text.Trim();
                _ = Append(room, new DiscussionMessage(agent, DiscussionMember.Boss,
                    partial.Length > 0 ? partial : $"Sent to {chatName}. It has not answered here yet; the reply will appear in Codex Desktop.",
                    DateTimeOffset.Now, Note: $"Still running in Codex Desktop ({chatName}). Open that chat there to see the rest.", Interrupted: partial.Length > 0));
                outcome = $"Codex is still working in Codex Desktop. Its reply will appear in {chatName}.";
                return;
            }
            Handle(delivery, answer.Trim(), stream.Notes);
        }

        void Handle(Delivery delivery, string answer, string? note)
        {
            var agent = delivery.To;
            var next = Recipient(answer);
            var body = next is null ? answer : StripAddress(answer);
            if (delivery.Discuss is { } plan)
            {
                if (agent == plan.Partner)
                {
                    // The partner always answers the lead.
                    _ = Append(room, new DiscussionMessage(agent, plan.Lead, body, DateTimeOffset.Now, Note: note));
                    queue.Enqueue(new Delivery(plan.Lead, agent, body, null, Discuss: plan));
                }
                else if (next == plan.Partner && plan.ExchangesLeft > 0)
                {
                    if (plan.Partner == DiscussionMember.Claude && room.Claude is null)
                    {
                        outcome = "Codex wants to discuss with Claude, but this session has no Claude chat selected.";
                        _ = Append(room, new DiscussionMessage(agent, DiscussionMember.Boss, body, DateTimeOffset.Now, Note: note));
                        stop = true;
                        return;
                    }
                    _ = Append(room, new DiscussionMessage(agent, plan.Partner, body, DateTimeOffset.Now, Note: note));
                    queue.Enqueue(new Delivery(plan.Partner, agent, body, null, Discuss: plan with { ExchangesLeft = plan.ExchangesLeft - 1 }));
                    partnerAsked = true;
                }
                else
                {
                    // The lead's answer to Boss (or anything after the last exchange) ends the discussion.
                    _ = Append(room, new DiscussionMessage(agent, DiscussionMember.Boss, body, DateTimeOffset.Now, Final: true, Note: note));
                    outcome = partnerAsked
                        ? $"{plan.Lead} and {plan.Partner} finished the discussion. {plan.Lead} sent you the final answer."
                        : $"{plan.Lead} answered without asking {plan.Partner}.";
                }
                return;
            }
            if (next == agent || next == DiscussionMember.Boss) next = null;
            if (delivery.Relay) next = agent == DiscussionMember.Codex ? DiscussionMember.Claude : DiscussionMember.Codex;
            _ = Append(room, new DiscussionMessage(agent, next ?? DiscussionMember.Boss, body, DateTimeOffset.Now, Note: note));
            if (next is not { } handoff) return;
            if (handoff == DiscussionMember.Claude && room.Claude is null)
            {
                outcome = "Codex addressed Claude, but this session has no Claude chat selected.";
                stop = true;
                return;
            }
            queue.Enqueue(new Delivery(handoff, agent, body, null));
        }
    }

    /// <summary>Refreshes the session list and, for the visible session, presence and live replies.</summary>
    private void Changed(DiscussionRoom room)
    {
        RefreshList();
        if (ReferenceEquals(room, _room)) ShowRunState();
    }

    private void ShowRunState()
    {
        var live = CurrentRun?.Live;
        foreach (var member in new[] { DiscussionMember.Codex, DiscussionMember.Claude })
            SetPresence(member, live?.ContainsKey(member) == true);
        foreach (var agent in _live.Keys.ToArray())
        {
            if (live is not null && live.TryGetValue(agent, out var stream) && ReferenceEquals(stream, _live[agent].Stream)) continue;
            _messages.Children.Remove(_live[agent].Row);
            _live.Remove(agent);
        }
        if (live is not null)
            foreach (var (agent, stream) in live)
                if (!_live.ContainsKey(agent)) AddLive(agent, stream);
        if (_live.Count > 0 && _liveTimer is null)
        {
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _liveTimer.Tick += (_, _) => UpdateLive();
            _liveTimer.Start();
        }
        else if (_live.Count == 0 && _liveTimer is not null)
        {
            _liveTimer.Stop();
            _liveTimer = null;
        }
        UpdateComposer();
    }

    private string? WorkspaceFor(DiscussionRoom room, DiscussionMember member) => member == DiscussionMember.Claude
        ? room.Claude?.Workspace : room.CodexDesktop ? room.NewCodexFolder ?? room.CodexWorkspace
            ?? _desktopItems.FirstOrDefault(item => item.Id == room.CodexChatId)?.Workspace
        : Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(AppContext.BaseDirectory, "codex-data"), "workspace");

    private Task<bool> Append(DiscussionRoom room, DiscussionMessage message)
    {
        if (_voiceMode.IsChecked == true && Speak is { } speak && ReferenceEquals(room, _room) && !message.Failed
            && message.Command is null && message.DeliveryId is null && message.Sender != DiscussionMember.Boss && message.To is null or DiscussionMember.Boss && message.Text.Length > 0)
            _ = speak(message.Text);
        if (message.Command is null && message.Sender != DiscussionMember.Boss)
            message = message with { Workspace = WorkspaceFor(room, message.Sender),
                DesktopThread = room.CodexDesktop && message.Sender == DiscussionMember.Codex,
                ThreadId = message.Sender == DiscussionMember.Claude ? room.Claude?.Id : room.CodexChatId };
        room.Messages.Add(message);
        room.Updated = message.Time;
        var saved = Persist(room);
        if (ReferenceEquals(room, _room))
        {
            if (room.Messages.Count == 1) { _messages.Children.Clear(); _rendered.Clear(); }
            // Finished messages go above the replies that are still being written.
            foreach (var bubble in _live.Values) _messages.Children.Remove(bubble.Row);
            Render(message, scroll: false);
            foreach (var bubble in _live.Values) _messages.Children.Add(bubble.Row);
            ScrollToEnd(force: message.Sender == DiscussionMember.Boss);
        }
        RefreshList();
        return saved;
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void SetPresence(DiscussionMember member, bool thinking)
    {
        if (!_presence.TryGetValue(member, out var item)) return;
        item.Dot.Background = thinking ? TrayvoraTheme.Warning : TrayvoraTheme.Success;
        item.State.Text = thinking ? "answering…" : "ready";
    }

    private void AddLive(DiscussionMember agent, ReplyStream stream)
    {
        var view = new MarkdownView { IsVisible = false };
        var waiting = new TextBlock { Text = "•", FontSize = 18, Foreground = MemberColor(agent), LetterSpacing = 2 };
        var steps = new TextBlock { FontSize = 11.5, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false, LineHeight = 17 };
        var activity = new TextBlock { FontSize = 11, Foreground = TrayvoraTheme.Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = DisplayName(agent), FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = MemberColor(agent) }, activity },
        };
        var bubble = new Border
        {
            Background = TrayvoraTheme.Surface, BorderBrush = MemberColor(agent), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4, 14, 14, 14), Padding = new Thickness(14, 10), MaxWidth = BubbleWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Spacing = 6, Children = { steps, waiting, view } },
        };
        var avatar = TrayvoraTheme.Avatar(Initials(agent), MemberColor(agent), 32);
        avatar.VerticalAlignment = VerticalAlignment.Top;
        var column = new StackPanel { Spacing = 4, Children = { meta, bubble } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(0, 0, 80, 0) };
        Grid.SetColumn(column, 1);
        row.Children.AddRange([avatar, column]);
        _live[agent] = new LiveBubble(stream, row, view, activity, waiting, steps);
        _messages.Children.Add(row);
        UpdateLive();
        ScrollToEnd();
    }

    /// <summary>Copies the latest text of every live reply into its bubble (called on a short timer).</summary>
    private void UpdateLive()
    {
        var grew = false;
        foreach (var bubble in _live.Values)
        {
            var (text, activity, version) = bubble.Stream.Snapshot();
            var seconds = (int)(DateTime.Now - bubble.Started).TotalSeconds;
            var doing = activity ?? (text.Length == 0 ? "is working" : "is writing");
            bubble.Activity.Text = seconds < 1 ? doing + "…" : $"{doing}… · {seconds} s";
            if (text.Length == 0) bubble.Waiting.Text = new string('•', 1 + (int)(DateTime.Now - bubble.Started).TotalMilliseconds / 380 % 3);
            var (recent, total) = bubble.Stream.Steps;
            var steps = ReplyStream.FormatSteps(recent, total, running: activity is not null);
            if (bubble.Steps.Text != steps)
            {
                bubble.Steps.Text = steps;
                bubble.Steps.IsVisible = steps.Length > 0;
                grew = true;
            }
            if (version == bubble.Version) continue;
            bubble.Version = version;
            bubble.Waiting.IsVisible = text.Length == 0;
            bubble.View.IsVisible = text.Length > 0;
            bubble.View.Show(text);
            grew = true;
        }
        if (grew) ScrollToEnd();
    }

    private void Render(DiscussionMessage message, bool scroll = true, int insertAt = -1)
    {
        if (message.Command is { } command)
        {
            var result = new StackPanel { Spacing = 4, Margin = new Thickness(8), Children =
            {
                TrayvoraTheme.Hint($"Claude CLI → You   ·   {command}"),
                new SelectableTextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap }
            } };
            Place(message, result, scroll, insertAt);
            return;
        }
        if (message.Failed)
        {
            var failedAt = message.Time.ToLocalTime();
            var failedTools = LazyTools(alignRight: false);
            var notice = new StackPanel
            {
                Spacing = 4, Margin = new Thickness(42, 0, 80, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Children =
                {
                    new Border
                    {
                        Background = Brush.Parse("#2A1A22"), BorderBrush = Brush.Parse("#5A2635"), BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8),
                        Child = new StackPanel
                        {
                            Spacing = 3,
                            Children =
                            {
                                new TextBlock { Text = $"{DisplayName(message.Sender)} did not answer · {failedAt:HH:mm}", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#FF9DAE") },
                                new SelectableTextBlock { Text = message.Text, FontSize = 12.5, Foreground = Brush.Parse("#FFC2CC"), TextWrapping = TextWrapping.Wrap },
                            },
                        },
                    },
                    failedTools,
                },
            };
            AttachTools(notice, failedTools, message);
            Place(message, notice, scroll, insertAt);
            return;
        }

        var sender = message.Sender;
        var mine = sender == DiscussionMember.Boss;
        var color = MemberColor(sender);
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        meta.Children.Add(new TextBlock { Text = DisplayName(sender), FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = color });
        if (message.To is { } addressee)
            meta.Children.Add(new Border
            {
                Background = TrayvoraTheme.Elevated, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1),
                Child = new TextBlock { Text = $"→ {DisplayName(addressee)}", FontSize = 11, Foreground = MemberColor(addressee) },
            });
        else if (mine)
            meta.Children.Add(new Border
            {
                Background = TrayvoraTheme.Elevated, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1),
                Child = new TextBlock { Text = "→ Codex, Claude", FontSize = 11, Foreground = TrayvoraTheme.Muted },
            });
        if (message.DiscussWith is { } partnerBadge)
        {
            var badge = new Border
            {
                Background = TrayvoraTheme.AccentSoft, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1),
                Child = new TextBlock { Text = $"⇆ discuss with {DisplayName(partnerBadge)}", FontSize = 11, Foreground = MemberColor(partnerBadge) },
            };
            ToolTip.SetTip(badge, $"The agent works it out with {DisplayName(partnerBadge)} and sends you the final decision.");
            meta.Children.Add(badge);
        }
        if (message.Final)
            meta.Children.Add(new Border
            {
                Background = Brush.Parse("#173A2E"), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1),
                Child = new TextBlock { Text = "✓ final answer", FontSize = 11, Foreground = TrayvoraTheme.Success },
            });
        if (message.RelayTo is { } relayTarget)
        {
            var badge = new Border
            {
                Background = TrayvoraTheme.Elevated, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1),
                Child = new TextBlock { Text = $"⇄ then {DisplayName(relayTarget)}", FontSize = 11, Foreground = MemberColor(relayTarget) },
            };
            ToolTip.SetTip(badge, $"The reply is passed on to {DisplayName(relayTarget)}.");
            meta.Children.Add(badge);
        }
        if (message.ForwardedFrom is { } source)
            meta.Children.Add(new TextBlock { Text = $"forwarded from {DisplayName(source)}", FontSize = 11, FontStyle = FontStyle.Italic, Foreground = TrayvoraTheme.Muted, VerticalAlignment = VerticalAlignment.Center });
        if (message.Interrupted)
        {
            var badge = new Border
            {
                Background = TrayvoraTheme.Elevated, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1),
                Child = new TextBlock { Text = "⏸ interrupted", FontSize = 11, Foreground = TrayvoraTheme.Warning },
            };
            ToolTip.SetTip(badge, "The agent was stopped before it finished. This is what it had written.");
            meta.Children.Add(badge);
        }
        var local = message.Time.ToLocalTime();
        meta.Children.Add(new TextBlock
        {
            Text = local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("dd.MM HH:mm"),
            FontSize = 11, Foreground = TrayvoraTheme.Faint, VerticalAlignment = VerticalAlignment.Center,
        });

        var bubble = new Border
        {
            Background = mine ? TrayvoraTheme.AccentSoft : TrayvoraTheme.Surface,
            BorderBrush = mine ? Brush.Parse("#3A4380") : TrayvoraTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = mine ? new CornerRadius(14, 4, 14, 14) : new CornerRadius(4, 14, 14, 14),
            Padding = new Thickness(14, 10),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = BubbleWidth,
            // Agents write Markdown; what you typed is shown exactly as typed.
            Child = mine && message.ForwardedFrom is null
                ? new SelectableTextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 }
                : Markdown(message),
        };
        var tools = LazyTools(alignRight: mine);

        var column = new StackPanel { Spacing = 4, Children = { meta, bubble } };
        if (message.Note is { Length: > 0 } note)
            column.Children.Add(new TextBlock
            {
                Text = "⚠ " + note, FontSize = 11.5, Foreground = TrayvoraTheme.Warning, TextWrapping = TextWrapping.Wrap,
                MaxWidth = BubbleWidth, HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            });
        if (!mine && !message.Failed && !message.Interrupted && message.To == DiscussionMember.Boss && message.Text.TrimEnd().EndsWith('?'))
        {
            var type = new Button { Content = "Reply to " + DisplayName(sender) };
            var voice = new Button { Content = "Answer by voice", IsVisible = dictate is not null };
            TrayvoraTheme.StyleControls(type, voice);
            bool Prepare()
            {
                if (_room is not { } active || CurrentRun is not null) return false;
                var bound = sender == DiscussionMember.Claude ? active.Claude?.Id : active.CodexChatId;
                if (message.ThreadId is { } expected && expected != bound)
                { _status.Text = "Select the original chat before replying to this question."; return false; }
                SetTarget(sender == DiscussionMember.Claude ? DiscussionTarget.Claude : DiscussionTarget.Codex);
                _editor.Focus();
                return true;
            }
            type.Click += (_, _) => Prepare();
            voice.Click += async (_, _) => { if (Prepare()) await ToggleDictationAsync(); };
            column.Children.Add(TrayvoraTheme.Row(type, voice));
        }
        column.Children.Add(tools);
        var avatar = TrayvoraTheme.Avatar(Initials(sender), color, 32);
        var row = new Grid
        {
            ColumnDefinitions = mine ? new ColumnDefinitions("*,Auto") : new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 10,
            Margin = mine ? new Thickness(80, 0, 0, 0) : new Thickness(0, 0, 80, 0),
            Background = Brushes.Transparent,
        };
        Grid.SetColumn(avatar, mine ? 1 : 0);
        Grid.SetColumn(column, mine ? 0 : 1);
        row.Children.AddRange([avatar, column]);
        AttachTools(row, tools, message, bubble.Child as MarkdownView);
        Place(message, row, scroll, insertAt);
    }

    private MarkdownView Markdown(DiscussionMessage message)
    {
        var room = _room;
        var view = new MarkdownView { OpenFile = path =>
        {
            if (room is null) return;
            PreviewFile?.Invoke(path, message.Workspace, AttachmentsFolder(room), (to, quote) =>
            {
                if (!_items.Contains(room)) return;
                if (!ReferenceEquals(room, _room)) Open(room);
                SetTarget(to);
                PromptTemplates.InsertInto(_editor, quote);
                _editor.Focus();
            });
        } };
        view.Show(message.Text);
        return view;
    }

    private void Place(DiscussionMessage message, Control control, bool scroll, int insertAt)
    {
        if (insertAt >= 0) _messages.Children.Insert(insertAt, control);
        else _messages.Children.Add(control);
        _rendered[message] = control;
        if (scroll) ScrollToEnd();
    }

    /// <summary>An empty toolbar that keeps its height; buttons and the right-click menu are built on first hover.</summary>
    private static StackPanel LazyTools(bool alignRight) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 2, Opacity = 0, MinHeight = 26,
        HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
    };

    private void AttachTools(Control owner, StackPanel tools, DiscussionMessage message, MarkdownView? view = null)
    {
        void Build()
        {
            if (tools.Children.Count > 0) return;
            owner.ContextMenu = FillTools(tools, message, view);
        }
        owner.PointerEntered += (_, _) => { Build(); tools.Opacity = 1; };
        owner.PointerExited += (_, _) => tools.Opacity = 0;
        owner.ContextRequested += (_, args) =>
        {
            if (owner.ContextMenu is not null) return;
            Build();
            owner.ContextMenu?.Open(owner);
            args.Handled = true;
        };
    }

    /// <summary>Agents this message can still be forwarded to (never back to its author or to whoever already got it).</summary>
    internal static IEnumerable<DiscussionMember> ForwardTargets(DiscussionMessage message) =>
        new[] { DiscussionMember.Codex, DiscussionMember.Claude }.Where(target =>
            !message.Failed && target != message.Sender && message.To != target
            && !(message.Sender == DiscussionMember.Boss && message.To is null));

    /// <summary>Hover toolbar under a message plus the same actions in a right-click menu.</summary>
    private ContextMenu FillTools(StackPanel tools, DiscussionMessage message, MarkdownView? view = null)
    {
        var items = new List<MenuItem>();

        void Add(string glyph, string? label, string name, Func<Task> action, bool danger = false)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            if (label is not null) content.Children.Add(new TextBlock { Text = label, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, Padding = new Thickness(7, 3), MinHeight = 0 };
            TrayvoraTheme.Ghost(button);
            if (danger) button.Foreground = Brush.Parse("#FF9DAE");
            ToolTip.SetTip(button, name);
            Avalonia.Automation.AutomationProperties.SetName(button, name);
            button.Click += async (_, _) => await action();
            tools.Children.Add(button);
            var item = new MenuItem { Header = name, Icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 12 } };
            item.Click += async (_, _) => await action();
            items.Add(item);
        }

        if (view is not null)
        {
            Add("", "Select", "Select plain text across the whole message (Esc to return)", () => { view.ToggleSelecting(); return Task.CompletedTask; });
            var all = new MenuItem { Header = "Select all text", Icon = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12 } };
            all.Click += (_, _) => { if (view.Selecting) view.ToggleSelecting(); view.ToggleSelecting(selectAll: true); };
            items.Add(all);
        }
        Add(TrayvoraTheme.IconCopy, null, "Copy message", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, view?.CopyText ?? message.Text);
            _status.Text = "Message copied.";
        });
        if (message.DeliveryProfile is { } profile && message.DeliveryId is { } delivery && message.ThreadId is { } thread && _room is { } room && CheckDelivery is { } check)
            Add("↻", "Check reply", "Check delivery without sending again", async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var turn = await check(profile, thread, delivery, timeout.Token);
                    if (turn?.Status is not ("completed" or "failed"))
                    { _status.Text = turn is null ? "No matching reply yet. Open the Desktop chat to check." : $"Delivery · {turn.Status}"; return; }
                    if (room.Messages.All(item => item.DeliveryId != delivery) || _runs.ContainsKey(room.Id)) return;
                    var index = room.Messages.FindIndex(item => item.DeliveryId == delivery);
                    room.Messages[index] = message with { DeliveryId = null, Text = string.IsNullOrEmpty(turn.Answer) ? turn.Status : turn.Answer,
                        Failed = turn.Status == "failed" };
                    _ = Persist(room);
                    if (ReferenceEquals(room, _room)) RefreshTimeline(room);
                }
                catch (Exception error) { _status.Text = error.Message; }
            });
        if (message.ThreadId is { } id && id != Guid.Empty && message.Sender == DiscussionMember.Codex && message.DesktopThread)
            Add("↗", "Open chat", "Open this thread in Codex Desktop", async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri($"codex://threads/{id}"));
            });
        if (!message.Failed)
        {
            // Reply: the message is quoted in the text box, where you can edit it and add your answer before sending.
            var replyTo = message.Sender == DiscussionMember.Claude ? DiscussionTarget.Claude : DiscussionTarget.Codex;
            Add("", "Reply", $"Reply (quote in the text box, to {(replyTo == DiscussionTarget.Claude ? "Claude" : "Codex")})", () => { QuoteInEditor(message, replyTo); return Task.CompletedTask; });
            var other = replyTo == DiscussionTarget.Claude ? DiscussionTarget.Codex : DiscussionTarget.Claude;
            var otherItem = new MenuItem { Header = $"Reply to {(other == DiscussionTarget.Claude ? "Claude" : "Codex")} with this quote", Icon = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12 } };
            otherItem.Click += (_, _) => QuoteInEditor(message, other);
            items.Add(otherItem);
        }
        if (Speak is { } speak && message.Sender != DiscussionMember.Boss && !message.Failed)
        {
            Add("♪", null, "Read full reply", () => speak(message.Text));
            var paragraphs = message.Text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (paragraphs.Length > 1) Add("♪", null, "Read last paragraph", () => speak(paragraphs[^1]));
        }
        foreach (var target in ForwardTargets(message))
            Add("", target.ToString(), $"Forward to {target}", () => ForwardAsync(message, target));
        Add(TrayvoraTheme.IconClear, null, "Delete from this session", () =>
        {
            DeleteMessage(message);
            return Task.CompletedTask;
        }, danger: true);

        return new ContextMenu { ItemsSource = items };
    }

    /// <summary>The message as a Markdown quote ("> " before each line) with who wrote it.</summary>
    internal static string Quote(DiscussionMessage message) =>
        $"> {DisplayName(message.Sender)} wrote:\n" + string.Join("\n", message.Text.Trim().ReplaceLineEndings("\n").Split('\n').Select(line => "> " + line)) + "\n\n";

    /// <summary>Puts the quoted message into the text box, chooses who gets the reply and leaves the cursor after the quote.</summary>
    private void QuoteInEditor(DiscussionMessage message, DiscussionTarget to)
    {
        var current = _editor.Text ?? "";
        var quote = Quote(message);
        _editor.Text = string.IsNullOrWhiteSpace(current) ? quote : current.TrimEnd() + "\n\n" + quote;
        _editor.CaretIndex = _editor.Text.Length;
        SetTarget(to);
        _editor.Focus();
        _status.Text = $"Quoted. Edit it, add your answer and press Enter to send to {(to == DiscussionTarget.Claude ? "Claude" : "Codex")}.";
    }

    /// <summary>Removes a message from Trayvora's saved view only; the agents keep their own chat history.</summary>
    private void DeleteMessage(DiscussionMessage message)
    {
        if (_room is not { } room) return;
        var index = room.Messages.FindIndex(item => ReferenceEquals(item, message));
        if (index < 0) return;
        room.Messages.RemoveAt(index);
        if (index < _shownFrom) _shownFrom--;
        _ = Persist(room);
        if (_rendered.Remove(message, out var control)) _messages.Children.Remove(control);
        UpdateEarlier();
        if (room.Messages.Count == 0)
        {
            _messages.Children.Clear();
            ShowEmptyState(false);
            foreach (var bubble in _live.Values) _messages.Children.Add(bubble.Row);
        }
        RefreshList();
        _status.Text = "Message removed from this session. Codex and Claude keep their own history.";
    }

    /// <summary>Scrolls to the newest message when the reader is already at the bottom (or when forced); otherwise shows the jump button.</summary>
    private void ScrollToEnd(bool force = false)
    {
        if (!force && !_stick)
        {
            _jump.IsVisible = true;
            return;
        }
        _stick = true;
        _jump.IsVisible = false;
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private static string DisplayName(DiscussionMember member) => member == DiscussionMember.Boss ? "You" : member.ToString();
    private static string Initials(DiscussionMember member) => member switch { DiscussionMember.Codex => "Cx", DiscussionMember.Claude => "Cl", _ => "Y" };
    private static IBrush MemberColor(DiscussionMember member) => member switch
    {
        DiscussionMember.Codex => TrayvoraTheme.CodexColor,
        DiscussionMember.Claude => TrayvoraTheme.ClaudeColor,
        _ => TrayvoraTheme.BossColor,
    };
}
