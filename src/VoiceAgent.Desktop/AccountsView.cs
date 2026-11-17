using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace VoiceAgent.Desktop;

internal sealed record AccountStatus(bool SignedIn, string Summary, bool Available = true);

/// <summary>
/// One sign-in provider (ChatGPT via Codex, Claude via Claude Code). The command-line tools keep their own
/// tokens in their profiles; Trayvora only starts the commands and shows their output.
/// </summary>
internal sealed record AccountProvider(
    string Name,
    string Description,
    Func<CancellationToken, Task<AccountStatus>> Status,
    Func<CliSession> SignIn,
    Func<CancellationToken, Task<int>> SignOut);

/// <summary>A running command whose output streams line by line and which can receive typed input (for example a code).</summary>
internal sealed class CliSession : IDisposable
{
    private readonly Process _process;
    public event Action<string>? Line;

    public CliSession(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (name, value) in environment) start.Environment[name] = value;
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => { if (args.Data is { } text) Line?.Invoke(Clean(text)); };
        _process.ErrorDataReceived += (_, args) => { if (args.Data is { } text) Line?.Invoke(Clean(text)); };
    }

    public void Start()
    {
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public async Task<int> WaitAsync(CancellationToken cancellationToken)
    {
        try { await _process.WaitForExitAsync(cancellationToken); }
        finally { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        return _process.ExitCode;
    }

    public void Send(string text)
    {
        if (_process.HasExited) return;
        _process.StandardInput.WriteLine(text);
        _process.StandardInput.Flush();
    }

    /// <summary>Runs a short command to completion and returns its exit code and output.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string executable, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        using var session = new CliSession(executable, arguments, environment);
        var output = new System.Text.StringBuilder();
        session.Line += line => { lock (output) output.AppendLine(line); };
        session.Start();
        session._process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var code = await session.WaitAsync(timeout.Token);
        lock (output) return (code, output.ToString().Trim());
    }

    /// <summary>Strips terminal colour codes.</summary>
    private static string Clean(string text) => AnsiCodes().Replace(text, "");

    private static readonly Regex AnsiPattern = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
    private static Regex AnsiCodes() => AnsiPattern;

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }
}

internal static class AccountProviders
{
    private static readonly string[] CodexAuth = ["-c", "cli_auth_credentials_store=\"file\"", "-c", "forced_login_method=\"chatgpt\""];

    /// <summary>ChatGPT sign-in for Trayvora's own Codex profile (kept in codex-data next to Trayvora.exe).</summary>
    public static AccountProvider ChatGpt() => new(
        "ChatGPT",
        "Used by Codex inside Trayvora. Signs in Trayvora's own Codex profile, stored next to Trayvora.exe.",
        async ct =>
        {
            if (CodexExe() is not { } codex) return new(false, "Codex was not found. Install the Codex app or Codex CLI, then restart Trayvora.", Available: false);
            var (code, output) = await CliSession.RunAsync(codex, [.. CodexAuth, "login", "status"], null, ct);
            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            return code == 0 ? new(true, first.Length > 0 ? first : "Signed in") : new(false, "Not signed in");
        },
        () => new CliSession(CodexExe() ?? throw new IOException("Codex was not found. Install the Codex app or Codex CLI."), [.. CodexAuth, "login"]),
        async ct => CodexExe() is { } codex ? (await CliSession.RunAsync(codex, [.. CodexAuth, "logout"], null, ct)).Code : 1);

    private static string? CodexExe() =>
        Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE") is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// <summary>Claude subscription sign-in for Claude Code (its own profile, the one Trayvora uses for Claude chats).</summary>
    public static AccountProvider Claude(Func<string> executable, Func<string> profile) => new(
        "Claude",
        "Used for Claude chats and sessions. Signs in Claude Code with your Claude subscription.",
        async ct =>
        {
            string exe;
            try { exe = ClaudeChats.FindExecutable(executable()); }
            catch (FileNotFoundException) { return new(false, "Claude Code was not found. Install Claude Desktop or the Claude Code CLI.", Available: false); }
            var (code, output) = await CliSession.RunAsync(exe, ["auth", "status", "--json"], ClaudeEnvironment(profile()), ct);
            return ParseClaudeStatus(code, output);
        },
        () => new CliSession(ClaudeChats.FindExecutable(executable()), ["auth", "login", "--claudeai"], ClaudeEnvironment(profile())),
        async ct => (await CliSession.RunAsync(ClaudeChats.FindExecutable(executable()), ["auth", "logout"], ClaudeEnvironment(profile()), ct)).Code);

    private static Dictionary<string, string> ClaudeEnvironment(string profile)
    {
        var environment = ClaudeChats.RealUserFolders().ToDictionary(item => item.Name, item => item.Value);
        if (Path.IsPathFullyQualified(profile)) environment["CLAUDE_CONFIG_DIR"] = profile;
        return environment;
    }

