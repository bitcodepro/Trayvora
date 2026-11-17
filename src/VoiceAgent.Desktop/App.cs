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
        // A failure in one UI handler must not close the tray app: log it and keep running.
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.Handled = true;
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
        string Instructions() => $"Preview {voiceKeys.Preview}  ·  Background {voiceKeys.Background}  ·  Send {voiceKeys.Send}  ·  Dictate {voiceKeys.DictationShortcut}  ·  Shared chat {voiceKeys.SharedShortcut}  ·  Long questions {voiceKeys.LongShortcut}  ·  Short questions {voiceKeys.ShortShortcut}  ·  Windows audio {voiceKeys.LoopbackShortcut}  ·  Stop reading {voiceKeys.StopSpeechShortcut}";
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
            ("Quick question (GPT, last six exchanges)", voiceKeys.ShortShortcut.ToString()), ("Record computer sound and send", voiceKeys.LoopbackShortcut.ToString()), ("Stop reading aloud", voiceKeys.StopSpeechShortcut.ToString()),
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
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        _ = overlay.CheckAccountsAsync();
        var pendingChats = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        pendingChats.Tick += (_, _) => { if (!exiting && OperatingSystem.IsWindows()) overlay.ApplyPendingCodexChats(); };
        pendingChats.Start();
        if (OperatingSystem.IsWindows()) overlay.ApplyPendingCodexChats();
        // A minute after start and then twice a day: look for a newer release on GitHub and offer it in the tray menu.
        NativeMenuItem? updateItem = null;
        var updates = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        updates.Tick += async (_, _) =>
        {
            updates.Interval = TimeSpan.FromHours(12);
            if (exiting) return;
            AvailableUpdate? found;
            try { found = await GitHubUpdate.CheckAsync(); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { return; }
            if (found is null || exiting) return;
            overlay.OfferUpdate(found);
            tray.ToolTipText = $"Trayvora · update {found.Version} available";
            if (updateItem is null)
            {
                updateItem = new NativeMenuItem();
                updateItem.Click += (_, _) => { if (!exiting) overlay.ShowReadiness(); };
                menu.Items.Insert(0, updateItem);
                menu.Items.Insert(1, new NativeMenuItemSeparator());
            }
            updateItem.Header = $"Update to Trayvora {found.Version}…";
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
