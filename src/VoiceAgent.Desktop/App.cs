using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using VoiceAgent.Application;
using VoiceAgent.Codex;
using VoiceAgent.Speech;
using VoiceAgent.Infrastructure;

namespace VoiceAgent.Desktop;

internal sealed class App : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        TrayvoraTheme.AddStyles(Styles);
        Appearance.Load(Environment.GetEnvironmentVariable("VOICEAGENT_SETTINGS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "settings"));
        Appearance.Install(Styles);
        KeyPhrases.Load(Environment.GetEnvironmentVariable("VOICEAGENT_SETTINGS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "settings"));
        // A failure in one UI handler must not close the tray app: log it and keep running.
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            ErrorLog.Write(args.Exception, "Interface");
            args.Handled = true;
        };
        // Failures in background work, and a crash, are recorded too.
        TaskScheduler.UnobservedTaskException += (_, args) => ErrorLog.Write(args.Exception, "Background task");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) ErrorLog.Write(error, "Crash");
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            throw new InvalidOperationException("The desktop application lifetime is required.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Trayvora requires Windows.");
        }

        var modelFolder = Environment.GetEnvironmentVariable("VOICEAGENT_MODEL_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "models");
        var modelPath = Path.GetFullPath(Path.Combine(modelFolder, "ggml-base.bin"));
        var settingsFolder = Environment.GetEnvironmentVariable("VOICEAGENT_SETTINGS_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "settings");
        UiLanguage.Start(settingsFolder);
        var settings = new HotkeySettings(settingsFolder);
        var codex = new CodexSession(Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "codex-data"));
        var (shortcut, settingsWarning) = settings.Load();
        var speech = new SpeechSettings(settingsFolder, modelFolder);
        var hotkeyFile = Path.Combine(settingsFolder, "voice-hotkeys.json");
        var voiceKeys = VoiceHotkeys.Default(shortcut);
        try { voiceKeys = SettingsFile.Read<VoiceHotkeys>(hotkeyFile) ?? voiceKeys; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        var hotkeys = new VoiceHotkeyBindings(
            key => new WindowsGlobalHotkey((uint)key.Modifiers | 0x4000, key.Key),
            value => SettingsFile.Write(hotkeyFile, value));
        var hotkeyWarning = hotkeys.Apply(voiceKeys, persist: false);
        var services = new ServiceCollection()
            .AddSingleton(_ => new RequestHistory(Environment.GetEnvironmentVariable("VOICEAGENT_HISTORY_DIR")
                ?? Path.Combine(AppContext.BaseDirectory, "history")))
            .AddSingleton<IAudioRecorder, WasapiRecorder>()
            .AddSingleton(new WhisperModel(modelPath))
            .AddSingleton<ISpeechTranscriber, WhisperTranscriber>()
            .BuildServiceProvider();
        string Instructions() => $"Preview {voiceKeys.Preview}  ·  Background {voiceKeys.Background}  ·  Send {voiceKeys.Send}  ·  Dictate {voiceKeys.DictationShortcut}  ·  Shared chat {voiceKeys.SharedShortcut}  ·  Long questions {voiceKeys.LongShortcut}  ·  Short questions {voiceKeys.ShortShortcut}  ·  Windows audio {voiceKeys.LoopbackShortcut}  ·  Stop reading {voiceKeys.StopSpeechShortcut}  ·  Dictate anywhere {voiceKeys.AnywhereShortcut}";
        var message = string.Join(Environment.NewLine, new[] { Instructions(), settingsWarning, hotkeyWarning }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        var overlay = new VoiceOverlay(services.GetRequiredService<IAudioRecorder>(),
            services.GetRequiredService<ISpeechTranscriber>(), services.GetRequiredService<WhisperModel>(),
            codex, services.GetRequiredService<RequestHistory>(), message, speech, settingsFolder);
        overlay.PreviewSendShortcut = voiceKeys.Send.ToString();
        FloatingAgent? floating = null;
        SettingsWindow? settingsWindow = null;
        WorkspaceWindow? workspaceWindow = null;

        var exiting = false;
        var windows = new WindowsDesktopEvents(overlay);
        windows.Suspending += (_, _) => Dispatcher.UIThread.Post(overlay.OnSystemSuspend);
        windows.Resumed += (_, _) => Dispatcher.UIThread.Post(async () => await overlay.OnSystemResumeAsync());
        overlay.RequestEnded += status =>
        {
            if (!exiting && status != RequestStatus.Cancelled && !overlay.IsActive)
            {
                windows.Notify(status == RequestStatus.Completed);
            }
        };
        overlay.Closing += (_, args) =>
        {
            if (!exiting && args.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
            {
                args.Cancel = true;
                overlay.Hide();
            }
        };

        async Task ShowOverlayAsync(bool toggleRecording)
        {
            if (exiting)
            {
                return;
            }

            var wasVisible = overlay.IsVisible;
            overlay.Show();
            overlay.Activate();
            if (toggleRecording)
            {
                await overlay.ToggleRecordingAsync();
            }
            else if (!wasVisible)
            {
                await overlay.RefreshDevicesAsync();
            }
        }

        async void HotkeyPressed(VoiceAction action)
        {
            if (exiting) return;
            ActivityLog.Shared.Add(ActivityLog.PressKind, $"{VoiceHotkeys.Describe(action)} pressed");
            switch (action)
            {
                case VoiceAction.Background:
                    await overlay.ToggleRecordingAsync(VoiceShortcutMode.Background);
                    break;
                case VoiceAction.Preview:
                    await overlay.ToggleRecordingAsync(VoiceShortcutMode.Preview);
                    break;
                case VoiceAction.SharedChat:
                    await overlay.ToggleRecordingAsync(VoiceShortcutMode.SharedChat);
                    break;
                case VoiceAction.Dictate:
                    await overlay.ToggleRecordingAsync(VoiceShortcutMode.Dictation);
                    break;
                case VoiceAction.LongQuestions:
                    await overlay.ToggleQuestionsAsync(longMemory: true);
                    break;
                case VoiceAction.ShortQuestions:
                    await overlay.ToggleQuestionsAsync(longMemory: false);
                    break;
                case VoiceAction.WindowsAudio:
                    await overlay.ToggleRecordingAsync(VoiceShortcutMode.WindowsAudio);
                    break;
                case VoiceAction.Anywhere:
                    await overlay.ToggleRecordingAsync(VoiceShortcutMode.Anywhere);
                    break;
                case VoiceAction.StopSpeech:
                    overlay.StopSpeech();
                    break;
                case VoiceAction.Send:
                    await overlay.SendPreviewAsync();
                    break;
            }
        }

        hotkeys.Pressed += HotkeyPressed;
        var open = new NativeMenuItem("Open Trayvora");
        open.Click += async (_, _) => await ShowOverlayAsync(toggleRecording: false);
        var record = new NativeMenuItem("Record / Stop");
        record.Click += async (_, _) => await ShowOverlayAsync(toggleRecording: true);
        var exit = new NativeMenuItem("Exit");
        async Task ExitAsync()
        {
            exiting = true;
            await overlay.CancelAndWaitAsync();
            desktop.Shutdown();
        }
        exit.Click += async (_, _) => await ExitAsync();
        overlay.ExitRequested += () => Dispatcher.UIThread.Post(async () => await ExitAsync());
        var sessions = new NativeMenuItem("Sessions");
        sessions.Click += (_, _) => { if (!exiting) overlay.ShowSessions(); };
        var menu = new NativeMenu();
        var more = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(sessions);
        menu.Items.Add(record);
        using var iconStream = typeof(App).Assembly.GetManifestResourceStream("VoiceAgent.Desktop.Assets.trayvora.ico")
            ?? throw new InvalidOperationException("Missing tray icon.");
        var tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = hotkeyWarning is null ? "Trayvora" : "Trayvora (check shortcuts in Settings)",
            IsVisible = true,
            Menu = menu,
        };
        var preferences = new NativeMenuItem("Settings…");
        IReadOnlyList<(string, string)> ShortcutList() =>
        [
            ("Record with preview", voiceKeys.Preview.ToString()), ("Record and send at once", voiceKeys.Background.ToString()),
            ("Send preview", voiceKeys.Send.ToString()), ("Dictate into Codex Desktop", voiceKeys.DictationShortcut.ToString()),
            ("Send to Codex Desktop chat", voiceKeys.SharedShortcut.ToString()), ("Deep question (GPT, long memory)", voiceKeys.LongShortcut.ToString()),
            ("Quick question (GPT, last six exchanges)", voiceKeys.ShortShortcut.ToString()), ("Computer sound → ask OpenAI, answer aloud", voiceKeys.LoopbackShortcut.ToString()), ("Stop reading aloud", voiceKeys.StopSpeechShortcut.ToString()), ("Dictate anywhere", voiceKeys.AnywhereShortcut.ToString()),
        ];
        overlay.SetShortcuts(ShortcutList());
        EventHandler openSettings = (_, _) =>
        {
            if (!OperatingSystem.IsWindows()) return;
            if (settingsWindow is null)
            {
                settingsWindow = new SettingsWindow(voiceKeys, selected =>
                {
                    var warning = hotkeys.Apply(selected, persist: true);
                    if (warning is null)
                    {
                        voiceKeys = selected;
                        overlay.SetHotkeyMessage(Instructions());
                        overlay.SetShortcuts(ShortcutList());
                        overlay.PreviewSendShortcut = selected.Send.ToString();
                        hotkeyWarning = null;
                        tray.ToolTipText = "Trayvora";
                    }

                    return warning;
                }, codex, speech, overlay.SpeechSettingsChanged, new DiscussionSettings(settingsFolder), settingsFolder);
                settingsWindow.Closed += (_, _) => settingsWindow = null;
            }

            settingsWindow.Show();
            settingsWindow.Activate();
        };
        preferences.Click += openSettings;
        overlay.SettingsRequested += () => openSettings(null, EventArgs.Empty);
        var recent = new NativeMenuItem("History…");
        recent.Click += async (_, _) => await overlay.ShowHistoryAsync();
        var workspace = new NativeMenuItem("Local workspace…");
        workspace.Click += (_, _) =>
        {
            if (workspaceWindow is null)
            {
                workspaceWindow = new WorkspaceWindow(overlay.LocalWorkspace, selected => overlay.LocalWorkspace = selected);
                workspaceWindow.Closed += (_, _) => workspaceWindow = null;
            }

            workspaceWindow.Show();
            workspaceWindow.Activate();
        };
        // Shown only while something is running.
        var emergency = new NativeMenuItem("Stop everything") { IsVisible = false };
        emergency.Click += (_, _) => overlay.EmergencyStop();
        more.Items.Add(recent);
        more.Items.Add(workspace);
        var floatingItem = new NativeMenuItem("Show floating agent");
        floatingItem.Click += (_, _) =>
        {
            if (floating?.IsVisible == true)
            {
                floating.Hide();
                floatingItem.Header = "Show floating agent";
                return;
            }

            if (floating is null)
            {
                floating = new FloatingAgent(overlay.ToggleFloatingRecordingAsync);
                floating.Closed += (_, _) =>
                {
                    floating = null;
                    floatingItem.Header = "Show floating agent";
                };
            }

            floating.SetState(overlay.CurrentAgentState);
            floating.Show();
            floatingItem.Header = "Hide floating agent";
        };
        overlay.AgentStateChanged += state =>
        {
            floating?.SetState(state);
            emergency.IsVisible = state != AgentState.Ready;
            tray.ToolTipText = state switch
            {
                AgentState.Recording => "Trayvora (recording)",
                AgentState.Transcribing => "Trayvora (transcribing)",
                AgentState.Sending => "Trayvora (sending)",
                AgentState.Busy => "Trayvora (busy)",
                _ => hotkeyWarning is null ? "Trayvora" : "Trayvora (check shortcuts in Settings)",
            };
        };
        more.Items.Add(floatingItem);
        more.Items.Add(new NativeMenuItemSeparator());
        var signIn = new NativeMenuItem("Accounts (ChatGPT, Claude)…");
        signIn.Click += (_, _) => { if (!exiting) overlay.ShowAccounts(); };
        more.Items.Add(signIn);
        if (Environment.ProcessPath is { } exePath)
            more.Items.Add(StartupRegistration.CreateMenuItem(exePath, message => tray.ToolTipText = message));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(preferences);
        menu.Items.Add(new NativeMenuItem("More") { Menu = more });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(emergency);
        menu.Items.Add(exit);
        tray.Clicked += async (_, _) => await ShowOverlayAsync(toggleRecording: false);
        overlay.QuietNotice += text => tray.ToolTipText = "Trayvora · " + text;
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        _ = overlay.CheckAccountsAsync();
        var pendingChats = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        pendingChats.Tick += (_, _) => { if (!exiting && OperatingSystem.IsWindows()) overlay.ApplyPendingCodexChats(); };
        pendingChats.Start();
        if (OperatingSystem.IsWindows()) overlay.ApplyPendingCodexChats();
        // Tray → "Check for updates": checks GitHub; when a newer release exists the same item installs it.
        // Also checked a minute after start and then twice a day.
        var updateItem = new NativeMenuItem("Check for updates");
        AvailableUpdate? offered = null;
        var updateBusy = false;
        void Offer(AvailableUpdate found)
        {
            offered = found;
            overlay.OfferUpdate(found);
            updateItem.Header = $"Install update {found.Version}";
            tray.ToolTipText = $"Trayvora · update {found.Version} available";
        }
        async Task CheckUpdatesAsync(bool quiet)
        {
            if (updateBusy || exiting) return;
            updateBusy = true;
            if (!quiet) updateItem.Header = "Checking for updates…";
            try
            {
                if (await GitHubUpdate.CheckAsync() is { } found) Offer(found);
                else
                {
                    // Nothing newer (or the offered release was withdrawn): back to a plain check.
                    offered = null;
                    updateItem.Header = "Check for updates";
                    if (quiet) return;
                    var current = GitHubUpdate.CurrentVersion();
                    updateItem.Header = current is null ? "Check for updates · up to date" : $"Check for updates · {current} is the latest";
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                if (!quiet) updateItem.Header = "Check for updates · could not reach GitHub";
            }
            finally { updateBusy = false; }
        }
        updateItem.Click += async (_, _) =>
        {
            if (exiting || updateBusy) return;
            if (offered is not { } chosen)
            {
                await CheckUpdatesAsync(quiet: false);
                return;
            }
            updateBusy = true;
            updateItem.Header = $"Installing {chosen.Version}…";
            var message = await overlay.InstallUpdateFromTrayAsync(chosen, new Progress<string>(text => tray.ToolTipText = "Trayvora · " + text));
            updateBusy = false;
            if (message is not null && !message.StartsWith("Updating", StringComparison.Ordinal))
            {
                // Forget the cached offer so the next click checks GitHub again instead of retrying the same download.
                offered = null;
                updateItem.Header = "Check for updates · last install failed";
                tray.ToolTipText = "Trayvora · " + message;
            }
        };
        menu.Items.Insert(menu.Items.IndexOf(exit), updateItem);
        var updates = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        updates.Tick += async (_, _) =>
        {
            updates.Interval = TimeSpan.FromHours(12);
            await CheckUpdatesAsync(quiet: true);
        };
        if (OperatingSystem.IsWindows()) updates.Start();
        // For checking the built-in terminal: TRAYVORA_OPEN_TERMINAL=powershell|claude|codex opens it at startup.
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("TRAYVORA_OPEN_TERMINAL") is { Length: > 0 } openTerminal
            && Enum.TryParse<TerminalKind>(openTerminal, ignoreCase: true, out var terminalKind))
            Dispatcher.UIThread.Post(() => { if (OperatingSystem.IsWindows()) overlay.ShowTerminal(terminalKind); }, DispatcherPriority.Background);
        var volumeWheel = new WindowsVolumeWheel(steps => { if (!exiting) overlay.AdjustPlaybackVolume(steps); });
        if (!volumeWheel.Registered) tray.ToolTipText += " · Alt+wheel unavailable";

        desktop.ShutdownRequested += (_, _) =>
        {
            exiting = true;
            overlay.Cancel();
        };
        desktop.Exit += (_, _) =>
        {
            exiting = true;
            overlay.Cancel();
            volumeWheel.Dispose();
            overlay.DisposeSharedChat();
            tray.IsVisible = false;
            tray.Dispose();
            windows.Dispose();
            hotkeys.Pressed -= HotkeyPressed;
            hotkeys.Dispose();
            services.Dispose();
        };
        base.OnFrameworkInitializationCompleted();
    }
}