    /// <summary>Reads <c>claude auth status --json</c>: signed in, email, plan and expiry.</summary>
    internal static AccountStatus ParseClaudeStatus(int code, string output)
    {
        var start = output.IndexOf('{');
        if (start < 0) return new(false, code == 0 ? "Not signed in" : "Could not read the Claude sign-in status.");
        try
        {
            using var json = JsonDocument.Parse(output[start..]);
            var root = json.RootElement;
            var signedIn = root.TryGetProperty("loggedIn", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (!signedIn) return new(false, "Not signed in");
            var parts = new List<string>();
            if (root.TryGetProperty("email", out var email) && email.GetString() is { Length: > 0 } mail) parts.Add(mail);
            if (root.TryGetProperty("subscriptionType", out var plan) && plan.GetString() is { Length: > 0 } tier) parts.Add(char.ToUpperInvariant(tier[0]) + tier[1..]);
            if (root.TryGetProperty("loginExpiresAt", out var expires) && expires.TryGetDateTimeOffset(out var until))
                parts.Add(until < DateTimeOffset.Now ? "sign-in expired" : $"until {until.ToLocalTime():dd.MM.yyyy}");
            if (root.TryGetProperty("loginExpiresAt", out var expired) && expired.TryGetDateTimeOffset(out var end) && end < DateTimeOffset.Now)
                return new(false, string.Join(" · ", parts));
            return new(true, parts.Count > 0 ? string.Join(" · ", parts) : "Signed in");
        }
        catch (JsonException) { return new(false, "Could not read the Claude sign-in status."); }
    }
}

/// <summary>Accounts page: sign in to ChatGPT and Claude without typing commands.</summary>
internal sealed class AccountsView : ToolPage
{
    public event Action<string, AccountStatus>? StatusChanged;
    private readonly List<AccountCard> _cards = [];

