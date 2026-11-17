using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VoiceAgent.Application;
using VoiceAgent.Codex;
using VoiceAgent.Speech;
using VoiceAgent.Infrastructure;

namespace VoiceAgent.Desktop;

internal sealed class VoiceOverlay : Window
{
    private readonly IAudioRecorder _recorder;
    private readonly ISpeechTranscriber _transcriber;
    private readonly WhisperModel _model;
    private readonly SpeechSettings? _speech;
    private VoiceShortcutMode _recordMode;
    private TranscriptPreview? _preview;
    public string? PreviewSendShortcut { get; set; }
    public bool HasDraftPreview => _preview?.HasDraft == true;
    private bool _transcribing;
    public event Action<AgentState>? AgentStateChanged;
    public AgentState CurrentAgentState =>
        _stop is { IsCancellationRequested: false } && _operation?.IsCancellationRequested == false ? AgentState.Recording :
        _transcribing ? AgentState.Transcribing :
        _codexAbort is not null ? AgentState.Sending :
        _suspended || _operation is not null ? AgentState.Busy : AgentState.Ready;
    private readonly CodexSession _codex;
    private readonly DesktopChatPanel? _sharedChat;
    private readonly ReplyAudioPanel? _replyAudio;
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "Codex · computer tasks", "Questions · short memory", "Questions · long memory", "Claude Code" }, SelectedIndex = 0 };
    private readonly Button _clearQuestions = new() { Content = "New conversation" };
    private readonly List<QuestionExchange> _questions = [];
    private bool ClaudeActive => _mode.SelectedIndex == 3;
    private ClaudeChat? _claudeChat;
    private DiscussionWindow? _discussion;
    private readonly Panel _root = new();
    private Control? _appView;
    private readonly TextBox _claudeExe = new() { PlaceholderText = "Auto-detect Claude Code (optional override)" };
    private readonly TextBox _claudeProfile = new() { PlaceholderText = @"E:\path\Claude profile" };
    private readonly TextBox _claudeSearch = new() { PlaceholderText = "Filter Claude chats" };
    private readonly ComboBox _claudeChats = new() { PlaceholderText = "Choose a Claude session" };
    private IReadOnlyList<ClaudeChat> _claudeItems = [];
    private bool QuestionsActive => _mode.SelectedIndex is 1 or 2;
    private bool LongQuestionsActive => _mode.SelectedIndex == 2;
    private string? _memoryFile;
    private QuestionMemory _memory = new([], null, 0);
    private bool _memoryReadFailed;
    private readonly TextBlock _destination = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _modeHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stateText = new();
    private readonly Border _stateDot = TrayvoraTheme.Dot(TrayvoraTheme.Success, 7);
    private Button[] _modeButtons = [];
    private StackPanel? _recordContent;
    private readonly TextBlock _recordIcon = new() { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly TextBlock _recordLabel = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly string? _settingsDirectory;
    private readonly ComboBox _route = new()
    {
        ItemsSource = new[] { "Automatic (Git via Revqora)", "Direct Git · branch and HEAD", "Revqora MCP", "Codex chat" },
        SelectedIndex = 0,
    };
    public string? LocalWorkspace { get; set; }
    private readonly RequestHistory _history;
    private HistoryWindow? _historyWindow;
    private readonly ComboBox _chats = new() { MinWidth = 190 };
    private readonly Button _newChat = new() { Content = "New chat" };
    private Guid _chatId;
    private bool _loadingChats;
    private readonly Button _send = new() { Content = "Send to Codex", IsEnabled = false };
    private readonly TextBox _answer = new()
    {
        AcceptsReturn = true, Height = 180, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Type or paste text to test playback",
    };
    private readonly ChatFeed _feed = new();
    private Task? _codexRun;
    private CancellationTokenSource? _codexAbort;
    private bool _emergencyRequested;
    private readonly ComboBox _microphone = new() { MinWidth = 230 };
    private readonly ComboBox _language = new() { ItemsSource = new[] { "Auto", "Русский", "Українська", "English" }, SelectedIndex = 0 };
    private readonly string[] _languageCodes = ["auto", "ru", "uk", "en"];
    private readonly Button _record = new() { Content = "Record", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _download = new() { Content = "Download selected Whisper model" };
    private readonly Button _refresh = new() { Content = "Refresh devices" };
    private readonly TextBlock _hotkeyMessage = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, IsVisible = false };
    private readonly TextBox _transcript = new()
    {
        AcceptsReturn = true, MinHeight = 100, TextWrapping = TextWrapping.Wrap, MaxLength = 16000, PlaceholderText = "Type a message or edit your transcript",
    };
    private readonly Microphone _defaultMicrophone = new("", "Windows default microphone");
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _stop;
    private bool _loadingDevices;
    private bool _modelNeedsReplacement;
    private bool _refreshOnResume;
    private bool _suspended;
    public event Action<RequestStatus>? RequestEnded;

    public VoiceOverlay(IAudioRecorder recorder, ISpeechTranscriber transcriber, WhisperModel model, CodexSession codex, RequestHistory history, string hotkeyMessage, SpeechSettings? speech = null, string? settingsDirectory = null)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _model = model;
        _speech = speech;
        _settingsDirectory = settingsDirectory;
        TrayvoraTheme.Apply(this);
        TrayvoraTheme.Primary(_record);
        TrayvoraTheme.Primary(_send);
        _codex = codex;
        _history = history;
        if (settingsDirectory is not null)
        {
            _memoryFile = Path.Combine(AppContext.BaseDirectory, "Memory", "questions.json");
            _sharedChat = new DesktopChatPanel(settingsDirectory);
            _replyAudio = new ReplyAudioPanel(settingsDirectory, () => _answer.Text, _sharedChat.AutoPlaybackControl,
                () => OperatingSystem.IsWindows() ? _speech?.Current.ProtectedKey : null);
            _answer.TextChanged += (_, _) => _replyAudio.Refresh(_operation is null && !_suspended);
            _sharedChat.TargetChanged += () =>
            {
                _preview?.Hide();
                _replyAudio.Stop();
                _answer.Clear();
                RefreshControls();
            };
        }
        _answer.TextChanged += (_, _) => _feed.Update(_answer.Text);
        if (_replyAudio is not null) _feed.Speak = text => _replyAudio.ReadAsync(text);
        _feed.Reply = quote => { PromptTemplates.InsertInto(_transcript, quote); _transcript.CaretIndex = _transcript.Text?.Length ?? 0; };
        SetHotkeyMessage(hotkeyMessage);
        Title = "Trayvora";
        Width = 1060;
        Height = 760;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _send.Click += async (_, _) =>
        {
            _codexRun = SendToActiveChatAsync();
            await _codexRun;
        };
        _chats.SelectionChanged += async (_, _) =>
        {
            if (!_loadingChats && _operation is null && _chats.SelectedItem is Conversation chat)
                await OpenChatAsync(chat.Id);
        };
        _newChat.Click += async (_, _) =>
        {
            if (_operation is not null) return;
            try
            {
                var id = Guid.NewGuid();
                var (chats, _) = _history.Conversations();
                var number = chats.Count + 1;
                while (chats.Any(chat => chat.Title == $"Chat {number}")) number++;
                _history.AddConversation(id, $"Chat {number}");
                await OpenChatAsync(id);
            }
            catch (Exception)
            {
                _status.Text = "Could not create a chat. Check access to the history folder.";
            }
        };
        var discussionButton = TrayvoraTheme.NavItem(TrayvoraTheme.IconGroup, "Discussion", "You · Codex · Claude");
        discussionButton.Click += (_, _) => OpenDiscussion();
        var historyButton = TrayvoraTheme.NavItem(TrayvoraTheme.IconHistory, "History", "Past requests");
        var audioToTextButton = TrayvoraTheme.NavItem("\uE8D6", "Audio to text", "MP3, WAV → text");
        var textToAudioButton = TrayvoraTheme.NavItem("\uE767", "Text to audio", "Text → speech file");
        audioToTextButton.Click += (_, _) => { if (OperatingSystem.IsWindows()) OpenAudioToText(); };
        textToAudioButton.Click += (_, _) => { if (OperatingSystem.IsWindows()) OpenTextToAudio(); };
        var terminalButton = TrayvoraTheme.NavItem("", "Terminal", "Claude Code · Codex CLI");
        terminalButton.Click += (_, _) =>
        {
            if (!OperatingSystem.IsWindows()) return;
            if (_terminal?.IsVisible == true) HideTerminal();
            else { _terminalRoom = null; ShowTerminal(TerminalKind.Claude); }
        };
        terminalButton.IsVisible = settingsDirectory is not null && OperatingSystem.IsWindows();
        _terminalButton = terminalButton;
        audioToTextButton.IsVisible = textToAudioButton.IsVisible = settingsDirectory is not null && OperatingSystem.IsWindows();
        _chatGptButton = TrayvoraTheme.NavItem("\uE77B", "ChatGPT", "Checking…");
        _claudeButton = TrayvoraTheme.NavItem("\uE77B", "Claude", "Checking…");
        _chatGptButton.Click += (_, _) => ShowAccounts();
        _claudeButton.Click += (_, _) => ShowAccounts();
        _chatGptButton.IsVisible = _claudeButton.IsVisible = settingsDirectory is not null;
        historyButton.Click += async (_, _) => await ShowHistoryAsync();
        _transcript.TextChanged += (_, _) => RefreshControls();
        _record.Click += async (_, _) => await ToggleRecordingAsync(_stop is null ? VoiceShortcutMode.MainWindow : _recordMode);
        _cancel.Click += (_, _) => Cancel();
        _download.Click += async (_, _) => await DownloadAsync();
        _refresh.Click += async (_, _) => await RefreshDevicesAsync();
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape)
            {
                return;
            }

            if (_operation is null)
            {
                Hide();
            }
            else
            {
                Cancel();
            }

            args.Handled = true;
        };

        static Grid Stretch(Control main, Control side)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(side, 1);
            grid.Children.AddRange([main, side]);
            return grid;
        }
        var trayvoraChat = TrayvoraTheme.Field("Trayvora chat", Stretch(_chats, _newChat));
        var routing = TrayvoraTheme.Field("Routing", _route);
        var audioGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12, RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 10 };
        var micField = TrayvoraTheme.Field("Microphone", Stretch(_microphone, _refresh));
        var langField = TrayvoraTheme.Field("Transcription language", _language);
        Grid.SetColumn(langField, 1);
        Grid.SetRow(trayvoraChat, 1);
        Grid.SetRow(routing, 1);
        Grid.SetColumn(routing, 1);
        audioGrid.Children.AddRange([micField, langField, trayvoraChat, routing]);
        var localSettings = new Expander
        {
            Header = "Microphone, language and routing",
            Content = new StackPanel { Spacing = 12, Children = { audioGrid, _hotkeyMessage } },
        };
        _hotkeyMessage.Foreground = TrayvoraTheme.Faint;
        _hotkeyMessage.FontSize = 11.5;
        _hotkeyMessage.LineHeight = 18;
        _status.Foreground = TrayvoraTheme.Muted;
        _status.FontSize = 12;
        _status.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.PropertyChanged += (_, args) => { if (args.Property == TextBlock.TextProperty) ToolTip.SetTip(_status, _status.Text); };
        _transcript.Classes.Add("bare");
        _transcript.MaxHeight = 170;
        _transcript.MinHeight = 56;
        _answer.Height = double.NaN;
        _answer.MinHeight = 220;
        _answer.Classes.Add("bare");
        _answer.FontSize = 14;
        TrayvoraTheme.StyleControls(_chats, _newChat, _microphone, _refresh, _language, _record, _cancel,
            _download, _transcript, _route, _answer);
        TrayvoraTheme.Ghost(_cancel);
        _record.Classes.Add("record");
        _record.MinWidth = 104;
        _send.MinWidth = 130;
        var responseHeading = TrayvoraTheme.Heading("Codex response");
        var responseHeader = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = TrayvoraTheme.IconSpark, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = TrayvoraTheme.Accent, FontSize = 15, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            responseHeading,
        } };
        var responseDock = new DockPanel();
        DockPanel.SetDock(responseHeader, Dock.Top);
        responseHeader.Margin = new Thickness(0, 0, 0, 10);
        responseDock.Children.Add(responseHeader);
        if (_replyAudio is not null)
        {
            DockPanel.SetDock(_replyAudio, Dock.Bottom);
            _replyAudio.Margin = new Thickness(0, 10, 0, 0);
            responseDock.Children.Add(_replyAudio);
        }
        responseDock.Children.Add(_feed);
        var response = new Border
        {
            Background = TrayvoraTheme.Sidebar, BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 14, 8, 12), MinHeight = 260, Child = responseDock,
        };
        TrayvoraTheme.StyleControls(_mode, _clearQuestions);
        _mode.IsVisible = settingsDirectory is not null;
        _mode.SelectionChanged += (_, _) =>
        {
            _preview?.Hide();
            _replyAudio?.Stop();
            if (LongQuestionsActive) LoadQuestionMemory();
            _answer.Text = LongQuestionsActive ? _memory.Exchanges.LastOrDefault()?.Answer : QuestionsActive ? _questions.LastOrDefault()?.Answer : "";
            _status.Text = QuestionsActive ? "Ask by voice or type. OpenAI API usage is billed separately." : "Codex mode selected.";
            RefreshControls();
        };
        _clearQuestions.Click += (_, _) =>
        {
            if (_operation is not null) return;
            if (LongQuestionsActive)
            {
                try
                {
                    if (_memoryFile is not null && File.Exists(_memoryFile))
                        File.Move(_memoryFile, Path.Combine(Path.GetDirectoryName(_memoryFile)!, $"questions-{Guid.NewGuid():N}.json"));
                    _memory = new([], null, 0);
                    _memoryReadFailed = false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _status.Text = "Could not archive this conversation.";
                    return;
                }
            }
            else _questions.Clear();
            _preview?.Hide();
            _replyAudio?.Stop();
            _transcript.Clear();
            _answer.Clear();
            _feed.Clear();
            _status.Text = "New questions conversation.";
            RefreshControls();
        };
        var findClaude = new Button { Content = "Find Claude chats" };
        var claudePanel = new StackPanel { Spacing = 8, IsVisible = false,
            Children = { _claudeExe, _claudeProfile, findClaude, _claudeSearch, _claudeChats } };
        TrayvoraTheme.StyleControls(_claudeExe, _claudeProfile, _claudeSearch, _claudeChats, findClaude);
        var claudeSettings = settingsDirectory is null ? null : Path.Combine(settingsDirectory, "claude-route.json");
        try
        {
            if (claudeSettings is not null && SettingsFile.Read<ClaudeRoute>(claudeSettings) is { } saved)
            {
                _claudeExe.Text = saved.Executable;
                _claudeProfile.Text = saved.Profile;
                if (saved.Chat is { } chat)
                {
                    _claudeItems = [chat];
                    _claudeChats.ItemsSource = _claudeItems;
                    _claudeChats.SelectedItem = chat;
                    _claudeChat = chat;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        void SaveClaudeSettings()
        {
            try
            {
                if (claudeSettings is not null)
                    SettingsFile.Write(claudeSettings, new ClaudeRoute(_claudeExe.Text ?? "", _claudeProfile.Text ?? "", _claudeChat));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _status.Text = "Could not save Claude settings."; }
        }
        _claudeExe.LostFocus += (_, _) => SaveClaudeSettings();
        _claudeProfile.TextChanged += (_, _) => InvalidateClaudeSelection();
        _claudeProfile.LostFocus += (_, _) => SaveClaudeSettings();
        void ShowClaudeChats()
        {
            var active = _claudeChat?.Id;
            var matches = _claudeItems.Where(chat => DiscussionWindow.Matches(chat.Title, _claudeSearch.Text)).ToArray();
            _claudeChats.ItemsSource = matches;
            _claudeChats.SelectedItem = matches.FirstOrDefault(chat => chat.Id == active);
        }
        _claudeSearch.TextChanged += (_, _) => ShowClaudeChats();
        _claudeChats.SelectionChanged += (_, _) =>
        {
            if (_claudeChats.SelectedItem is not ClaudeChat chat) return;
            _claudeChat = chat;
            SaveClaudeSettings();
            RefreshControls();
        };
        findClaude.Click += async (_, _) =>
        {
            var profile = ClaudeProfile();
            if (!Path.IsPathFullyQualified(profile) || !Directory.Exists(profile))
            {
                _status.Text = "Select the existing Claude Code profile directory.";
                return;
            }
            findClaude.IsEnabled = false;
            try
            {
                var items = await Task.Run(() => ClaudeChats.Find(profile));
                if (!string.Equals(profile, ClaudeProfile(), StringComparison.OrdinalIgnoreCase)) return;
                _claudeItems = items;
                _claudeSearch.Clear();
                ShowClaudeChats();
                _status.Text = $"Found {items.Count} Claude sessions.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _status.Text = "Could not read Claude sessions."; }
            finally { findClaude.IsEnabled = true; }
        };
        var chooseChat = new Expander { Header = "Choose Codex Desktop chat", Content = _sharedChat, IsVisible = _sharedChat is not null };
        _mode.SelectionChanged += (_, _) =>
        {
            responseHeading.Text = QuestionsActive ? "Answer" : "Codex response";
            chooseChat.IsVisible = !QuestionsActive && !ClaudeActive && _sharedChat is not null;
            claudePanel.IsVisible = ClaudeActive;
            localSettings.Header = QuestionsActive ? "Microphone and language" : "Microphone, language and routing";
        };
        chooseChat.PropertyChanged += async (_, args) =>
        {
            if (args.Property == Expander.IsExpandedProperty && chooseChat.IsExpanded && _sharedChat is not null)
                await _sharedChat.LoadOnceAsync();
        };
        // ── Sidebar ──────────────────────────────────────────────
        _modeButtons =
        [
            TrayvoraTheme.NavItem(TrayvoraTheme.IconCode, "Codex", "Computer tasks"),
            TrayvoraTheme.NavItem(TrayvoraTheme.IconChat, "Quick questions", "GPT · short memory"),
            TrayvoraTheme.NavItem(TrayvoraTheme.IconMemory, "Deep questions", "GPT · long memory"),
            TrayvoraTheme.NavItem(TrayvoraTheme.IconBrain, "Claude Code", "Resume a session"),
        ];
        for (var index = 0; index < _modeButtons.Length; index++)
        {
            var selected = index;
            _modeButtons[index].Click += (_, _) => { if (_mode.IsEnabled) _mode.SelectedIndex = selected; };
        }
        var navModes = new StackPanel { Spacing = 2 };
        navModes.Children.AddRange(_modeButtons);
        navModes.IsVisible = settingsDirectory is not null;
        var brand = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 2, 0, 8),
            Children =
            {
                new Border
                {
                    Width = 32, Height = 32, CornerRadius = new CornerRadius(10),
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(Avalonia.Media.Color.Parse("#7C8CFF"), 0), new GradientStop(Avalonia.Media.Color.Parse("#B07CFF"), 1) },
                    },
                    Child = new TextBlock { Text = TrayvoraTheme.IconMic, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                },
                new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Children =
                {
                    new TextBlock { Text = "Trayvora", FontSize = 16, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = "Voice agent", FontSize = 11, Foreground = TrayvoraTheme.Muted },
                } },
            },
        };
        _stateText.Text = "Ready";
        var sidebarTop = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                brand,
                TrayvoraTheme.Caption("Modes"), navModes,
                new Border { Height = 8 },
                TrayvoraTheme.Caption("Workspace"), discussionButton, historyButton,
                new Border { Height = 8 },
                TrayvoraTheme.Caption("Tools"), audioToTextButton, textToAudioButton, terminalButton,
                new Border { Height = 8 },
                TrayvoraTheme.Caption("Accounts"), _chatGptButton, _claudeButton,
            },
        };
        var startButton = TrayvoraTheme.NavItem("\uE897", "Get started", "Readiness · shortcuts");
        var settingsButton = TrayvoraTheme.NavItem("\uE713", "Settings", "Speech, voices, keys");
        startButton.Click += (_, _) => ShowReadiness();
        settingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        startButton.IsVisible = settingsButton.IsVisible = settingsDirectory is not null;
        var sidebarBottom = new StackPanel { Spacing = 4, Children = { startButton, settingsButton, new Border { Height = 4 }, TrayvoraTheme.Pill(_stateText, _stateDot) } };
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ShowReadinessOnFirstRun, Avalonia.Threading.DispatcherPriority.Background);
        var sidebarGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(sidebarBottom, 1);
        sidebarBottom.Margin = new Thickness(0, 8, 0, 0);
        // The list scrolls when the window is short instead of running under the buttons at the bottom.
        var sidebarScroll = new ScrollViewer { Content = sidebarTop, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        sidebarGrid.Children.AddRange([sidebarScroll, sidebarBottom]);
        var sidebar = new Border
        {
            Width = 236,
            Background = TrayvoraTheme.Sidebar,
            BorderBrush = TrayvoraTheme.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(12, 18, 12, 14),
            Child = sidebarGrid,
        };

        // ── Header ───────────────────────────────────────────────
        _destination.FontSize = 18;
        _destination.TextTrimming = TextTrimming.CharacterEllipsis;
        _destination.TextWrapping = TextWrapping.NoWrap;
        _modeHint.FontSize = 12;
        _modeHint.Foreground = TrayvoraTheme.Muted;
        _clearQuestions.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var headerText = new StackPanel { Spacing = 2, Children = { _destination, _modeHint } };
        Grid.SetColumn(_clearQuestions, 1);
        headerGrid.Children.AddRange([headerText, _clearQuestions]);
        var header = new Border
        {
            BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(26, 18, 26, 14),
            Child = headerGrid,
        };

        // ── Body ─────────────────────────────────────────────────
        var context = new StackPanel { Spacing = 10, Children = { chooseChat } };
        claudePanel.Margin = new Thickness(0);
        var claudeCard = TrayvoraTheme.Card(TrayvoraTheme.Heading("Claude Code session"), TrayvoraTheme.Hint("Trayvora resumes the selected Claude Code session and sends your message to it."), claudePanel);
        claudeCard.IsVisible = false;
        claudePanel.PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) claudeCard.IsVisible = claudePanel.IsVisible; };
        context.Children.Add(claudeCard);
        var settingsArea = new ScrollViewer
        {
            MaxHeight = 300,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 12), Children = { context, localSettings } },
        };
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(26, 16, 26, 4) };
        Grid.SetRow(response, 1);
        body.Children.AddRange([settingsArea, response]);

        // ── Composer ─────────────────────────────────────────────
        var composerActions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        var templates = new PromptTemplates(Path.Combine(settingsDirectory ?? Path.Combine(AppContext.BaseDirectory, "settings"), "templates.json"))
            .Button(() => _transcript.Text, text => PromptTemplates.InsertInto(_transcript, text));
        templates.IsVisible = settingsDirectory is not null;
        Grid.SetColumn(templates, 1);
        Grid.SetColumn(_status, 2);
        Grid.SetColumn(_cancel, 3);
        Grid.SetColumn(_send, 4);
        _status.Margin = new Thickness(6, 0);
        composerActions.Children.AddRange([_record, templates, _status, _cancel, _send]);
        var composerBox = new Border
        {
            Background = TrayvoraTheme.Surface, BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 10, 10, 10),
            Child = new StackPanel { Spacing = 8, Children = { _transcript, _progress, composerActions } },
        };
        _transcript.GotFocus += (_, _) => composerBox.BorderBrush = TrayvoraTheme.Accent;
        _transcript.LostFocus += (_, _) => composerBox.BorderBrush = TrayvoraTheme.Border;
        _transcript.AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control) && _send.IsEnabled)
            {
                args.Handled = true;
                _codexRun = SendToActiveChatAsync();
                await _codexRun;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var composer = new Border
        {
            Padding = new Thickness(22, 8, 22, 18),
            Child = new StackPanel { Spacing = 8, Children = { _download, composerBox,
                new TextBlock { Text = "Ctrl+Enter to send · Esc to cancel or hide", FontSize = 11, Foreground = TrayvoraTheme.Faint, Margin = new Thickness(8, 0, 0, 0) } } },
        };

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(body, 1);
        Grid.SetRow(composer, 2);
        main.Children.AddRange([header, body, composer]);
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(main, 1);
        shell.Children.AddRange([sidebar, main]);
        _appView = shell;
        _root.Children.Add(shell);
        Grid.SetColumn(_terminalSplitter, 1);
        _shell.Children.AddRange([_root, _terminalSplitter]);
        Content = _shell;
        _status.Text = ModelAvailable ? "Ready to record." : "Select or download a speech model in Settings.";
        LoadChats();
        RefreshControls();
    }

    private void OpenDiscussion()
    {
        if (_discussion is null)
        {
            var folder = Path.Combine(_settingsDirectory ?? Path.Combine(AppContext.BaseDirectory, "settings"), "discussions");
            _discussion = new DiscussionWindow(new DiscussionStore(folder), async (agent, message, room, stream, ct) =>
            {
                if (agent == DiscussionMember.Claude)
                {
                    if (room.Claude is not { } chat) throw new IOException("Choose a Claude chat for this session first.");
                    var reply = await ClaudeChats.SendAsync(_claudeExe.Text?.Trim() ?? "", ClaudeProfile(), chat, message, ct, stream, room.ClaudePermissions);
                    // A chat Trayvora started is added to Claude Desktop's Code tab once Claude Code has saved it.
                    if (chat.Title.StartsWith("New Claude chat", StringComparison.Ordinal)) _ = Task.Run(() => ClaudeDesktopSessions.Register(chat));
                    return reply;
                }

                if (room.CodexDesktop)
                {
                    if (_sharedChat is null) throw new IOException("Codex Desktop is unavailable in this build.");
                    if (room.CodexChatId == Guid.Empty && room.NewCodexFolder is { } folder)
                    {
                        var created = await _sharedChat.StartChatAsync(folder, DiscussionStore.TitleFrom(message), message, ct, stream);
                        // The chat exists in Codex Desktop now; later messages go to it like to any Desktop chat.
                        room.CodexChatId = created.Chat.Id;
                        if (_settingsDirectory is { } settings)
                        {
                            var profile = _sharedChat.Profile;
                            var id = created.Chat.Id.ToString();
                            if (!await Task.Run(() => CodexDesktopChats.Register(profile, settings, id)))
                                stream.Activity("Close Codex Desktop once to see this chat in it; Trayvora adds it when Codex Desktop is closed.");
                        }
                        room.CodexChatTitle = $"Codex Desktop · {created.Chat.Title}";
                        room.NewCodexFolder = null;
                        if (created.Status != "completed") throw new IOException($"Codex Desktop returned {created.Status}.");
                        return created.Answer;
                    }
                    var turn = await _sharedChat.SubmitToAsync(room.CodexChatId, message, ct, stream);
                    if (turn is null) return null;
                    if (turn.Status != "completed") throw new IOException($"Codex Desktop returned {turn.Status}.");
                    return turn.Answer;
                }

                CodexTurnStatus result;
                try
                {
                    result = await _codex.RunAsync(message, new CodexOutput(stream.Append), ct, emergencyStop: ct, chatId: room.CodexChatId);
                }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or InvalidDataException or TimeoutException)
                {
                    // Sign-in, queue and length problems carry a readable reason; show it instead of a generic failure.
                    throw new IOException(error.Message, error);
                }
                if (result != CodexTurnStatus.Completed) throw new IOException($"Codex returned {result}.");
                return stream.Text;
            },
            () => _history.Conversations().Chats,
            _sharedChat is null ? null : ct => _sharedChat.ListChatsAsync(ct),
            async () =>
            {
                var profile = ClaudeProfile();
                if (!Path.IsPathFullyQualified(profile) || !Directory.Exists(profile))
                    throw new IOException("Set the Claude Code profile folder under Claude Code in the main window.");
                return await Task.Run(() => ClaudeChats.Find(profile));
            },
            () => _claudeChat,
            DictateAsync,
            () => new DiscussionSettings(_settingsDirectory ?? Path.Combine(AppContext.BaseDirectory, "settings")).Rounds,
            OpenTerminal);
            _discussion.Initialize();
            _discussion.BackRequested += CloseDiscussion;
            if (_replyAudio is not null) _discussion.Speak = text => _replyAudio.ReadAsync(text);
            // Tell the user when a session finishes while they are in another app.
            _discussion.RunFinished += (_, outcome) =>
            {
                if (outcome is null || !outcome.StartsWith("Paused", StringComparison.Ordinal)) RequestEnded?.Invoke(RequestStatus.Completed);
            };
            _root.Children.Add(_discussion);
        }
        ShowPage(_discussion, "Trayvora · Sessions");
        if (Width < 1100) Width = 1100;
    }

    /// <summary>
    /// Opens PowerShell in the session's folder with the same Claude Code and Codex Desktop profiles Trayvora uses,
    /// so the user can sign in, add MCP servers or change settings without another app.
    /// </summary>
    private void OpenTerminal(DiscussionRoom? room)
    {
        if (OperatingSystem.IsWindows())
        {
            _terminalRoom = room;
            ShowTerminal(TerminalKind.Claude);
            return;
        }
        System.Diagnostics.Process.Start(TerminalStart(room, TerminalKind.PowerShell, embedded: false))?.Dispose();
    }

    private DiscussionRoom? _terminalRoom;
    private Button? _terminalButton;
    private TerminalPanel? _terminal;
    private readonly Grid _shell = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,0") };
    private readonly GridSplitter _terminalSplitter = new() { Width = 5, Background = Brushes.Transparent, IsVisible = false, ResizeDirection = GridResizeDirection.Columns };

    /// <summary>Opens the built-in terminal beside the chat (or restarts it with another program).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ShowTerminal(TerminalKind kind)
    {
        Show();
        Activate();
        if (_terminal is null)
        {
            _terminal = new TerminalPanel(which => TerminalStart(_terminalRoom ?? _discussionRoom(), which, embedded: true));
            _terminal.CloseRequested += HideTerminal;
            Grid.SetColumn(_terminal, 2);
            _shell.Children.Add(_terminal);
        }
        _shell.ColumnDefinitions[2].Width = new GridLength(Math.Max(420, Bounds.Width * 0.45));
        _shell.ColumnDefinitions[2].MinWidth = 320;
        _terminal.IsVisible = _terminalSplitter.IsVisible = true;
        if (!_terminal.Running || kind != TerminalKind.Claude) _terminal.Start(kind);
        else _terminal.FocusConsole();
    }

    private void HideTerminal()
    {
        if (_terminal is null) return;
        if (OperatingSystem.IsWindows()) _terminal.Stop();
        _terminal.IsVisible = _terminalSplitter.IsVisible = false;
        _shell.ColumnDefinitions[2].MinWidth = 0;
        _shell.ColumnDefinitions[2].Width = new GridLength(0);
    }

    private DiscussionRoom? _discussionRoom() => _discussion?.IsVisible == true ? _discussion.CurrentRoom : null;

    private System.Diagnostics.ProcessStartInfo TerminalStart(DiscussionRoom? room, TerminalKind kind, bool embedded)
    {
        var folder = room?.Claude?.Workspace ?? room?.NewCodexFolder;
        if (folder is null || !Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shell = new[] { "pwsh.exe", "powershell.exe" }
            .Select(name => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(dir => dir.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
                .Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists))
            .FirstOrDefault(path => path is not null) ?? "powershell.exe";
        var conhost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
        var start = new System.Diagnostics.ProcessStartInfo(embedded ? conhost : shell) { WorkingDirectory = folder, UseShellExecute = false };
        if (embedded) start.ArgumentList.Add(shell);
        // The real user folders, not the ones Trayvora redirects for its own Codex profile.
        foreach (var (name, value) in ClaudeChats.RealUserFolders()) start.Environment[name] = value;
        start.Environment.Remove("CODEX_SQLITE_HOME");
        var paths = new List<string>();
        var profile = ClaudeProfile();
        if (Path.IsPathFullyQualified(profile)) start.Environment["CLAUDE_CONFIG_DIR"] = profile;
        try { paths.Add(Path.GetDirectoryName(ClaudeChats.FindExecutable(_claudeExe.Text?.Trim() ?? ""))!); }
        catch (FileNotFoundException) { }
        if (Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE") is { Length: > 0 } codex && File.Exists(codex))
            paths.Add(Path.GetDirectoryName(codex)!);
        if (_sharedChat?.Profile is { Length: > 0 } codexHome && Directory.Exists(codexHome)) start.Environment["CODEX_HOME"] = codexHome;
        else start.Environment.Remove("CODEX_HOME");
        start.Environment["PATH"] = string.Join(Path.PathSeparator, paths.Append(start.Environment["PATH"] ?? ""));
        const string hint =
            "Write-Host 'Trayvora terminal' -ForegroundColor Cyan; " +
            "Write-Host '  claude            - Claude Code here: /login, /mcp, /permissions, /config'; " +
            "Write-Host '  claude mcp list   - MCP servers;  claude mcp add ...  - add one'; " +
            "Write-Host '  codex login       - Codex sign-in;  codex mcp list / codex mcp add ...'; " +
            "Write-Host ('  Claude profile: ' + $env:CLAUDE_CONFIG_DIR); Write-Host ('  Codex profile:  ' + $env:CODEX_HOME); Write-Host ''";
        // The codex.exe bundled with Codex Desktop needs --no-daemon to run interactively (new chat, resume, fork);
        // the function adds it whenever "codex" is typed that way, and leaves subcommands such as "codex login" as they are.
        const string codexFunction =
            "; $codexExe = (Get-Command codex.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source; " +
            "if ($codexExe) { function global:codex { if ($args.Count -eq 0 -or $args[0] -in @('resume','fork') -or \"$($args[0])\".StartsWith('-')) { & $codexExe --no-daemon @args } else { & $codexExe @args } } }";
        var program = kind switch { TerminalKind.Claude => "; claude", TerminalKind.Codex => "; codex", _ => "" };
        foreach (var arg in new[] { "-NoLogo", "-NoExit", "-Command", hint + codexFunction + program }) start.ArgumentList.Add(arg);
        return start;
    }

    private AudioToTextView? _audioToText;
    private AccountsView? _accounts;
    private Button? _chatGptButton;
    private Button? _claudeButton;

    private AccountsView Accounts()
    {
        if (_accounts is null)
        {
            _accounts = new AccountsView([
                AccountProviders.ChatGpt(),
                AccountProviders.Claude(() => _claudeExe.Text?.Trim() ?? "", ClaudeProfile),
            ]);
            _accounts.BackRequested += CloseDiscussion;
            _accounts.StatusChanged += NoteAccount;
        }
        return _accounts;
    }

    /// <summary>Opens the Accounts page (sign in to ChatGPT and Claude).</summary>
    public void ShowAccounts()
    {
        Show();
        Activate();
        ShowPage(Accounts(), "Trayvora · Accounts");
    }

    /// <summary>Checks both sign-ins in the background so the sidebar shows their state.</summary>
    public async Task CheckAccountsAsync()
    {
        if (_settingsDirectory is null) return;
        foreach (var provider in new[] { AccountProviders.ChatGpt(), AccountProviders.Claude(() => _claudeExe.Text?.Trim() ?? "", ClaudeProfile) })
        {
            AccountStatus status;
            try { status = await Task.Run(() => provider.Status(CancellationToken.None)); }
            catch (Exception) { status = new(false, "Could not check"); }
            NoteAccount(provider.Name, status);
        }
    }

    private readonly Dictionary<string, AccountStatus> _accountStatus = [];
    private ReadinessView? _readiness;
    private IReadOnlyList<(string Name, string Keys)> _shortcutList = [];

    /// <summary>Asks the app to open the Settings window.</summary>
    public event Action? SettingsRequested;

    private void NoteAccount(string name, AccountStatus status)
    {
        _accountStatus[name] = status;
        SetAccountHint(name == "Claude" ? _claudeButton : _chatGptButton, status);
    }

    public void SetShortcuts(IReadOnlyList<(string Name, string Keys)> shortcuts)
    {
        _shortcutList = shortcuts;
        _readiness?.SetShortcuts(shortcuts);
    }

    /// <summary>Opens "Get started": readiness checks, shortcuts and key phrases.</summary>
    public void ShowReadiness()
    {
        Show();
        Activate();
        if (_readiness is null)
        {
            _readiness = new ReadinessView(CheckReadinessAsync, OperatingSystem.IsWindows() ? UpdateFromZipAsync : null);
            _readiness.BackRequested += CloseDiscussion;
            _readiness.SetShortcuts(_shortcutList);
        }
        ShowPage(_readiness, "Trayvora · Get started");
        _ = _readiness.RefreshAsync();
    }

    private async Task<IReadOnlyList<ReadinessItem>> CheckReadinessAsync()
    {
        var items = new List<ReadinessItem>();
        var microphones = (_microphone.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count(item => item is Microphone mic && mic.Id.Length > 0) ?? 0;
        items.Add(new("Microphone", microphones > 0 ? $"{microphones} found." : "No microphone found. Connect one, then check again.",
            microphones > 0, FixLabel: "Refresh devices", Fix: RefreshDevicesAsync));
        var cloud = OperatingSystem.IsWindows() && _speech?.Current.Cloud == true;
        items.Add(new("Speech recognition", ModelAvailable ? (cloud ? "OpenAI transcription is set up." : "Local Whisper model is ready.")
            : cloud ? "Add an OpenAI API key in Settings, or switch to local Whisper." : "The Whisper model is not downloaded yet.",
            ModelAvailable, FixLabel: cloud ? "Open Settings" : "Download model",
            Fix: cloud ? () => { SettingsRequested?.Invoke(); return Task.CompletedTask; } : DownloadAsync));
        foreach (var provider in new[] { AccountProviders.ChatGpt(), AccountProviders.Claude(() => _claudeExe.Text?.Trim() ?? "", ClaudeProfile) })
        {
            AccountStatus status;
            try { status = await Task.Run(() => provider.Status(CancellationToken.None)); }
            catch (Exception) { status = new(false, "Could not check"); }
            NoteAccount(provider.Name, status);
            var use = provider.Name == "Claude" ? "Claude Code chats and sessions" : "Codex";
            items.Add(new($"{provider.Name} sign-in", status.SignedIn ? $"Signed in. Used by {use}." : status.Available ? $"Not signed in. Needed for {use}." : $"Not installed. Needed for {use}.",
                status.SignedIn, FixLabel: "Sign in", Fix: () => { ShowAccounts(); return Task.CompletedTask; }));
        }
        var key = (OperatingSystem.IsWindows() ? _speech?.Current.ProtectedKey : null) ?? _replyAudio?.SavedSeparateKey;
        items.Add(new("OpenAI API key (optional)", key is not null ? "Saved. Quick and deep questions and OpenAI voices work."
            : "Only needed for Quick/Deep questions, OpenAI transcription and OpenAI voices.",
            key is not null, Optional: true, FixLabel: "Open Settings", Fix: () => { SettingsRequested?.Invoke(); return Task.CompletedTask; }));
        return items;
    }

    /// <summary>Adds chats waiting for Codex Desktop to close to its chat list (called every few seconds).</summary>
    public void ApplyPendingCodexChats()
    {
        if (_settingsDirectory is not { } settings || _sharedChat is not { } shared) return;
        var profile = shared.Profile;
        _ = Task.Run(() => CodexDesktopChats.ApplyPending(profile, settings, CodexDesktopChats.DesktopRunning()));
    }

    /// <summary>Asks the app to close (after an update is staged).</summary>
    public event Action? ExitRequested;

    private async Task<string?> UpdateFromZipAsync()
    {
        if (_operation is not null || _discussion?.IsRunning == true) return "Wait until the current request finishes.";
        if (StorageProvider is not { } storage) return null;
        var picked = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose the new Trayvora zip", AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Trayvora zip") { Patterns = ["*.zip"] }],
        });
        if (picked.Count == 0 || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(picked[0]) is not { } zip) return null;
        var app = AppContext.BaseDirectory;
        try
        {
            var staged = await Task.Run(() => PortableUpdate.Stage(zip, app));
            PortableUpdate.Launch(staged, app);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            return $"Could not update: {error.Message}";
        }
        ExitRequested?.Invoke();
        return "Updating… Trayvora restarts in a few seconds.";
    }

    /// <summary>Opens "Get started" once, the first time the window is shown on this computer.</summary>
    private void ShowReadinessOnFirstRun()
    {
        if (_settingsDirectory is null) return;
        var marker = Path.Combine(_settingsDirectory, "welcome-seen");
        if (File.Exists(marker)) return;
        try { Directory.CreateDirectory(_settingsDirectory); File.WriteAllText(marker, DateTimeOffset.Now.ToString("O")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        ShowReadiness();
    }

    private static void SetAccountHint(Button? button, AccountStatus status)
    {
        if (button?.Content is not Grid grid || grid.Children.OfType<StackPanel>().FirstOrDefault() is not { } text || text.Children.Count < 2) return;
        if (text.Children[1] is not TextBlock hint) return;
        hint.Text = status.SignedIn ? "Signed in" : status.Available ? "Not signed in · click to sign in" : "Not installed";
        hint.Foreground = status.SignedIn ? TrayvoraTheme.Success : status.Available ? TrayvoraTheme.Warning : TrayvoraTheme.Muted;
    }
    private TextToAudioView? _textToAudio;

    /// <summary>Shows one full-window page (Sessions, Audio to text, Text to audio) in place of the main view.</summary>
    private void ShowPage(Control page, string title)
    {
        _preview?.Hide();
        foreach (var child in _root.Children) child.IsVisible = ReferenceEquals(child, page);
        if (!_root.Children.Contains(page)) { _root.Children.Add(page); page.IsVisible = true; }
        Title = title;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OpenAudioToText()
    {
        if (_audioToText is null)
        {
            _audioToText = new AudioToTextView(
                (cloud, model) => _speech is not null ? _speech.CreateTranscriber(cloud, model) : _transcriber,
                cloud => _speech?.Describe(cloud) ?? "",
                cloud => _speech is not null ? _speech.ReadyFor(cloud) && !(!cloud && _modelNeedsReplacement) : !cloud && ModelAvailable,
                () => _speech?.Current.Cloud == true,
                new WhisperChoices(
                    SpeechSettings.LocalModels,
                    () => _speech?.Current.Local ?? 0,
                    index => _speech?.ModelAt(index).Exists ?? (index == 0 && _model.Exists),
                    (index, progress, ct) => _speech is not null ? _speech.ModelAt(index).DownloadAsync(progress, ct) : _model.DownloadAsync(progress, ct)));
            _audioToText.BackRequested += CloseDiscussion;
        }
        ShowPage(_audioToText, "Trayvora · Audio to text");
        _audioToText.Refresh();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void OpenTextToAudio()
    {
        if (_settingsDirectory is null) return;
        if (_textToAudio is null)
        {
            _textToAudio = new TextToAudioView(_settingsDirectory, () => _speech?.Current.ProtectedKey);
            _textToAudio.BackRequested += CloseDiscussion;
        }
        ShowPage(_textToAudio, "Trayvora · Text to audio");
    }

    /// <summary>Shows the window with the Sessions view open.</summary>
    public void ShowSessions()
    {
        Show();
        Activate();
        OpenDiscussion();
    }

    private void CloseDiscussion()
    {
        foreach (var child in _root.Children) child.IsVisible = ReferenceEquals(child, _appView);
        Title = "Trayvora";
        _transcript.Focus();
    }

    private void LoadChats()
    {
        _loadingChats = true;
        try
        {
            if (_codex.HasPreviousRevqoraChat)
                _history.AddConversation(CodexSession.PreviousRevqoraChatId, "Previous Revqora chat");
            var (chats, active) = _history.Conversations();
            _chatId = active;
            _chats.ItemsSource = chats;
            _chats.SelectedItem = chats.FirstOrDefault(chat => chat.Id == active);
        }
        catch (Exception)
        {
            _status.Text = "Chat history is unavailable. Check access to the history folder.";
        }
        finally
        {
            _loadingChats = false;
        }
    }

    private async Task OpenChatAsync(Guid id)
    {
        if (id == _chatId) return;
        try
        {
            _history.SelectConversation(id);
        }
        catch (Exception)
        {
            LoadChats();
            _status.Text = "Could not switch chats. Check access to the history folder.";
            return;
        }

        LoadChats();
        _preview?.Hide();
        _transcript.Clear();
        _answer.Clear();
        _status.Text = "Chat selected.";
        if (_historyWindow is { } history) await history.SetChatAsync(_chatId);
    }

    public void SetHotkeyMessage(string message) => _hotkeyMessage.Text = message;

    public async Task RefreshDevicesAsync()
    {
        if (_suspended || _loadingDevices || _operation is not null)
        {
            return;
        }

        _refreshOnResume = false;
        _loadingDevices = true;
        RefreshControls();
        try
        {
            var selection = (_microphone.SelectedItem as Microphone)?.Id;
            var devices = await Task.Run(_recorder.ListMicrophones);
            _microphone.ItemsSource = devices.Count == 0 ? Array.Empty<Microphone>() : new[] { _defaultMicrophone }.Concat(devices).ToArray();
            _microphone.SelectedItem = devices.FirstOrDefault(device => device.Id == selection)
                ?? (devices.Count > 0 ? _defaultMicrophone : null);
            if (_operation is null && devices.Count == 0)
            {
                _status.Text = "No active microphone found. Connect one and refresh devices.";
            }
            else if (_status.Text?.StartsWith("No active microphone", StringComparison.Ordinal) == true)
            {
                _status.Text = "Microphone found.";
            }
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            _microphone.ItemsSource = Array.Empty<Microphone>();
            _status.Text = FriendlyError(error, "Could not find microphones");
        }
        finally
        {
            _loadingDevices = false;
            RefreshControls();
        }
    }

    public Task ToggleFloatingRecordingAsync() =>
        ToggleRecordingAsync(_stop is { IsCancellationRequested: false } ? _recordMode : VoiceShortcutMode.Background);

    public async Task ToggleRecordingAsync(VoiceShortcutMode mode = VoiceShortcutMode.MainWindow)
    {
        if (_stop is { IsCancellationRequested: false } stop)
        {
            if (_recordMode != mode) return;
            stop.Cancel();
            RefreshControls();
            _record.IsEnabled = false;
            if (_recordMode == VoiceShortcutMode.Preview)
                GetPreview().ShowStatus("Finishing recording…");
            return;
        }

        if (_operation is not null)
        {
            return;
        }

        if (mode == VoiceShortcutMode.SharedChat && _sharedChat?.HasChat != true)
        {
            GetPreview().ShowStatus("Open Choose Codex Desktop chat. Find chats and select one.");
            return;
        }
        _replyAudio?.Stop();

        DesktopDictation.Target? dictationTarget = null;
        if (mode == VoiceShortcutMode.Dictation)
        {
            dictationTarget = DesktopDictation.Capture();
            if (dictationTarget is null)
            {
                GetPreview().ShowStatus("Open a chat in Codex Desktop and focus its message field, then press the dictation shortcut.");
                return;
            }
        }

        var loopback = mode == VoiceShortcutMode.WindowsAudio;
        if (!loopback && _microphone.SelectedItem is null)
        {
            await RefreshDevicesAsync();
        }

        if (_suspended || _operation is not null) return;
        var microphone = _microphone.SelectedItem as Microphone;
        if (!ModelAvailable || (!loopback && microphone is null))
        {
            if (mode is VoiceShortcutMode.Preview or VoiceShortcutMode.Dictation)
                GetPreview().ShowStatus(!ModelAvailable
                    ? "Speech recognition is unavailable. Check the selected model in Settings."
                    : "No microphone selected. Check the microphone in Trayvora.");
            return;
        }

        using var operation = new CancellationTokenSource();
        if (mode is VoiceShortcutMode.Background or VoiceShortcutMode.WindowsAudio) Hide();
        _preview?.Hide();
        _recordMode = mode;
        using var stopRecording = new CancellationTokenSource();
        _operation = operation;
        _stop = stopRecording;
        var bytes = new byte[VoiceCapture.Capacity];
        var language = _languageCodes[Math.Max(0, _language.SelectedIndex)];
        var stopwatch = Stopwatch.StartNew();
        var source = loopback ? "Windows audio" : "Microphone";
        _status.Text = $"{source} on · up to {VoiceCapture.MaxSeconds} seconds";
        _progress.IsVisible = true;
        RefreshControls();
        string? draft = null;
        try
        {
            var transcriber = OperatingSystem.IsWindows() && _speech is not null ? _speech.CreateTranscriber() : _transcriber;
            var progress = new Progress<double>(peak =>
            {
                if (ReferenceEquals(_stop, stopRecording) && !operation.IsCancellationRequested)
                {
                    _progress.Value = peak * 100;
                    _status.Text = $"{source} on · {stopwatch.Elapsed:mm\\:ss}";
                }
            });
            var count = await Task.Run(() =>
            {
                if (loopback)
                {
                    if (!OperatingSystem.IsWindows() || _recorder is not WasapiRecorder windows)
                        throw new InvalidOperationException("Windows audio capture is unavailable.");
                    return windows.RecordOutputAsync(bytes, progress, stopRecording.Token, operation.Token);
                }
                return _recorder.RecordAsync(
                    string.IsNullOrEmpty(microphone?.Id) ? null : microphone.Id, bytes, progress,
                    stopRecording.Token, operation.Token);
            });

            _stop = null;
            _transcribing = true;
            _status.Text = "Transcribing audio…";
            if (mode == VoiceShortcutMode.Preview)
                GetPreview().ShowStatus(_status.Text);
            _progress.IsIndeterminate = true;
            RefreshControls();
            var text = await Task.Run(() => transcriber.TranscribeAsync(
                bytes.AsMemory(0, count), language, operation.Token));
            operation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text))
            {
                _status.Text = "No speech detected.";
            }
            else
            {
                _transcript.Text = text;
                _status.Text = "Transcript ready.";
                draft = text;
                if (mode == VoiceShortcutMode.MainWindow) _transcript.Focus();
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _status.Text = "Recording or transcription cancelled.";
        }
        catch (Exception error)
        {
            if (error is WhisperModelVerificationException)
            {
                _modelNeedsReplacement = true;
            }

            _status.Text = FriendlyError(error, _stop is null ? "Could not transcribe" : $"{source} unavailable");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            _transcribing = false;
            EndOperation();
        }

        if (operation.IsCancellationRequested || _suspended)
        {
            if (mode == VoiceShortcutMode.Preview) _preview?.Hide();
            return;
        }
        if (draft is null)
        {
            if (mode is VoiceShortcutMode.Preview or VoiceShortcutMode.Dictation)
                GetPreview().ShowStatus(_status.Text ?? "No speech detected.");
            return;
        }
        if (mode is VoiceShortcutMode.Background or VoiceShortcutMode.WindowsAudio)
        {
            _codexRun = SendToActiveChatAsync(draft, RequestRoute.Auto, silent: true);
            await _codexRun;
        }
        else if (mode == VoiceShortcutMode.SharedChat)
        {
            await SendSharedTrackedAsync(draft, silent: true);
        }
        else if (mode == VoiceShortcutMode.Preview)
        {
            GetPreview().ShowDraft(draft, PreviewSendShortcut, !QuestionsActive && _sharedChat?.HasChat == true ? _sharedChat.TargetTitle : null, QuestionsActive);
        }
        else if (dictationTarget is { } target)
        {
            await InsertDictationAsync(target, draft);
        }
    }

    /// <summary>
    /// Records from the selected microphone until <paramref name="stopRecording"/> fires (or the 30-second limit)
    /// and returns the transcript. Used by session dictation; shares the main window's microphone, language and recognizer.
    /// </summary>
    public async Task<string?> DictateAsync(CancellationToken stopRecording, CancellationToken cancel, IProgress<double> level)
    {
        if (_suspended || _operation is not null) throw new IOException("Trayvora is busy with another recording or request. Try again when it finishes.");
        if (_microphone.SelectedItem is null) await RefreshDevicesAsync();
        if (_microphone.SelectedItem is not Microphone microphone) throw new IOException("No microphone found. Check the microphone in the main window.");
        if (!ModelAvailable) throw new IOException("Speech recognition is unavailable. Check the speech model in Settings.");
        if (_suspended || _operation is not null) throw new IOException("Trayvora is busy with another recording or request.");

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _operation = operation;
        _replyAudio?.Stop();
        var bytes = new byte[VoiceCapture.Capacity];
        var language = _languageCodes[Math.Max(0, _language.SelectedIndex)];
        RefreshControls();
        try
        {
            var transcriber = OperatingSystem.IsWindows() && _speech is not null ? _speech.CreateTranscriber() : _transcriber;
            var count = await Task.Run(() => _recorder.RecordAsync(
                string.IsNullOrEmpty(microphone.Id) ? null : microphone.Id, bytes, level, stopRecording, operation.Token));
            operation.Token.ThrowIfCancellationRequested();
            _transcribing = true;
            RefreshControls();
            var text = await Task.Run(() => transcriber.TranscribeAsync(bytes.AsMemory(0, count), language, operation.Token));
            operation.Token.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception error) when (error is not OperationCanceledException and not IOException)
        {
            if (error is WhisperModelVerificationException) _modelNeedsReplacement = true;
            throw new IOException(FriendlyError(error, "Could not record or transcribe"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            _transcribing = false;
            EndOperation();
        }
    }

    public Task ToggleQuestionsAsync(bool longMemory)
    {
        var index = longMemory ? 2 : 1;
        if (_suspended || (_operation is not null && (_mode.SelectedIndex != index || _recordMode != VoiceShortcutMode.Background)))
            return Task.CompletedTask;
        _mode.SelectedIndex = index;
        return ToggleRecordingAsync(VoiceShortcutMode.Background);
    }

    private void LoadQuestionMemory()
    {
        if (_memoryFile is null || !File.Exists(_memoryFile)) return;
        _memoryReadFailed = true;
        try
        {
            using var file = File.OpenRead(_memoryFile);
            var memory = System.Text.Json.JsonSerializer.Deserialize<QuestionMemory>(file);
            if (memory?.Exchanges is not null && memory.Summarized >= 0 && memory.Summarized <= memory.Exchanges.Count)
            {
                _memory = memory;
                _memoryReadFailed = false;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _memory = new([], null, 0);
        }
    }

    private static bool NeedsSummary(QuestionMemory memory)
    {
        var count = memory.Exchanges.Count - memory.Summarized;
        return count > 16 || (count > 2 && memory.Exchanges.Skip(memory.Summarized)
            .Sum(exchange => (long)exchange.Question.Length + exchange.Answer.Length) > 24000);
    }

    private sealed record QuestionMemory(List<QuestionExchange> Exchanges, string? Summary, int Summarized);

    private Task SendToActiveChatAsync(string? draft = null, RequestRoute? route = null, bool silent = false)
    {
        if (ClaudeActive) return SendClaudeAsync(draft ?? _transcript.Text);
        if (QuestionsActive) return AskQuestionAsync(draft ?? _transcript.Text, silent);
        if (_sharedChat?.HasChat != true) return SendAsync(draft, route);
        var text = draft ?? _transcript.Text;
        return string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : SendSharedTrackedAsync(text, silent);
    }

    private sealed record ClaudeRoute(string Executable, string Profile, ClaudeChat? Chat);

    private string ClaudeProfile() => string.IsNullOrWhiteSpace(_claudeProfile.Text) ? ClaudeChats.DefaultProfile() : _claudeProfile.Text.Trim();

    private void InvalidateClaudeSelection()
    {
        _claudeChat = null;
        _claudeItems = [];
        _claudeChats.ItemsSource = _claudeItems;
        RefreshControls();
    }

    private async Task SendClaudeAsync(string? text)
    {
        if (_suspended || _operation is not null || string.IsNullOrWhiteSpace(text)) return;
        if (_claudeChat is not { } chat)
        {
            _status.Text = "Select a Claude Code session first.";
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        _replyAudio?.Stop();
        _answer.Clear();
        _feed.Begin(text, "Claude Code");
        _status.Text = $"Waiting for Claude Code · {chat.Title}";
        RefreshControls();
        // Show Claude's reply while it is written.
        var stream = new ReplyStream();
        var shown = -1;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            var (partial, activity, version) = stream.Snapshot();
            if (version == shown) return;
            shown = version;
            _answer.Text = partial;
            _answer.CaretIndex = partial.Length;
            _status.Text = activity is null ? $"Claude Code · {chat.Title}" : $"Claude Code · {activity}";
        };
        timer.Start();
        try
        {
            var reply = await ClaudeChats.SendAsync(_claudeExe.Text?.Trim() ?? "", ClaudeProfile(), chat, text, operation.Token, stream);
            timer.Stop();
            _answer.Text = reply;
            _status.Text = stream.Notes ?? "Claude response ready.";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { _status.Text = "Claude request cancelled."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _status.Text = error.Message; }
        catch (Exception)
        { _status.Text = "Could not run Claude Code. Check the executable and session."; }
        finally
        {
            timer.Stop();
            EndOperation();
        }
    }

    private async Task AskQuestionAsync(string? question, bool silent)
    {
        if (_suspended || _operation is not null || string.IsNullOrWhiteSpace(question) || !OperatingSystem.IsWindows()) return;
        if (LongQuestionsActive && _memoryReadFailed)
        {
            _status.Text = "Could not read Memory. Start a new conversation to archive the old file.";
            if (!silent && !IsVisible) GetPreview().ShowStatus(_status.Text);
            return;
        }
        var key = _speech?.Current.ProtectedKey ?? _replyAudio?.SavedSeparateKey;
        if (key is null)
        {
            _status.Text = "Save an OpenAI API key in Speech recognition or Playback settings first.";
            if (!silent && !IsVisible) GetPreview().ShowStatus(_status.Text);
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        _replyAudio?.Stop();
        _answer.Clear();
        _feed.Begin(question, "GPT");
        _status.Text = "Waiting for GPT-6 Luna…";
        if (!silent && !IsVisible) GetPreview().ShowStatus(_status.Text);
        RefreshControls();
        string? answer = null;
        try
        {
            var api = new OpenAiQuestions();
            var credential = SpeechSettings.UnprotectKey(key);
            var memory = _memory;
            while (LongQuestionsActive && NeedsSummary(memory))
            {
                var count = Math.Min(4, memory.Exchanges.Count - memory.Summarized - 2);
                var batch = memory.Exchanges.Skip(memory.Summarized).Take(count).ToArray();
                _status.Text = "Summarizing older exchanges…";
                var summary = await api.SummarizeAsync(credential, memory.Summary, batch, operation.Token);
                memory = memory with { Summary = summary, Summarized = memory.Summarized + count };
            }
            var history = LongQuestionsActive ? memory.Exchanges.Skip(memory.Summarized).ToArray() : _questions.ToArray();
            answer = await api.AskAsync(credential, question, history, operation.Token, LongQuestionsActive ? memory.Summary : null);
            operation.Token.ThrowIfCancellationRequested();
            _answer.Text = answer;
            _status.Text = "Answer ready.";
            if (LongQuestionsActive && _memoryFile is not null)
            {
                memory.Exchanges.Add(new QuestionExchange(question, answer));
                _memory = memory;
                try { SettingsFile.Write(_memoryFile, memory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { _status.Text = "Answer ready. Could not save Memory beside the executable."; }
            }
            else
            {
                _questions.Add(new QuestionExchange(question, answer));
                if (_questions.Count > 6) _questions.RemoveAt(0);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            answer = null;
            _status.Text = "Question cancelled.";
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            _status.Text = error.Message;
        }
        catch (Exception)
        {
            _status.Text = "Could not get an answer. Check the saved API key and connection.";
        }
        finally
        {
            EndOperation();
        }
        if (!silent && !IsVisible && !_suspended)
        {
            if (answer is null) GetPreview().ShowStatus(_status.Text ?? "Could not get an answer.");
            else GetPreview().ShowAnswer(answer);
        }
        if (answer is not null && _sharedChat?.ReadAloud == true && _replyAudio is not null)
            _ = _replyAudio.ReadAsync(answer);
    }

    private Task SendSharedTrackedAsync(string text, bool silent)
    {
        if (_suspended || _operation is not null || _sharedChat?.HasChat != true) return Task.CompletedTask;
        _codexRun = SendSharedAsync(_sharedChat, text, silent);
        return _codexRun;
    }

    private async Task SendSharedAsync(DesktopChatPanel shared, string text, bool silent)
    {
        using var operation = new CancellationTokenSource();
        _operation = operation;
        _replyAudio?.Stop();
        _answer.Clear();
        _feed.Begin(text, "Codex Desktop");
        _status.Text = $"Sending to Codex Desktop: {shared.TargetTitle}…";
        var background = !silent && !IsVisible;
        if (background) GetPreview().ShowStatus(_status.Text);
        RefreshControls();
        DesktopTurn? completedReply = null;
        try
        {
            var result = await shared.SubmitAsync(text, operation.Token);
            completedReply = result;
            _answer.Text = result?.Answer;
            _status.Text = result is null
                ? $"Queued in {shared.TargetTitle}. Open it in Codex Desktop to check progress."
                : $"Desktop chat: {result.Status}.";
            if (background) GetPreview().ShowStatus(_status.Text);
            if (result is not null)
                RequestEnded?.Invoke(result.Status == "completed" ? RequestStatus.Completed : RequestStatus.Failed);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _status.Text = "Stopped waiting. Check Codex Desktop before resending.";
            shared.Report(_status.Text);
            if (background && !_suspended && !operation.IsCancellationRequested) GetPreview().ShowStatus(_status.Text);
        }
        catch (Exception)
        {
            _status.Text = "Could not confirm delivery or read the reply. Check the selected Desktop chat before resending.";
            shared.Report(_status.Text);
            if (background && !_suspended && !operation.IsCancellationRequested) GetPreview().ShowStatus(_status.Text);
        }
        finally
        {
            EndOperation();
        }
        if (completedReply is { Status: "completed" } && shared.ReadAloud && _replyAudio is not null)
            _ = _replyAudio.ReadAsync(completedReply.Answer);
    }

    public void AdjustPlaybackVolume(int steps) => _replyAudio?.AdjustVolume(steps);

    public void DisposeSharedChat()
    {
        _discussion?.Pause();
        _accounts?.Stop();
        if (OperatingSystem.IsWindows())
        {
            _audioToText?.Stop();
            _textToAudio?.Dispose();
        }
        // Let queued session writes finish before the process exits.
        try { _discussion?.PendingSaves.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        _replyAudio?.Dispose();
    }

    private async Task InsertDictationAsync(DesktopDictation.Target target, string draft)
    {
        using var insertion = new CancellationTokenSource();
        _operation = insertion;
        RefreshControls();
        try
        {
            var inserted = await DesktopDictation.InsertAsync(target, draft, insertion.Token);
            if (!insertion.IsCancellationRequested && !inserted) GetPreview().ShowDictationDraft(draft);
            _status.Text = inserted ? "Draft inserted in Codex Desktop." : "Dictation draft ready to copy.";
        }
        catch (OperationCanceledException) when (insertion.IsCancellationRequested)
        {
            _status.Text = "Dictation cancelled.";
        }
        finally
        {
            EndOperation();
        }
    }

    private TranscriptPreview GetPreview()
    {
        if (_preview is null)
        {
            _preview = new TranscriptPreview(() => _ = SendPreviewAsync());
            _preview.Closed += (_, _) => _preview = null;
        }

        return _preview;
    }

    public async Task CancelAndWaitAsync()
    {
        Cancel();
        if (_codexRun is { } run)
        {
            await run;
        }
    }

    public async Task ShowHistoryAsync()
    {
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow(_history);
            _historyWindow.Closed += (_, _) => _historyWindow = null;
        }

        _historyWindow.Show();
        _historyWindow.Activate();
        await _historyWindow.SetChatAsync(_chatId);
    }

    public async Task SendPreviewAsync()
    {
        if (_suspended || _operation is not null || _preview?.TakeDraft() is not { } draft) return;
        _transcript.Text = draft;
        _codexRun = SendToActiveChatAsync(draft, RequestRoute.Auto);
        await _codexRun;
    }

    public void SpeechSettingsChanged()
    {
        _modelNeedsReplacement = false;
        _replyAudio?.RefreshKeyChoices();
        RefreshControls();
    }

    private async Task SendAsync(string? draft = null, RequestRoute? selectedRoute = null)
    {
        var text = draft ?? _transcript.Text;
        if (_suspended || _operation is not null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        using var abort = new CancellationTokenSource();
        _codexAbort = abort;
        _emergencyRequested = false;
        _replyAudio?.Stop();
        _answer.Text = "";
        _feed.Begin(text, "Codex");
        _status.Text = "Waiting for Codex…";
        RefreshControls();
        var route = RequestRouting.Select(text, selectedRoute ?? (RequestRoute)Math.Max(0, _route.SelectedIndex));
        var localWorkspace = LocalWorkspace;
        var chatId = _chatId;
        var reply = new StringBuilder();
        Guid? historyId = null;
        var historyStatus = RequestStatus.Failed;
        string? detail = null;
        var receiving = true;
        var historyUnavailable = false;
        IReadOnlyList<ToolActivity> tools = [];
        try
        {
            try
            {
                historyId = await Task.Run(() => _history.Start(text, chatId, localGit: route == RequestRoute.LocalGit));
                if (_historyWindow is { } history) await history.ReloadAsync();
            }
            catch (Exception)
            {
                historyUnavailable = true;
            }

            var progress = new CodexOutput(delta =>
            {
                reply.Append(delta);
                Dispatcher.UIThread.Post(() =>
                {
                    if (receiving && ReferenceEquals(_operation, operation))
                    {
                        _answer.Text += delta;
                        _answer.CaretIndex = _answer.Text?.Length ?? 0;
                        if (!operation.IsCancellationRequested)
                        {
                            _status.Text = "Receiving response…";
                        }
                    }
                });
            });
            if (route == RequestRoute.LocalGit)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (localWorkspace is null)
                    throw new InvalidOperationException("Choose a folder in Local workspace first.");
                reply.Append(await LocalActions.ReadGitHeadAsync(localWorkspace, operation.Token));
                tools = [new ToolActivity("Local Git · branch and HEAD", "Completed")];
                historyStatus = RequestStatus.Completed;
                _status.Text = "Read directly from the selected folder.";
                return;
            }

            var routedText = route == RequestRoute.AppMcp
                ? "Use the Revqora MCP tools for the Git request. Discover repositories with list_repositories before selecting a repository when needed. If the relevant tool is unavailable, say so. User request:\n" + text
                : text;
            var localResults = historyUnavailable ? null : await Task.Run(() => _history.RecentLocalResults(chatId));
            var status = await Task.Run(() => _codex.RunAsync(routedText, progress, operation.Token,
                activities => tools = activities, abort.Token, useRevqora: route == RequestRoute.AppMcp,
                chatId: chatId, localResults: localResults));
            (historyStatus, _status.Text) = status switch
            {
                CodexTurnStatus.Completed => (RequestStatus.Completed, "Codex completed the request."),
                CodexTurnStatus.Interrupted => (RequestStatus.Interrupted, "Codex interrupted the request."),
                CodexTurnStatus.Failed => (RequestStatus.Failed, "Codex reported a failed request."),
                _ => (RequestStatus.Unknown, "Codex connection ended before the final status. Outcome unknown."),
            };
            if (historyStatus is RequestStatus.Failed or RequestStatus.Unknown) detail = _status.Text;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            historyStatus = RequestStatus.Cancelled;
            _status.Text = "Request cancelled before sending.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException
            or TimeoutException or ArgumentException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
            detail = "Codex could not start the request.";
        }
        catch (Exception error)
        {
            _status.Text = FriendlyError(error, "Could not connect to Codex");
            detail = "Could not connect to Codex.";
        }
        finally
        {
            receiving = false;
            if (_emergencyRequested)
            {
                const string audit = "Emergency stop requested by the user.";
                detail = detail is null ? audit : $"{detail} {audit}";
            }
            var answer = reply.ToString();
            _answer.Text = answer;
            _answer.CaretIndex = answer.Length;
            if (historyId is { } id)
            {
                try
                {
                    await Task.Run(() => _history.Finish(id, historyStatus, answer, detail,
                        string.Join(Environment.NewLine, tools.Select(tool => $"{tool.Name} · {tool.Status}"))));
                    if (_historyWindow is { } history) await history.ReloadAsync();
                }
                catch (Exception)
                {
                    historyUnavailable = true;
                }
            }

            if (historyUnavailable) _status.Text += " History could not be saved.";
            _codexAbort = null;
            EndOperation();
            if (tools.Any(tool => tool.Status == "Denied"))
            {
                var close = new Button { Content = "Close" };
                var notice = new Window
                {
                    Title = "Action blocked",
                    Icon = TrayvoraTheme.AppIcon,
                    Width = 420,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(20),
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "This request attempted an action outside the read-only profile. See Tool activity in History.",
                                TextWrapping = TextWrapping.Wrap,
                            },
                            close,
                        },
                    },
                };
                close.Click += (_, _) => notice.Close();
                if (IsActive) _ = notice.ShowDialog(this);
            }

            RequestEnded?.Invoke(historyStatus);
        }
    }

    public void OnSystemSuspend()
    {
        _suspended = true;
        _replyAudio?.Stop();
        _preview?.Hide();
        Cancel();
        RefreshControls();
    }

    public async Task OnSystemResumeAsync()
    {
        _suspended = false;
        Cancel();
        _refreshOnResume = true;
        RefreshControls();
        if (IsVisible) await RefreshDevicesAsync();
    }

    public void EmergencyStop()
    {
        Cancel();
        if (_codexAbort is { IsCancellationRequested: false } abort)
        {
            _emergencyRequested = true;
            _ = abort.CancelAsync();
        }
    }

    public void Cancel()
    {
        if (_operation is { IsCancellationRequested: false } operation)
        {
            _status.Text = "Cancelling…";
            operation.Cancel();
            RefreshControls();
        }
    }

    private async Task DownloadAsync()
    {
        if (_suspended || _operation is not null)
        {
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        _progress.IsVisible = true;
        _status.Text = "Downloading the model from Hugging Face…";
        RefreshControls();
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (ReferenceEquals(_operation, operation) && !operation.IsCancellationRequested)
                {
                    _progress.Value = value * 100;
                }
            });
            await (OperatingSystem.IsWindows() && _speech is not null ? _speech.Model : _model).DownloadAsync(progress, operation.Token);
            _status.Text = "Model verified. Ready to record.";
            _modelNeedsReplacement = false;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _status.Text = "Download cancelled.";
        }
        catch (Exception error)
        {
            _status.Text = FriendlyError(error, "Model download failed. Try again");
        }
        finally
        {
            EndOperation();
        }
    }

    private bool ModelAvailable => (OperatingSystem.IsWindows() && _speech is not null ? _speech.Ready : _model.Exists) && !_modelNeedsReplacement;

    /// <summary>Shows the feed of the active mode and chat; a chat seen for the first time is filled from its saved history.</summary>
    private void SyncFeed(bool desktopActive)
    {
        if (ClaudeActive)
            _feed.Use($"claude:{_claudeChat?.Id}", _claudeChat is null ? "Choose a Claude Code session above, then type or dictate a message." : "Your messages to this Claude Code session and its replies appear here.");
        else if (LongQuestionsActive)
            _feed.Use("questions:long", "Ask by voice or type. These answers keep a long memory.",
                () => _memory.Exchanges.TakeLast(20).Select(item => new ChatFeed.Entry(item.Question, item.Answer, "GPT")).ToList());
        else if (QuestionsActive)
            _feed.Use("questions:short", "Ask a quick question by voice or type. GPT remembers the last six exchanges.",
                () => _questions.Select(item => new ChatFeed.Entry(item.Question, item.Answer, "GPT")).ToList());
        else if (desktopActive)
            _feed.Use($"desktop:{_sharedChat!.TargetTitle}", "Messages you send to this Codex Desktop chat appear here.");
        else
        {
            var chat = _chatId;
            _feed.Use($"codex:{chat}", "Tell Codex what to do on this computer — type or press Record.", () => CodexHistory(chat));
        }
    }

    private List<ChatFeed.Entry> CodexHistory(Guid chat)
    {
        var entries = new List<ChatFeed.Entry>();
        foreach (var summary in _history.Recent(chat).Take(15).Reverse())
        {
            if (summary.Status == RequestStatus.Running || _history.Find(summary.Id) is not { } details) continue;
            var failed = string.IsNullOrWhiteSpace(details.Answer);
            entries.Add(new ChatFeed.Entry(details.Prompt, failed ? $"No reply ({summary.Status})." : details.Answer, "Codex", failed));
        }
        return entries;
    }

    private void RefreshControls()
    {
        var idle = !_suspended && _operation is null;
        _record.IsEnabled = (!_suspended && _stop is { IsCancellationRequested: false } && _operation?.IsCancellationRequested == false)
            || (idle && !_loadingDevices && ModelAvailable && _microphone.SelectedItem is not null);
        _send.IsEnabled = idle && !string.IsNullOrWhiteSpace(_transcript.Text);
        _transcript.IsReadOnly = !idle;
        _answer.IsReadOnly = !idle;
        _replyAudio?.Refresh(idle);
        var desktopActive = _sharedChat?.HasChat == true;
        _mode.IsEnabled = idle;
        _clearQuestions.IsVisible = QuestionsActive;
        _clearQuestions.IsEnabled = idle;
        _claudeExe.IsEnabled = _claudeProfile.IsEnabled = _claudeChats.IsEnabled = idle;
        _destination.Text = ClaudeActive ? $"Claude Code · {_claudeChat?.Title ?? "Choose a session"}" : QuestionsActive ? (LongQuestionsActive ? "GPT-6 Luna · long memory" : "GPT-6 Luna · last six exchanges") : desktopActive ? $"Active chat: Codex Desktop · {_sharedChat!.TargetTitle}"
            : $"Active chat: Trayvora · {(_chats.SelectedItem as Conversation)?.Title ?? "local"}";
        if (idle) SyncFeed(desktopActive);
        _send.Content = ClaudeActive ? "Send to Claude" : QuestionsActive ? "Ask" : desktopActive ? "Send to Desktop" : "Send to Codex";
        _route.IsEnabled = idle && !desktopActive && !QuestionsActive && !ClaudeActive;
        _chats.IsEnabled = _newChat.IsEnabled = idle && !desktopActive && !QuestionsActive && !ClaudeActive;
        _sharedChat?.Refresh(idle);
        _cancel.IsEnabled = _operation is { IsCancellationRequested: false };
        _microphone.IsEnabled = idle && !_loadingDevices;
        _language.IsEnabled = idle;
        _download.IsVisible = !ModelAvailable && !(OperatingSystem.IsWindows() && _speech?.Current.Cloud == true);
        _download.IsEnabled = idle;
        _refresh.IsEnabled = idle && !_loadingDevices;
        for (var index = 0; index < _modeButtons.Length; index++)
        {
            TrayvoraTheme.SetSelected(_modeButtons[index], _mode.SelectedIndex == index);
            _modeButtons[index].IsEnabled = idle || _mode.SelectedIndex == index;
        }
        _modeHint.Text = ClaudeActive ? "Messages resume the selected Claude Code session."
            : QuestionsActive ? (LongQuestionsActive ? "Long memory: full transcript saved under Memory, older turns summarized." : "Short memory: keeps the last six exchanges until restart.")
            : desktopActive ? "Messages go to the selected Codex Desktop chat. Codex Desktop keeps the permissions."
            : "Codex runs the request in the Trayvora profile with read-only tools.";
        var recording = _stop is { IsCancellationRequested: false } && _operation is not null;
        TrayvoraTheme.SetSelected(_record, recording);
        if (recording && !_record.Classes.Contains("recording")) _record.Classes.Add("recording");
        if (!recording) _record.Classes.Remove("recording");
        // Built once; only the glyph and label change.
        _recordContent ??= new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7,
            Children = { _recordIcon, _recordLabel },
        };
        if (!ReferenceEquals(_record.Content, _recordContent)) _record.Content = _recordContent;
        _recordIcon.Text = recording ? TrayvoraTheme.IconStop : TrayvoraTheme.IconMic;
        _recordLabel.Text = recording ? "Stop" : "Record";
        var state = CurrentAgentState;
        (_stateText.Text, _stateDot.Background) = state switch
        {
            AgentState.Recording => ("Recording…", TrayvoraTheme.Danger),
            AgentState.Transcribing => ("Transcribing…", TrayvoraTheme.Warning),
            AgentState.Sending => ("Waiting for reply…", TrayvoraTheme.Accent),
            AgentState.Busy => (_suspended ? "Paused (sleep)" : "Working…", TrayvoraTheme.Warning),
            _ => (ModelAvailable ? "Ready" : "Speech model missing", ModelAvailable ? TrayvoraTheme.Success : TrayvoraTheme.Warning),
        };
        AgentStateChanged?.Invoke(state);
    }

    private void EndOperation()
    {
        _stop = null;
        _operation = null;
        _progress.IsVisible = false;
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _feed.Finish(_status.Text);
        RefreshControls();
        if (_refreshOnResume && IsVisible) _ = RefreshDevicesAsync();
    }

    private static StackPanel Row(params Control[] controls) => TrayvoraTheme.Row(controls);

    private static string FriendlyError(Exception error, string fallback) => error switch
    {
        WhisperModelVerificationException or InvalidDataException => error.Message,
        UnauthorizedAccessException => "Access denied. Check Windows microphone permissions or the model folder.",
        FileNotFoundException => "The local model is missing. Download it and retry.",
        _ => $"{fallback} (0x{error.HResult:X8}).",
    };

    private sealed class CodexOutput(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
