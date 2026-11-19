using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VoiceAgent.Codex;

namespace VoiceAgent.Desktop;

internal sealed record SharedChatOptions(string Profile, Guid? ChatId = null, bool ReadAloud = true, string? ChatTitle = null, string? Workspace = null);

internal sealed class DesktopChatPanel : StackPanel
{
    private readonly DesktopChatClient _client;
    private readonly string _file;
    private readonly TextBox _profile = new() { PlaceholderText = "Codex Desktop profile folder" };
    private readonly ComboBox _chats = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 320, PlaceholderText = "Select a Codex Desktop chat" };
    private readonly Button _load = new() { Content = "Find Desktop chats" };
    private readonly TextBox _search = new() { PlaceholderText = "Search chats by title" };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Height = 3, IsVisible = false };
    private readonly Button _open = new() { Content = "Open in Codex", IsEnabled = false };
    private string? _cursor;
    private string? _cursorProfile;
    private readonly Button _more = new() { Content = "Load more", IsVisible = false };
    private readonly Button _new = new() { Content = "New in Codex" };
    private readonly Button _read = new() { Content = "Read history", IsEnabled = false };
    private readonly Button _local = new() { Content = "Switch to Trayvora chats" };
    private readonly CheckBox _auto = new() { Content = "Read replies aloud" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _reply = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxHeight = 210 };
    private SharedChatOptions _options;
    private bool _loading;
    private bool _changingChatList;
    private bool _idle = true;
    private bool _loaded;
    private IReadOnlyList<DesktopChat> _items = [];

    public DesktopChatPanel(string settingsDirectory)
    {
        _file = Path.Combine(settingsDirectory, "desktop-chat.json");
        _client = new DesktopChatClient(Path.Combine(settingsDirectory, "desktop-protocol"));
        _options = ReadOptions(_file);
        _profile.Text = _options.Profile;
        _auto.IsChecked = _options.ReadAloud;
        if (_options.ChatId is { } id) _chats.ItemsSource = new[] { new DesktopChat(id, TargetTitle, null) };
        _chats.SelectedIndex = 0;
        TrayvoraTheme.StyleControls(_profile, _search, _chats, _load, _new, _open, _read, _local, _reply);
        _status.Foreground = TrayvoraTheme.Muted;
        _reply.IsVisible = false;
        _reply.TextChanged += (_, _) => _reply.IsVisible = !string.IsNullOrWhiteSpace(_reply.Text);
        Spacing = 9;
        Children.AddRange([
            _search, _chats, Row(_open, _new), Row(_load, _more, _local), _busy, _status,
            new Expander { Header = "Desktop chat history", Content = new StackPanel
            {
                Spacing = 8,
                Children = { _read, _reply },
            } },
            new Expander { Header = "Desktop profile folder", Content = _profile },
        ]);
        _more.Click += async (_, _) => await LoadAsync(more: true);
        _load.Click += async (_, _) => await LoadAsync();
        _search.TextChanged += (_, _) => FilterChats();
        _read.Click += async (_, _) => await ReadAsync();
        _open.Click += (_, _) => Open($"codex://threads/{_options.ChatId}");
        _new.Click += (_, _) => Open("codex://threads/new");
        _chats.SelectionChanged += (_, _) =>
        {
            if (!_idle || _loading || _changingChatList || _chats.SelectedItem is not DesktopChat chat) return;
            if (TrySave(_options with { ChatId = chat.Id, ChatTitle = chat.Title, Workspace = chat.Workspace }))
                ChangeTarget($"Active Desktop chat: {TargetTitle}");
            else
                FilterChats();
        };
        _local.Click += (_, _) =>
        {
            if (_idle && !_loading && TrySave(_options with { ChatId = null, ChatTitle = null, Workspace = null }))
            {
                FilterChats();
                ChangeTarget("Trayvora chats are active.");
            }
        };
        _auto.IsCheckedChanged += (_, _) => TrySave(_options with { ReadAloud = _auto.IsChecked == true });
        Refresh(_idle);
    }

    public Control AutoPlaybackControl => _auto;
    public bool ReadAloud => _auto.IsChecked == true;

    public Guid? TargetId => _options.ChatId;
    public string? TargetWorkspace => _options.Workspace ?? _items.FirstOrDefault(item => item.Id == TargetId)?.Workspace;
    public bool HasChat => _options.ChatId is not null;
    public string TargetTitle => _options.ChatTitle ?? (_options.ChatId is { } id ? $"Chat {id.ToString()[..8]}" : "");
    public event Action? TargetChanged;

    private void ChangeTarget(string status)
    {
        _reply.Clear();
        _status.Text = status;
        Refresh(_idle);
        TargetChanged?.Invoke();
    }

    public void Refresh(bool idle)
    {
        _idle = idle;
        var unlocked = _idle && !_loading;
        _profile.IsEnabled = _load.IsEnabled = _new.IsEnabled = _chats.IsEnabled = _search.IsEnabled = unlocked;
        _more.IsEnabled = unlocked;
        _busy.IsVisible = _loading;
        _local.IsEnabled = unlocked && HasChat;
        _read.IsEnabled = unlocked && HasChat;
        _open.IsEnabled = HasChat;
    }

    public async Task<DesktopTurn?> SubmitAsync(string text, CancellationToken ct)
    {
        if (_options.ChatId is not { } chat) throw new InvalidOperationException("Select a Codex Desktop chat first.");
        _reply.Clear();
        var result = await _client.SubmitAsync(_options.Profile, chat, text, new Progress<string>(message =>
        {
            if (!ct.IsCancellationRequested) _status.Text = message;
        }), ct);
        ct.ThrowIfCancellationRequested();
        if (result is null)
        {
            _status.Text = $"Queued in {TargetTitle}. Open it in Codex to check progress.";
            return null;
        }

        _status.Text = $"Codex: {result.Status}.";
        _reply.Text = result.Answer;
        return result;
    }

    public void Report(string text) => _status.Text = text;

    /// <summary>Lists Codex Desktop chats in the configured profile without changing the main-window selection.</summary>
    public Task<IReadOnlyList<DesktopChat>> ListChatsAsync(CancellationToken ct) => _client.ListAsync(_options.Profile, ct);

    /// <summary>The Codex Desktop profile folder (CODEX_HOME) Trayvora reads chats from.</summary>
    public string Profile => _options.Profile;

    /// <summary>Creates a Codex Desktop chat in <paramref name="workspace"/> with its first message.</summary>
    public Task<(DesktopChat Chat, string Status, string Answer)> StartChatAsync(string workspace, string title, string text, CancellationToken ct, ReplyStream stream) =>
        _client.StartChatAsync(_options.Profile, workspace, title, text,
            new ReplyStream.Reporter(status => stream.Activity(status)), new ReplyStream.Reporter(stream.Append), ct);

    /// <summary>Sends to a specific Codex Desktop chat without changing the main-window selection.</summary>
    /// <remarks>Shows the reply while it is written and waits for it until the session is paused.</remarks>
    public Task<DesktopTurn?> SubmitToAsync(Guid chat, string text, CancellationToken ct, ReplyStream? stream = null, Guid? delivery = null) =>
        _client.SubmitAsync(_options.Profile, chat, text,
            new ReplyStream.Reporter(status => stream?.Activity(status)), ct,
            new ReplyStream.Reporter(partial => stream?.Replace(partial)), TimeSpan.FromMinutes(10), delivery);

    public Task<DesktopTurn?> CheckDeliveryAsync(string profile, Guid chat, Guid delivery, CancellationToken ct) =>
        _client.CheckDeliveryAsync(profile, chat, delivery, ct);

    public async Task<bool> SelectIdeChatAsync(Guid id)
    {
        if (TargetId == id) return true;
        var chat = _items.FirstOrDefault(item => item.Id == id);
        if (chat is null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            _items = await ListChatsAsync(timeout.Token);
            chat = _items.FirstOrDefault(item => item.Id == id);
        }
        if (chat is null || !TrySave(_options with { ChatId = id, ChatTitle = chat.Title, Workspace = chat.Workspace })) return false;
        FilterChats();
        ChangeTarget($"Active Desktop chat: {chat.Title}");
        return true;
    }

    public Task LoadOnceAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    private void FilterChats()
    {
        var query = _search.Text?.Trim() ?? "";
        var selected = _options.ChatId;
        var matches = _items.Where(chat => (chat.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || chat.Id.ToString().Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
        _changingChatList = true;
        try
        {
            _chats.ItemsSource = matches;
            _chats.SelectedItem = matches.FirstOrDefault(chat => chat.Id == selected);
        }
        finally { _changingChatList = false; }
        Refresh(_idle);
    }

    private async Task LoadAsync(bool more = false)
    {
        if (_loading || !_idle) return;
        _loading = true;
        _status.Text = "Loading Codex Desktop chats…";
        _load.Content = "Loading…";
        Refresh(_idle);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var path = (_profile.Text ?? "").Trim().Trim('"');
            more = more && string.Equals(path, _cursorProfile, StringComparison.OrdinalIgnoreCase) && _cursor is not null;
            var page = await _client.PageAsync(path, more ? _cursor : null, timeout.Token);
            IReadOnlyList<DesktopChat> chats = more ? _items.Concat(page.Chats).DistinctBy(chat => chat.Id).ToArray() : page.Chats;
            _cursor = page.Next == _cursor && more ? null : page.Next;
            _cursorProfile = path;
            _more.IsVisible = !string.IsNullOrEmpty(_cursor);
            var previous = _options;
            var sameProfile = string.Equals(Path.GetFullPath(path), (Path.IsPathFullyQualified(previous.Profile) ? Path.GetFullPath(previous.Profile) : previous.Profile), StringComparison.OrdinalIgnoreCase);
            var selected = sameProfile ? chats.FirstOrDefault(c => c.Id == previous.ChatId) : null;
            var options = new SharedChatOptions(path, sameProfile ? previous.ChatId : null,
                _auto.IsChecked == true, sameProfile ? selected?.Title ?? previous.ChatTitle : null, sameProfile ? selected?.Workspace ?? previous.Workspace : null);
            if (!TrySave(options)) return;
            _items = chats;
            _loaded = true;
            FilterChats();
            if (previous.ChatId != options.ChatId || previous.ChatTitle != options.ChatTitle)
                ChangeTarget(HasChat ? $"Active Desktop chat: {TargetTitle}" : "Trayvora chats are active.");
            _status.Text = chats.Count == 0
                ? "No saved Desktop chats found. Send the first message in Codex, then refresh."
                : $"{chats.Count} chats loaded. Select a chat to activate it.";
        }
        catch (Exception)
        {
            _status.Text = "Could not list Desktop chats. Check the Codex profile folder and installed CLI.";
        }
        finally
        {
            _loading = false;
            _load.Content = _loaded ? "Refresh chats" : "Find Desktop chats";
            Refresh(_idle);
        }
    }

    private async Task ReadAsync()
    {
        if (_options.ChatId is not { } id) return;
        _loading = true;
        Refresh(_idle);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var turns = await _client.ReadTurnsAsync(_options.Profile, id, timeout.Token);
            _reply.Text = string.Join("\n\n", turns.Reverse().Select(turn => $"You: {turn.Question}\nCodex: {turn.Answer}"));
            _status.Text = $"{turns.Count} recent turns.";
        }
        catch (Exception) { _status.Text = "Could not read this chat. Refresh the Desktop chat list."; }
        finally
        {
            _loading = false;
            Refresh(_idle);
        }
    }

    private bool TrySave(SharedChatOptions options)
    {
        try
        {
            SettingsFile.Write(_file, options);
            _options = options;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Could not save shared chat settings.";
            return false;
        }
    }

    private static SharedChatOptions ReadOptions(string file)
    {
        try
        {
            var saved = SettingsFile.Read<SharedChatOptions>(file);
            if (saved is not null) return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var desktop = Environment.GetEnvironmentVariable("VOICEAGENT_DESKTOP_CODEX_HOME");
        return new SharedChatOptions(string.IsNullOrWhiteSpace(desktop) ? local : desktop);
    }

    private void Open(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { _status.Text = "Could not open Codex Desktop."; }
    }

    private static StackPanel Row(params Control[] controls) => TrayvoraTheme.Row(controls);

}