    public AccountsView(IEnumerable<AccountProvider> providers)
    {
        var stack = new StackPanel { Spacing = 14 };
        foreach (var provider in providers)
        {
            var card = new AccountCard(provider, this);
            card.StatusChanged += status => StatusChanged?.Invoke(provider.Name, status);
            _cards.Add(card);
            stack.Children.Add(card.Root);
        }
        stack.Children.Add(TrayvoraTheme.Hint("Trayvora starts the official sign-in of each tool and shows its output. Your passwords and tokens are kept by Codex and Claude Code in their own profiles; Trayvora does not read or store them."));
        Content = Frame("Accounts", "Sign in once; Trayvora remembers it through the tools' own profiles.", stack);
        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    public Task RefreshAsync() => Task.WhenAll(_cards.Select(card => card.RefreshAsync()));

    protected override bool OnEscape() => _cards.Any(card => card.Cancel());

    public void Stop()
    {
        foreach (var card in _cards) card.Cancel();
    }

    private sealed class AccountCard
    {
        private static readonly Regex Url = new(@"https?://[^\s""'<>]+", RegexOptions.Compiled);
        private readonly AccountProvider _provider;
        private readonly Control _owner;
        private readonly Border _dot = TrayvoraTheme.Dot(TrayvoraTheme.Faint, 9);
        private readonly TextBlock _state = new() { FontWeight = FontWeight.SemiBold, FontSize = 13.5 };
        private readonly TextBlock _summary = new() { FontSize = 12, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap };
        private readonly Button _signIn = new() { Content = "Sign in" };
        private readonly Button _signOut = new() { Content = "Sign out" };
        private readonly Button _cancel = new() { Content = "Cancel", IsVisible = false };
        private readonly Button _open = new() { Content = "Open sign-in page", IsVisible = false };
        private readonly Button _copy = new() { Content = "Copy link", IsVisible = false };
        private readonly TextBox _code = new() { PlaceholderText = "If the browser shows a code, paste it here", MinWidth = 320 };
        private readonly Button _sendCode = new() { Content = "Send code" };
        private readonly StackPanel _codeRow;
        private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, FontSize = 12, IsVisible = false };
        private CancellationTokenSource? _run;
        private CliSession? _session;
        private string? _url;

        public event Action<AccountStatus>? StatusChanged;
        public Border Root { get; }

        public AccountCard(AccountProvider provider, Control owner)
        {
            _provider = provider;
            _owner = owner;
            TrayvoraTheme.Primary(_signIn);
            TrayvoraTheme.StyleControls(_signOut, _cancel, _open, _copy, _code, _sendCode, _log);
            TrayvoraTheme.Ghost(_cancel);
            TrayvoraTheme.Ghost(_signOut);
            Avalonia.Automation.AutomationProperties.SetName(_signIn, $"Sign in to {provider.Name}");
            Avalonia.Automation.AutomationProperties.SetName(_signOut, $"Sign out of {provider.Name}");
            _signIn.Click += async (_, _) => await SignInAsync();
            _signOut.Click += async (_, _) => await SignOutAsync();
            _cancel.Click += (_, _) => Cancel();
            _open.Click += (_, _) => OpenUrl();
            _copy.Click += async (_, _) =>
            {
                if (_url is not null && TopLevel.GetTopLevel(_owner)?.Clipboard is { } clipboard)
                    await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, _url);
            };
            _sendCode.Click += (_, _) => SendCode();
            _code.KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.Enter) { SendCode(); args.Handled = true; } };
            _codeRow = TrayvoraTheme.Row(_code, _sendCode);
            _codeRow.IsVisible = false;
            Root = TrayvoraTheme.Card(
                new TextBlock { Text = provider.Name, FontSize = 18, FontWeight = FontWeight.SemiBold },
                TrayvoraTheme.Hint(provider.Description),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _dot, _state } },
                _summary,
                TrayvoraTheme.Wrap(_signIn, _signOut, _cancel, _open, _copy),
                _codeRow,
                _log);
            Show(new(false, "Checking…"), checking: true);
        }

        private void Show(AccountStatus status, bool checking = false)
        {
            _dot.Background = checking ? TrayvoraTheme.Faint : status.SignedIn ? TrayvoraTheme.Success : status.Available ? TrayvoraTheme.Warning : TrayvoraTheme.Danger;
            _state.Text = checking ? "Checking…" : status.SignedIn ? "Signed in" : status.Available ? "Not signed in" : "Not available";
            _summary.Text = checking || status.Summary is "Not signed in" ? "" : status.Summary;
            _summary.IsVisible = _summary.Text.Length > 0;
            var idle = _run is null;
            _signIn.Content = status.SignedIn ? "Sign in again" : "Sign in";
            _signIn.IsEnabled = idle && status.Available && !checking;
            _signOut.IsVisible = status.SignedIn;
            _signOut.IsEnabled = idle;
            _cancel.IsVisible = !idle;
            if (!checking) StatusChanged?.Invoke(status);
        }

        public async Task RefreshAsync()
        {
            if (_run is not null) return;
            Show(new(false, "Checking…"), checking: true);
            AccountStatus status;
            try { status = await _provider.Status(CancellationToken.None); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
            {
                status = new(false, "Could not check the sign-in status.");
            }
            Show(status);
        }

        private void Log(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            _log.IsVisible = true;
            _log.Text = (_log.Text ?? "") + line + Environment.NewLine;
            _log.CaretIndex = _log.Text.Length;
            if (_url is null && Url.Match(line) is { Success: true } found)
            {
                _url = found.Value.TrimEnd('.', ',', ')');
                _open.IsVisible = _copy.IsVisible = true;
            }
        }

        private async Task SignInAsync()
        {
            if (_run is not null) return;
            using var run = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            _run = run;
            _url = null;
            _log.Text = "";
            _open.IsVisible = _copy.IsVisible = false;
            _codeRow.IsVisible = true;
            _code.Text = "";
            Show(new(false, "Waiting for the browser sign-in…"), checking: true);
            _state.Text = "Signing in… finish in your browser";
            try
            {
                using var session = _provider.SignIn();
                _session = session;
                session.Line += line => Dispatcher.UIThread.Post(() => Log(line));
                session.Start();
                var code = await session.WaitAsync(run.Token);
                await Dispatcher.UIThread.InvokeAsync(() => { });
                if (code != 0) Log($"Sign-in ended with code {code}.");
            }
            catch (OperationCanceledException) { Log("Sign-in cancelled."); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
            {
                Log(error.Message);
            }
            finally
            {
                _session = null;
                _run = null;
                _codeRow.IsVisible = false;
                _open.IsVisible = _copy.IsVisible = false;
            }
            await RefreshAsync();
        }

        private async Task SignOutAsync()
        {
            if (_run is not null) return;
            try { await _provider.SignOut(CancellationToken.None); }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or FileNotFoundException or OperationCanceledException)
            {
                Log(error.Message);
            }
            await RefreshAsync();
        }

        private void SendCode()
        {
            var code = _code.Text?.Trim();
            if (string.IsNullOrEmpty(code) || _session is null) return;
            _session.Send(code);
            _code.Text = "";
            Log("Code sent.");
        }

        private void OpenUrl()
        {
            if (_url is null) return;
            try { Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Log("Could not open the browser. Copy the link instead."); }
        }

        public bool Cancel()
        {
            if (_run is null) return false;
            _run.Cancel();
            return true;
        }
    }
}
