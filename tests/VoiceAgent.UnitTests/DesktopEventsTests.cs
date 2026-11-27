using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using VoiceAgent.Application;
using VoiceAgent.Codex;
using VoiceAgent.Infrastructure;
using VoiceAgent.Speech;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class DesktopEventsTests
{
    [Fact]
    public async Task HiddenWindowReceivesPowerMessagesAndWindowsAcceptsNotification()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<Avalonia.Application>().UsePlatformDetect().SetupWithoutStarting();
                var window = new Window();
                using (var events = new WindowsDesktopEvents(window))
                {
                    Assert.True(events.Registered);
                    Assert.False(window.IsVisible);
                    var changes = new List<string>();
                    events.Suspending += (_, _) => changes.Add("suspend");
                    events.Resumed += (_, _) => changes.Add("resume");
                    var handle = window.TryGetPlatformHandle()!.Handle;
                    SendMessage(handle, 0x0218, 4, 0);
                    SendMessage(handle, 0x0218, 18, 0);
                    SendMessage(handle, 0x0218, 7, 0);
                    Assert.Equal(new[] { "suspend", "resume" }, changes);
                    Assert.True(events.Notify(succeeded: true));
                }
                window.Close();
                VerifyOverlay();
                VerifySessions();
                VerifyCommandMode();
                VerifyTerminalReplacement();
                VerifyLongSession();
                VerifyDiscussWith();
                VerifyStreamingSession();
                VerifyMarkdown();
                VerifyAttachments();
                VerifyDesktopDelivery();
                VerifyAudioEngineSwitch();
                VerifyAccounts();
                VerifyFloatingAgent();
                VerifyDictationDraft();
                VerifyUpdateDialog();
                VerifyVoiceRecipient();
                result.SetResult();
            }
            catch (Exception error)
            {
                result.SetException(error);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void VerifyOverlay()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-overlay-");
        var modelPath = Path.Combine(folder.FullName, "model");
        File.WriteAllBytes(modelPath, []);
        try
        {
            using var history = new RequestHistory(folder.FullName);
            var overlay = new VoiceOverlay(new TestRecorder(), new TestTranscriber(), new WhisperModel(modelPath),
                new CodexSession(folder.FullName), history, "Test shortcut");
            Assert.False(overlay.IsVisible);
            overlay.Show();
            using var toolInput = System.Text.Json.JsonDocument.Parse("""{"query":"test"}""");
            var chat = new ClaudeChat(Guid.NewGuid(), folder.FullName, "Test chat");
            foreach (var allow in new[] { false, true })
            {
                var result = overlay.ConfirmClaudeToolAsync(chat, "mcp__example__lookup", toolInput.RootElement, default);
                PumpUntil(() => overlay.OwnedWindows.Any(w => w.Title == "Claude Code · MCP permission"));
                var prompt = overlay.OwnedWindows.Single(w => w.Title == "Claude Code · MCP permission");
                var controls = prompt.GetLogicalDescendants().ToArray();
                Assert.Equal(toolInput.RootElement.GetRawText(), controls.OfType<TextBox>().Single().Text);
                controls.OfType<Button>().Single(b => Equals(b.Content, allow ? "Allow once" : "Deny"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(result);
                Assert.Equal(allow, result.Result);
            }
            Pump(overlay.RefreshDevicesAsync());
            var recording = overlay.ToggleRecordingAsync();
            Pump(overlay.ToggleRecordingAsync());
            Pump(recording);

            var buttons = overlay.GetLogicalDescendants().OfType<Button>().ToArray();
            var download = buttons.Single(button => Equals(button.Content, "Download selected Whisper model"));
            Assert.False(download.IsVisible);
            var transcript = overlay.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Type a message or edit your transcript");
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, transcript.VerticalContentAlignment);
            Assert.Equal("Test transcript", transcript.Text);
            transcript.Text = "Edited transcript";
            Assert.Equal("Edited transcript", transcript.Text);
            var send = buttons.Single(button => Equals(button.Content, "Send to Codex"));
            Pump(Task.Delay(50));
            Assert.True(send.IsEnabled);

            var suspended = overlay.ToggleRecordingAsync();
            overlay.OnSystemSuspend();
            Pump(suspended);
            Assert.False(send.IsEnabled);
            Pump(overlay.OnSystemResumeAsync());
            Assert.True(send.IsEnabled);
            Assert.Equal("Edited transcript", transcript.Text);
            overlay.RaiseEvent(new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent });
            Assert.False(overlay.IsVisible);
            var states = new List<AgentState>();
            overlay.AgentStateChanged += state => states.Add(state);
            var preview = overlay.ToggleRecordingAsync(VoiceShortcutMode.Preview);
            Assert.Equal(AgentState.Recording, overlay.CurrentAgentState);
            Pump(overlay.ToggleRecordingAsync(VoiceShortcutMode.Preview));
            Pump(preview);
            Assert.True(overlay.HasDraftPreview);
            Assert.Contains(AgentState.Transcribing, states);
            Assert.Equal(AgentState.Ready, overlay.CurrentAgentState);
            Assert.False(overlay.IsVisible);

            // Session dictation records until stopped and returns the transcript without touching the main window draft.
            using (var stopDictation = new CancellationTokenSource())
            {
                var dictation = overlay.DictateAsync(stopDictation.Token, CancellationToken.None, new Progress<double>());
                Assert.NotEqual(AgentState.Ready, overlay.CurrentAgentState);
                var busy = overlay.DictateAsync(CancellationToken.None, CancellationToken.None, new Progress<double>());
                Assert.True(busy.IsFaulted);
                Assert.IsType<IOException>(busy.Exception!.InnerException);
                stopDictation.Cancel();
                Pump(dictation);
                Assert.Equal("Test transcript", dictation.Result);
                Assert.Equal(AgentState.Ready, overlay.CurrentAgentState);
            }
            using (var cancelDictation = new CancellationTokenSource())
            {
                var dictation = overlay.DictateAsync(CancellationToken.None, cancelDictation.Token, new Progress<double>());
                cancelDictation.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => Pump(dictation));
                Assert.Equal(AgentState.Ready, overlay.CurrentAgentState);
            }

            overlay.OnSystemSuspend();
            overlay.Close();
        }
        finally
        {
            File.Delete(modelPath);
            File.Delete(Path.Combine(folder.FullName, "history.db"));
            File.Delete(Path.Combine(folder.FullName, "history.lock"));
            folder.Delete();
        }
    }

    private static void VerifyVoiceRecipient()
    {
        var tmp = Directory.CreateTempSubdirectory();
        var window = new Window { Width = 960, Height = 640 };
        try
        {
            var store = new DiscussionStore(tmp.FullName);
            var first = store.Create(null);
            first.Title = "Original room";
            var second = store.Create(null);
            second.Title = "Second room";
            first.Updated = second.Updated.AddSeconds(1);
            store.Save(first);
            store.Save(second);
            var voice = new TaskCompletionSource<string?>();
            var sent = 0;
            var view = new DiscussionWindow(store, (_, _, _, _, _, _) => { sent++; return Task.FromResult<string?>("ok"); },
                () => [], null, () => Task.FromResult<IReadOnlyList<ClaudeChat>>([]), () => null, (_, _, _) => voice.Task);
            view.Initialize();
            window.Content = view;
            window.Show();
            PumpUntil(() => view.CurrentRoom?.Id == first.Id);
            var mic = view.GetLogicalDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Dictate a message");
            view.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(button => Equals(button.Content, "Voice mode")).IsChecked = true;
            mic.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForButton(view, second.Title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => view.CurrentRoom?.Id == second.Id);
            voice.SetResult("original voice note");
            Pump(Task.Delay(20));
            Assert.Equal(0, sent);
            WaitForButton(view, first.Title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => view.CurrentRoom?.Id == first.Id);
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBox>(), box => box.Text == "original voice note");
        }
        finally { window.Close(); tmp.Delete(true); }
    }

    private static void VerifyUpdateDialog()
    {
        CancellationToken downloadToken = default;
        var dialog = new UpdateWindow(_ => Task.FromResult<AvailableUpdate?>(new(new Version(99, 0), "", "", "")),
            async (_, progress, token) =>
            {
                downloadToken = token;
                progress("Downloading · 42%", 0.42);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        try
        {
            dialog.Show();
            Button Action(string label) => dialog.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
            PumpUntil(() => Action("Update").IsEnabled);
            Action("Update").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0.42, dialog.GetLogicalDescendants().OfType<ProgressBar>().Single().Value);
            Assert.False(Action("Update").IsEnabled);
            Action("Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(downloadToken.IsCancellationRequested);
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }

    private static void VerifyDictationDraft()
    {
        var sent = false;
        var preview = new TranscriptPreview(() => sent = true);
        try
        {
            preview.ShowDictationDraft("A draft for the open chat");
            Assert.True(preview.HasDraft);
            Assert.Null(preview.TakeDraft());
            Assert.False(sent);
        }
        finally
        {
            preview.Close();
        }
    }

    private static void VerifyCommandMode()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-commands-");
        var window = new Window { Width = 900, Height = 650 };
        try
        {
            var store = new DiscussionStore(folder.FullName);
            var chat = new ClaudeChat(Guid.NewGuid(), folder.FullName, "Review project");
            var calls = new List<string>();
            var sent = new List<string>();
            var firstProfile = Path.Combine(folder.FullName, "profile-one");
            var secondProfile = Path.Combine(folder.FullName, "profile-two");
            var activeProfile = firstProfile + Path.DirectorySeparatorChar;
            var consoles = new List<TerminalContext>();
            var reply = new TaskCompletionSource<(int, string)>();
            var legacy = store.Create(null);
            legacy.Messages.Add(new(DiscussionMember.Claude, DiscussionMember.Boss, "Legacy MCP status",
                DateTimeOffset.Now, Workspace: folder.FullName, Command: "/mcp"));
            store.Save(legacy);
            var view = new DiscussionWindow(store, (_, text, _, _, _, _) => { sent.Add(text); return Task.FromResult<string?>("OK"); },
                () => [], null, () => Task.FromResult<IReadOnlyList<ClaudeChat>>([chat]), () => null,
                (_, _, _) => Task.FromResult<string?>(null), openTerminal: consoles.Add);
            view.CurrentClaudeProfile = () => activeProfile;
            view.RunClaudeCommand = (args, context, ct) =>
            {
                Assert.Equal(new TerminalContext(folder.FullName, firstProfile), context);
                calls.Add(string.Join(" ", args));
                return reply.Task;
            };
            view.Initialize();
            window.Content = view;
            window.Show();
            PumpUntil(() => view.IsEnabled);
            Assert.True(view.IsEnabled);
            var controls = view.GetLogicalDescendants().ToArray();
            var mode = controls.OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Single(b => AutomationProperties.GetName(b) == "Claude command mode");
            var line = controls.OfType<TextBox>().Single(b => b.PlaceholderText == "Type / for Claude CLI commands");
            var editor = controls.OfType<TextBox>().Single(b => b.PlaceholderText?.StartsWith("Message Codex") == true);
            var run = controls.OfType<Button>().Single(b => Equals(b.Content, "Run command"));
            var list = controls.OfType<ListBox>().Single(b => AutomationProperties.GetName(b) == "Claude command suggestions");
            var send = controls.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Send");
            var voice = controls.OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Single(b => Equals(b.Content, "Voice mode"));
            var switches = Assert.IsType<StackPanel>(mode.Parent);
            Assert.Equal<Control>([voice, mode], switches.Children);
            Assert.True(mode.IsEffectivelyVisible);
            Assert.True(voice.IsEffectivelyVisible);

            editor.Text = "/version";
            mode.IsChecked = true;
            Assert.True(mode.IsEffectivelyVisible);
            Assert.True(voice.IsEffectivelyVisible);
            Assert.False(voice.IsEnabled);
            Assert.True(line.IsEffectivelyVisible);
            Assert.False(editor.IsEffectivelyVisible);
            var console = controls.OfType<Button>().Single(b => Equals(b.Content, "Claude console"));
            Assert.True(console.IsEffectivelyVisible);
            console.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([new TerminalContext(AppContext.BaseDirectory, firstProfile)], consoles);
            void AssertNeedsFreshMcp() => Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(),
                block => block.Text == ClaudeCommands.RefreshMcp);
            var legacyManage = view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Manage MCP in console"));
            legacyManage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AssertNeedsFreshMcp();
            Assert.Single(consoles);
            Assert.Empty(sent);
            line.Text = "/res";
            PumpUntil(() => list.Items.OfType<CommandChoice>().SingleOrDefault()?.Command == "/resume");
            void Run(string command)
            {
                line.Text = command;
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => !view.IsRunning);
                Pump(view.PendingSaves);
            }
            void Press(Key key) => line.RaiseEvent(new KeyEventArgs { Key = key, RoutedEvent = InputElement.KeyDownEvent });
            void ReleaseEnter() => window.RaiseEvent(new KeyEventArgs { Key = Key.Enter, RoutedEvent = InputElement.KeyUpEvent });
            void Enter() { Press(Key.Enter); ReleaseEnter(); }
            Press(Key.Down);
            Assert.Equal(0, list.SelectedIndex);
            Press(Key.Enter);
            Assert.Equal("/resume", line.Text);
            Assert.Equal(line.Text.Length, line.CaretIndex);
            Assert.Single(store.ReadRoom(view.CurrentRoom!.Id).Messages);

            Press(Key.Enter);
            Assert.Single(store.ReadRoom(view.CurrentRoom!.Id).Messages);
            ReleaseEnter();
            Enter();
            PumpUntil(() => !view.IsRunning);
            Pump(view.PendingSaves);
            Assert.Contains(list.Items.OfType<CommandChoice>(), item => item.Description == chat.Title);
            Run($"/resume {chat.Id}");
            Run("/effort");
            Assert.Equal("/effort ", line.Text);
            Press(Key.Down);
            Press(Key.Down);
            Press(Key.Down);
            Press(Key.Down);
            Press(Key.Down);
            Press(Key.Up);
            Assert.Equal("/effort high", Assert.IsType<CommandChoice>(list.SelectedItem).Command);
            Press(Key.Enter);
            Assert.Equal("/effort high", line.Text);
            Press(Key.Enter);
            Assert.Null(view.CurrentRoom!.ClaudeEffort);
            ReleaseEnter();
            Enter();
            PumpUntil(() => !view.IsRunning);
            Pump(view.PendingSaves);
            Run("/model sonnet");

            line.Text = "/mc";
            PumpUntil(() => list.Items.Count == 1 && list.Items.OfType<CommandChoice>().First().Command == "/mcp");
            Press(Key.Enter);
            Assert.Equal("/mcp", line.Text);
            Press(Key.Enter);
            Assert.Empty(calls);
            ReleaseEnter();
            Enter();
            Assert.True(view.IsRunning);
            activeProfile = secondProfile;
            reply.SetResult((0, "sample: server --config private-value - ✓ Connected"));
            PumpUntil(() => !view.IsRunning);
            Pump(view.PendingSaves);
            Run("/mcp ; echo unsafe");
            Run("/unknown");
            var saved = Assert.Single(store.Load());
            Assert.Equal(chat, saved.Claude);
            Assert.Equal("high", saved.ClaudeEffort);
            Assert.Equal("sonnet", saved.ClaudeModel);
            var arguments = new List<string>();
            ClaudeCommands.AddTurnOptions(arguments, saved.ClaudeEffort, saved.ClaudeModel);
            Assert.Equal(["--effort", "high", "--model", "sonnet"], arguments);
            Assert.Equal(["mcp list"], calls);
            Assert.Equal(7, saved.Messages.Count);
            Assert.All(saved.Messages, msg => { Assert.NotNull(msg.Command); Assert.Equal(DiscussionMember.Boss, msg.To); });
            Assert.Equal("sample · ✓ Connected", saved.Messages[^1].Text);
            Assert.Equal(folder.FullName, saved.Messages[^1].Workspace);
            Assert.Equal(firstProfile, saved.Messages[^1].McpProfile);
            Assert.Null(saved.Messages[0].McpProfile);
            var manage = view.GetLogicalDescendants().OfType<Button>().Last(b => Equals(b.Content, "Manage MCP in console"));
            manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AssertNeedsFreshMcp();
            Assert.Single(consoles);
            activeProfile = firstProfile;
            view.CurrentRoom!.Claude = new ClaudeChat(Guid.NewGuid(), Path.Combine(folder.FullName, "other"), "Other project");
            manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([new TerminalContext(AppContext.BaseDirectory, firstProfile), new TerminalContext(folder.FullName, firstProfile)], consoles);
            Assert.Equal(["mcp list"], calls);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(sent);
            mode.IsChecked = false;
            Assert.True(voice.IsEnabled);
            Assert.False(line.IsEffectivelyVisible);
            Assert.True(editor.IsEffectivelyVisible);
            Assert.Equal("/version", editor.Text);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => sent.Count == 1 && !view.IsRunning);
            Assert.Contains("/version", sent[0]);
            Pump(view.PendingSaves);
        }
        finally { window.Close(); folder.Delete(recursive: true); }
    }

    private static void VerifyTerminalReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("trayvora-console-");
        try
        {
            var first = new TerminalContext(Directory.CreateDirectory(Path.Combine(directory.FullName, "one")).FullName, "profile");
            var second = new TerminalContext(Directory.CreateDirectory(Path.Combine(directory.FullName, "two")).FullName, "profile");
            var starts = new List<TerminalContext>();
            var currentProfile = first.Profile;
            // Use a hidden process with a fixed lifetime to exercise replacement without an agent session.
            var terminal = new TerminalPanel((_, context) =>
            {
                if (context.Profile != currentProfile) throw new IOException(ClaudeCommands.RefreshMcp);
                starts.Add(context);
                var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                var start = new System.Diagnostics.ProcessStartInfo(shell) { WorkingDirectory = context.Workspace, CreateNoWindow = true };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }) start.ArgumentList.Add(arg);
                return start;
            });
            try
            {
                terminal.Start(TerminalKind.Claude, first);
                Assert.True(terminal.Running);
                var buttons = terminal.GetLogicalDescendants().OfType<Button>().ToArray();
                var keep = buttons.Single(b => Equals(b.Content, "Keep current console"));
                var replace = buttons.Single(b => Equals(b.Content, "Replace console"));
                terminal.Start(TerminalKind.Claude, second);
                Assert.True(keep.IsEffectivelyVisible);
                Assert.True(terminal.Running);
                Assert.Equal([first], starts);
                keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(keep.IsEffectivelyVisible);
                Assert.True(terminal.Running);
                Assert.Equal([first], starts);
                terminal.Start(TerminalKind.Claude, second);
                currentProfile = "changed";
                replace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(terminal.Running);
                Assert.Equal([first], starts);

                currentProfile = second.Profile;
                replace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(replace.IsEffectivelyVisible);
                Assert.True(terminal.Running);
                Assert.Equal([first, second], starts);
            }
            finally { terminal.Stop(); }
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void VerifySessions()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-sessions-");
        var window = new Window { Width = 1000, Height = 700 };
        try
        {
            var store = new DiscussionStore(folder.FullName);
            var sent = new List<(DiscussionMember Agent, string Text)>();
            var claudeFails = false;
            var view = new DiscussionWindow(store, (agent, text, _, _, _, _) =>
                {
                    sent.Add((agent, text));
                    if (claudeFails && agent == DiscussionMember.Claude) throw new IOException("Claude Code was not found.");
                    return Task.FromResult<string?>(claudeFails ? "Claude: hello from Codex" : "Noted.");
                },
                () => [], null, () => Task.FromResult<IReadOnlyList<ClaudeChat>>([]),
                () => new ClaudeChat(Guid.NewGuid(), folder.FullName, "Test Claude chat"));
            view.Initialize();
            // Read the saved files only after the window has finished writing them.
            IReadOnlyList<DiscussionRoom> Stored() { Pump(view.PendingSaves); return store.Load(); }
            window.Content = view;
            window.Show();
            Pump(Task.Delay(100));

            var editor = view.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText?.StartsWith("Message Codex", StringComparison.Ordinal) == true);
            var send = view.GetLogicalDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Send");
            var replies = 0;
            foreach (var message in new[] { "First question", "Second question" })
            {
                editor.Text = message;
                send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // Wait for the reply itself: a fixed delay was too short on slower CI machines.
                replies++;
                PumpUntil(() => Stored().SingleOrDefault()?.Messages.Count >= replies * 2);
            }

            var room = Assert.Single(Stored());
            Assert.Equal("First question", room.Title);
            Assert.Equal(["First question", "Noted.", "Second question", "Noted."], room.Messages.Select(message => message.Text));
            Assert.Equal(2, sent.Count);
            Assert.All(sent, item => Assert.Equal(DiscussionMember.Codex, item.Agent));
            Assert.Contains("Second question", sent[1].Text);
            Assert.DoesNotContain("Noted.", sent[1].Text);

            // A second session, then switch back through the session list.
            view.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "+  New session"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.Text = "Other topic";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Stored().Any(item => item.Title == "Other topic" && item.Messages.Count >= 2));
            Assert.Equal(2, Stored().Count);
            WaitForButton(view, "First question").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => view.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(block => block.Text == "Second question"));
            Assert.Contains(view.GetLogicalDescendants().OfType<SelectableTextBlock>(), block => block.Text == "Second question");
            Assert.DoesNotContain(view.GetLogicalDescendants().OfType<SelectableTextBlock>(), block => block.Text == "Other topic");

            // Both agents: Codex hands off to Claude, Claude fails. Each failure is shown and saved; nothing else is lost.
            claudeFails = true;
            sent.Clear();
            view.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Both"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.Text = "Say hello to Claude";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => sent.Count >= 3 && Stored().Single(item => item.Title == "First question").Messages.Count(message => message.Failed) >= 2);
            Pump(Task.Delay(50));
            Assert.Equal([DiscussionMember.Codex, DiscussionMember.Claude, DiscussionMember.Claude], sent.Select(item => item.Agent));
            Assert.Contains("hello from Codex", sent[2].Text);
            Assert.StartsWith("Codex wrote:", sent[2].Text);
            var saved = Stored().Single(item => item.Title == "First question").Messages.TakeLast(4).ToArray();
            Assert.Equal("Say hello to Claude", saved[0].Text);
            Assert.Equal((DiscussionMember.Codex, (DiscussionMember?)DiscussionMember.Claude, "hello from Codex"), (saved[1].Sender, saved[1].To, saved[1].Text));
            Assert.All(saved[2..], message => Assert.True(message.Failed && message.Sender == DiscussionMember.Claude));
            Assert.Contains(view.GetLogicalDescendants().OfType<SelectableTextBlock>(), block => block.Text == "Claude Code was not found.");

            // Relay toggle on the Claude chip: Claude's plain reply is passed on to Codex automatically.
            claudeFails = false;
            sent.Clear();
            Button Named(string name) => view.GetLogicalDescendants().OfType<Button>().First(button => AutomationProperties.GetName(button) == name);
            Named("Pass the reply on to Codex").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.Text = "Review this and tell Codex";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(200));
            Assert.Equal([DiscussionMember.Claude, DiscussionMember.Codex], sent.Select(item => item.Agent));
            Assert.Equal("Review this", sent[0].Text); // the relay phrase is removed, nothing is added
            Assert.StartsWith("Claude wrote:", sent[1].Text);
            var relayed = Stored().Single(item => item.Title == "First question").Messages;
            Assert.Equal(DiscussionMember.Codex, relayed[^2].To);
            Assert.Equal(DiscussionMember.Claude, relayed[^2].Sender);

            // Relay off again: Claude's reply stays with Boss.
            Named("Pass the reply on to Codex").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            sent.Clear();
            editor.Text = "Just answer me";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(200));
            Assert.Equal([DiscussionMember.Claude], sent.Select(item => item.Agent));
            Assert.Equal("Just answer me", sent[0].Text);

            // Key phrase at the end, toggle off: Claude gets the message without the phrase, its reply goes on to Codex.
            sent.Clear();
            editor.Text = "Проверь этот план, перешли Кодексу";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(200));
            Assert.Equal([DiscussionMember.Claude, DiscussionMember.Codex], sent.Select(item => item.Agent));
            Assert.Contains("Проверь этот план", sent[0].Text);
            Assert.DoesNotContain("перешли Кодексу", sent[0].Text);
            Assert.Equal("Проверь этот план", sent[0].Text);
            Assert.StartsWith("Claude wrote:", sent[1].Text);
            var phrased = Stored().Single(item => item.Title == "First question").Messages;
            var request = phrased.Last(message => message.Sender == DiscussionMember.Boss);
            Assert.Equal("Проверь этот план, перешли Кодексу", request.Text);
            Assert.Equal(DiscussionMember.Codex, request.RelayTo);
            Assert.Equal(DiscussionMember.Codex, phrased[^2].To); // Claude's reply addressed to Codex
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "⇄ then Codex");

            // Delete removes the message from the saved session only.
            var before = Stored().Single(item => item.Title == "First question").Messages.Count;
            // Hover the message so its toolbar is built, then press its own Delete.
            Control row = view.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(block => block.Text == "First question");
            while (row.GetLogicalParent() is Control parent && parent is not StackPanel { Spacing: 14 }) row = parent;
            row.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, row, new Pointer(0, PointerType.Mouse, true),
                window, default, 0, new PointerPointProperties(), KeyModifiers.None));
            row.GetLogicalDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Delete from this session")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(100));
            var after = Stored().Single(item => item.Title == "First question").Messages;
            Assert.Equal(before - 1, after.Count);
            Assert.DoesNotContain(after, message => message.Text == "First question");
        }
        finally
        {
            window.Close();
            folder.Delete(recursive: true);
        }
    }

    private static void VerifyDiscussWith()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-discuss-");
        var window = new Window { Width = 1000, Height = 700 };
        try
        {
            var store = new DiscussionStore(folder.FullName);
            var calls = new List<(DiscussionMember Agent, string Text)>();
            var script = new Dictionary<DiscussionMember, Queue<string>>
            {
                [DiscussionMember.Codex] = new(["Claude: I read Client. Plan A: add a Phone field. Is A enough?",
                                                 "Claude: Revised plan A+B with validation. Agree?",
                                                 "Boss: Final plan: add Phone with validation (agreed with Claude)."]),
                [DiscussionMember.Claude] = new(["Add validation (B).", "Agreed."]),
            };
            var endless = false;
            var rounds = 5;
            var view = new DiscussionWindow(store, (agent, text, _, _, _, _) =>
                {
                    calls.Add((agent, text));
                    if (endless) return Task.FromResult<string?>(agent == DiscussionMember.Codex ? "Claude: one more question?" : "Codex: and one more from me");
                    return Task.FromResult<string?>(script[agent].Dequeue());
                },
                () => [], null, () => Task.FromResult<IReadOnlyList<ClaudeChat>>([]),
                () => new ClaudeChat(Guid.NewGuid(), folder.FullName, "Claude chat"), discussRounds: () => rounds);
            view.Initialize();
            window.Content = view;
            window.Show();
            Pump(Task.Delay(100));
            var editor = view.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText?.StartsWith("Message Codex", StringComparison.Ordinal) == true);
            var send = view.GetLogicalDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Send");

            editor.Text = "Найди класс Client, посмотри поля и обсуди с Клодом, как лучше решить задачу";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(300));

            Assert.Equal([DiscussionMember.Codex, DiscussionMember.Claude, DiscussionMember.Codex, DiscussionMember.Claude, DiscussionMember.Codex],
                calls.Select(call => call.Agent));
            Assert.Contains("You lead the discussion", calls[0].Text);
            Assert.Contains("at most 5 times", calls[0].Text);
            Assert.Contains("asks for your review", calls[1].Text);
            Assert.Contains("I read Client", calls[1].Text);
            Assert.Contains("decide yourself", calls[2].Text);
            Assert.Contains("4 more exchanges", calls[2].Text);
            var messages = store.Load().Single().Messages;
            Assert.Equal(DiscussionMember.Claude, messages[0].DiscussWith);
            Assert.Equal([DiscussionMember.Claude, DiscussionMember.Codex, DiscussionMember.Claude, DiscussionMember.Codex, DiscussionMember.Boss],
                messages.Skip(1).Select(message => message.To!.Value));
            Assert.True(messages[^1].Final);
            Assert.Equal("Final plan: add Phone with validation (agreed with Claude).", messages[^1].Text);
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "✓ final answer");
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "⇆ discuss with Claude");

            // Agents that never agree stop after five exchanges; the lead's last turn goes to Boss.
            endless = true;
            calls.Clear();
            editor.Text = "Обсуди с Клодом решение";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(400));
            Assert.Equal(11, calls.Count); // lead + 5 × (partner + lead)
            Assert.Equal(5, calls.Count(call => call.Agent == DiscussionMember.Claude));
            Assert.Contains("That was the last exchange", calls[^1].Text);
            var last = store.Load().Single().Messages[^1];
            Assert.Equal(DiscussionMember.Codex, last.Sender);
            Assert.Equal(DiscussionMember.Boss, last.To);
            Assert.True(last.Final);

            // The number of rounds comes from Settings and applies to the next discussion.
            rounds = 2;
            calls.Clear();
            editor.Text = "Обсуди с Клодом ещё раз";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(300));
            Assert.Equal(5, calls.Count); // lead + 2 × (partner + lead)
            Assert.Contains("at most 2 times", calls[0].Text);
            Assert.Contains("That was the last exchange", calls[^1].Text);
        }
        finally
        {
            window.Close();
            folder.Delete(recursive: true);
        }
    }

    /// <summary>"Both" reaches the agents at the same time, replies show while written, and Pause keeps the partial text.</summary>
    private static void VerifyStreamingSession()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-stream-");
        var window = new Window { Width = 1000, Height = 700 };
        try
        {
            var store = new DiscussionStore(folder.FullName);
            var started = new List<DiscussionMember>();
            var replies = new Dictionary<DiscussionMember, TaskCompletionSource<string?>>();
            var streams = new Dictionary<DiscussionMember, ReplyStream>();
            var view = new DiscussionWindow(store, async (agent, _, _, stream, _, ct) =>
                {
                    started.Add(agent);
                    streams[agent] = stream;
                    var reply = replies[agent] = new TaskCompletionSource<string?>();
                    using (ct.Register(() => reply.TrySetCanceled(ct))) return await reply.Task;
                },
                () => [], null, () => Task.FromResult<IReadOnlyList<ClaudeChat>>([]),
                () => new ClaudeChat(Guid.NewGuid(), folder.FullName, "Claude chat"));
            view.Initialize();
            window.Content = view;
            window.Show();
            Pump(Task.Delay(100));
            var editor = view.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText?.StartsWith("Message Codex", StringComparison.Ordinal) == true);
            Button Named(string name) => view.GetLogicalDescendants().OfType<Button>().First(button => AutomationProperties.GetName(button) == name);
            view.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Both")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            editor.Text = "Compare the plans";
            Named("Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(100));
            Assert.Equal([DiscussionMember.Codex, DiscussionMember.Claude], started);

            // Text written so far appears before the agent finishes.
            streams[DiscussionMember.Claude].Append("Partial **answer**");
            streams[DiscussionMember.Codex].Append("Codex says");
            Pump(Task.Delay(250));
            Assert.Contains(view.GetLogicalDescendants().OfType<SelectableTextBlock>(), block => block.Inlines?.Any(inline => inline is Avalonia.Controls.Documents.Span) == true);
            Assert.Contains(view.GetLogicalDescendants().OfType<SelectableTextBlock>(), block => block.Text == "Codex says");

            // Codex finishes, Claude is paused: Codex's answer and Claude's partial text are both kept.
            replies[DiscussionMember.Codex].SetResult("Codex final answer");
            Pump(Task.Delay(100));
            Named("Pause").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(200));
            var messages = store.Load().Single().Messages;
            Assert.Equal("Codex final answer", messages[1].Text);
            Assert.Equal((DiscussionMember.Claude, "Partial **answer**", true), (messages[2].Sender, messages[2].Text, messages[2].Interrupted));
            Assert.DoesNotContain(messages, message => message.Failed);
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "⏸ interrupted");
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text?.StartsWith("Paused", StringComparison.Ordinal) == true);
        }
        finally
        {
            window.Close();
            folder.Delete(recursive: true);
        }
    }

    private static void VerifyDesktopDelivery()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-delivery-");
        try
        {
            var directory = Path.Combine(folder.FullName, "sessions");
            File.WriteAllText(directory, "Block directory creation during the first save");
            var store = new DiscussionStore(directory);
            var view = new DiscussionWindow(store, (_, _, _, _, _, _) => throw new NotSupportedException(),
                () => [], null, () => Task.FromResult<IReadOnlyList<ClaudeChat>>([]), () => null);
            var originalChat = Guid.NewGuid();
            var room = new DiscussionRoom { CodexDesktop = true, CodexChatId = originalChat };
            var originalProfile = Path.Combine(folder.FullName, "profile-a");
            string selectedProfile = originalProfile;
            var submissions = 0;
            Task<DesktopTurn?> Submit(DesktopDelivery delivery)
            {
                submissions++;
                var persisted = Assert.Single(store.ReadRoom(room.Id).Messages);
                Assert.Equal(originalChat, delivery.ThreadId);
                Assert.Equal(originalProfile, delivery.Profile);
                Assert.Equal(delivery.ThreadId, persisted.ThreadId);
                Assert.Equal(delivery.Profile, persisted.DeliveryProfile);
                Assert.Equal(delivery.Id, persisted.DeliveryId);
                return Task.FromResult<DesktopTurn?>(null);
            }

            var failed = view.SubmitDesktopDeliveryAsync(room, selectedProfile, Submit);
            Assert.Throws<IOException>(() => Pump(failed));
            Pump(view.PendingSaves);
            Assert.Equal(0, submissions);
            Assert.Empty(room.Messages);

            File.Delete(directory);
            var retry = view.SubmitDesktopDeliveryAsync(room, selectedProfile, Submit);
            selectedProfile = Path.Combine(folder.FullName, "profile-b");
            room.CodexChatId = Guid.NewGuid();
            Pump(retry);
            Assert.Equal(1, submissions);
            Assert.NotEqual(selectedProfile, room.Messages.Single().DeliveryProfile);
        }
        finally { folder.Delete(true); }
    }

    private static void VerifyAttachments()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-attach-ui-");
        try
        {
            var target = Path.Combine(folder.FullName, "session");
            var source = Path.Combine(folder.FullName, "notes.txt");
            File.WriteAllText(source, "synthetic");
            var attachments = new ComposerAttachments(() => target);
            Assert.Equal(2, attachments.AddFiles([source, source, Path.Combine(folder.FullName, "missing.txt")]));
            Assert.Equal(["notes.txt", "notes (2).txt"], attachments.Files.Select(Path.GetFileName));
            Assert.All(attachments.Files, file => Assert.StartsWith(target, file));
            Assert.True(attachments.Chips.IsVisible);
            var message = ComposerAttachments.WithFiles("Посмотри", attachments.Files);
            Assert.Contains(attachments.Files[1], message);
            Assert.Equal("Посмотри", ComposerAttachments.WithFiles("Посмотри", []));

            // Attached in one session, sent from another: the files move into the sending session's folder.
            var other = Path.Combine(folder.FullName, "other-session");
            var moved = attachments.MoveInto(other);
            Assert.Equal(["notes.txt", "notes (2).txt"], moved.Select(Path.GetFileName));
            Assert.All(moved, file => Assert.True(File.Exists(file) && file.StartsWith(other + Path.DirectorySeparatorChar)));
            Assert.Equal(moved, attachments.Files);
            Assert.Equal(moved, ComposerAttachments.FilesIn(ComposerAttachments.WithFiles("x", moved), other));

            using (var image = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96)))
                Assert.True(attachments.AddImage(image));
            Assert.True(ComposerAttachments.IsImage(attachments.Files[^1]));
            Assert.True(new FileInfo(attachments.Files[^1]).Length > 0);
            attachments.Clear();
            Assert.Empty(attachments.Files);
            Assert.False(attachments.Chips.IsVisible);

            var locked = Path.Combine(folder.FullName, "locked.txt");
            var problems = new List<string>();
            attachments.Problem += problems.Add;
            using (File.Open(locked, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Pump(attachments.AddFilesAsync([source, locked, source]));
            Assert.Equal(["notes.txt", "notes (2).txt"], attachments.Files.Select(Path.GetFileName));
            Assert.Contains("locked.txt", Assert.Single(problems));
            Assert.False(File.Exists(Path.Combine(target, "locked.txt")));
            Assert.False(attachments.Copying);

            var box = new TextBox { Text = "Hello world" };
            box.SelectionStart = 6;
            box.SelectionEnd = 11;
            ComposerAttachments.InsertAtCaret(box, "Trayvora");
            Assert.Equal("Hello Trayvora", box.Text);

            Assert.Equal(DiscussionWindow.EditorMinHeight, DiscussionWindow.EditorHeight(100, -500, 400));
            Assert.Equal(400, DiscussionWindow.EditorHeight(100, 900, 400));
            Assert.Equal(160, DiscussionWindow.EditorHeight(100, 60, 400));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static void VerifyMarkdown()
    {
        var view = new MarkdownView();
        view.Show("# Plan\n\nUse **bold** and `code` and [docs](https://example.com) and [file](file:///c:/x).\n\n- one\n- two\n\n```cs\nvar x = 1;\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n<script>alert(1)</script>");
        Assert.Contains("A\tB\n1\t2", view.CopyText);
        Assert.Contains("docs (https://example.com) and file (file:///c:/x)", view.CopyText);
        Assert.Equal("source (src/File.cs#L27) and https://example.com",
            MarkdownView.ReadableText("[**source**][file] and <https://example.com>\n\n[file]: src/File.cs#L27"));
        var blocks = view.GetLogicalDescendants().OfType<SelectableTextBlock>().ToArray();
        Assert.Contains(blocks, block => block.Text == "Plan" && block.FontWeight == Avalonia.Media.FontWeight.SemiBold);
        Assert.Contains(blocks, block => block.Text == "var x = 1;");
        Assert.Contains(blocks, block => block.Text == "one");
        Assert.Contains(blocks, block => block.Text == "2");
        Assert.Contains(blocks, block => block.Text == "<script>alert(1)</script>");
        Assert.Single(view.GetLogicalDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Copy code");
        Assert.Equal("https://example.com/", MarkdownView.SafeUrl("https://example.com"));
        Assert.Null(MarkdownView.SafeUrl("file:///c:/x"));

        // A reply that grows rebuilds only the blocks that changed.
        var live = new MarkdownView();
        live.Show("First paragraph.\n\nSecond");
        Assert.Equal(2, live.Rebuilt);
        live.Show("First paragraph.\n\nSecond paragraph");
        Assert.Equal(3, live.Rebuilt);
        Assert.Equal(["First paragraph.", "Second paragraph"], live.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(block => block.Text));

        // Select text: the whole message becomes one read-only box (selection can cross paragraphs, lists and code), and back.
        live.Show("**First** paragraph.\n\n[Second](https://example.com) paragraph");
        live.ToggleSelecting();
        Assert.True(live.Selecting);
        var box = Assert.Single(live.GetLogicalDescendants().OfType<TextBox>());
        Assert.True(box.IsReadOnly);
        Assert.Equal("First paragraph.\n\nSecond (https://example.com) paragraph", box.Text);
        Assert.All(live.Children.Where(child => child != box), child => Assert.False(child.IsVisible));
        box.SelectionStart = 2;
        box.SelectionEnd = 9;
        live.Show("**First** paragraph.\n\nSecond paragraph\n\n`Third`");
        Assert.Equal("rst par", box.SelectedText);
        Assert.Equal("First paragraph.\n\nSecond paragraph\n\nThird", box.Text);
        var openMarkup = "Before **chosen words";
        box.ClearSelection();
        live.Show(openMarkup);
        box.SelectionStart = box.Text!.IndexOf("words", StringComparison.Ordinal);
        box.SelectionEnd = box.Text.IndexOf("chosen", StringComparison.Ordinal);
        live.Show(openMarkup + "** after");
        Assert.Equal("chosen ", box.SelectedText);
        Assert.Equal("Before chosen words after", box.Text);
        Assert.True(box.SelectionStart > box.SelectionEnd);

        box.ClearSelection();
        live.Show("**echo echo");
        box.SelectionStart = 7;
        box.SelectionEnd = 11;
        live.Show("**echo echo** again");
        Assert.Equal("echo", box.SelectedText);
        Assert.Equal("**echo echo", box.Text);
        box.ClearSelection();
        PumpUntil(() => box.Text == "echo echo again");
        Assert.Same(box, live.Children[^1]);
        Assert.All(live.Children.Where(child => child != box), child => Assert.False(child.IsVisible));
        live.ToggleSelecting();
        Assert.False(live.Selecting);
        Assert.Empty(live.GetLogicalDescendants().OfType<TextBox>());
        Assert.All(live.Children, child => Assert.True(child.IsVisible));
    }

    private static void VerifyLongSession()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-long-");
        var window = new Window { Width = 900, Height = 600 };
        try
        {
            var store = new DiscussionStore(folder.FullName);
            var room = store.Create(null);
            for (var i = 0; i < 130; i++)
                room.Messages.Add(new DiscussionMessage(DiscussionMember.Codex, DiscussionMember.Boss, $"message {i}", DateTimeOffset.Now));
            store.Save(room);
            var view = new DiscussionWindow(store, (_, _, _, _, _, _) => Task.FromResult<string?>("ok"),
                () => [], null, () => Task.FromResult<IReadOnlyList<ClaudeChat>>([]), () => null);
            view.Initialize();
            window.Content = view;
            window.Show();
            Pump(Task.Delay(100));
            int Shown() => view.GetLogicalDescendants().OfType<SelectableTextBlock>().Count(block => block.Text?.StartsWith("message ", StringComparison.Ordinal) == true);
            Button Earlier() => view.GetLogicalDescendants().OfType<Button>().Single(button => (button.Content as string)?.StartsWith("Show earlier", StringComparison.Ordinal) == true);

            Assert.Equal(60, Shown());
            Assert.Equal("Show earlier messages (70)", Earlier().Content);
            Earlier().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(120, Shown());
            Earlier().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(130, Shown());
            Assert.DoesNotContain(view.GetLogicalDescendants().OfType<Button>(), button => (button.Content as string)?.StartsWith("Show earlier", StringComparison.Ordinal) == true);
            var texts = view.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(block => block.Text).Where(text => text?.StartsWith("message ", StringComparison.Ordinal) == true).ToArray();
            Assert.Equal(Enumerable.Range(0, 130).Select(i => $"message {i}"), texts);
        }
        finally
        {
            window.Close();
            folder.Delete(recursive: true);
        }
    }

    private static void VerifyAudioEngineSwitch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var window = new Window { Width = 900, Height = 760 };
        try
        {
            var downloaded = new HashSet<int> { 0 }; // only Base is on disk
            var downloads = new List<int>();
            string[] names = ["Base · 148 MB", "Small · 488 MB", "Medium · 1.53 GB", "Large v3 Turbo · 1.62 GB"];
            var view = new AudioToTextView((_, _) => new TestTranscriber(),
                cloud => cloud ? "gpt-4o-transcribe" : "Base · 148 MB",
                cloud => !cloud, // no OpenAI key
                () => true,      // Settings chose OpenAI
                new WhisperChoices(names, () => 0, downloaded.Contains, (index, progress, _) =>
                {
                    downloads.Add(index);
                    progress.Report(1);
                    downloaded.Add(index);
                    return Task.CompletedTask;
                }));
            window.Content = view;
            window.Show();
            Pump(Task.Delay(50));
            TextBlock Note() => view.GetLogicalDescendants().OfType<TextBlock>().Single(block =>
                block.Text?.StartsWith("No OpenAI API key", StringComparison.Ordinal) == true
                || block.Text?.StartsWith("Runs on this computer", StringComparison.Ordinal) == true
                || block.Text?.StartsWith("Whisper ", StringComparison.Ordinal) == true && block.Text.Contains("not downloaded", StringComparison.Ordinal));
            Button Named(string name) => view.GetLogicalDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
            var model = view.GetLogicalDescendants().OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == "Whisper model");
            Button Download() => view.GetLogicalDescendants().OfType<Button>().Single(button => (button.Content as string)?.StartsWith("Download", StringComparison.Ordinal) == true);

            Assert.True(view.Cloud);
            Assert.Contains("selected", Named("Use OpenAI").Classes);
            Assert.StartsWith("No OpenAI API key", Note().Text);
            Assert.False(model.IsEffectivelyVisible); // model choice only for local Whisper

            Named("Use local Whisper").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(view.Cloud);
            Assert.True(model.IsEffectivelyVisible);
            Assert.Equal("Runs on this computer with Whisper Base · 148 MB. Free; the audio stays here.", Note().Text);
            Assert.False(Download().IsVisible);

            // Pick a model that is not downloaded yet, download it from the page.
            model.SelectedIndex = 3;
            Assert.Equal(3, view.SelectedModel);
            Assert.Contains("not downloaded", Note().Text);
            Assert.True(Download().IsVisible);
            Assert.Equal("Download Large v3 Turbo (1.6 GB)", Download().Content);
            Download().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(50));
            Assert.Equal([3], downloads);
            Assert.Equal(3, view.SelectedModel);
            Assert.Equal("Runs on this computer with Whisper Large v3 Turbo · 1.62 GB. Free; the audio stays here.", Note().Text);
            Assert.False(Download().IsVisible);
            Assert.EndsWith("downloaded", (string)model.SelectedItem!);
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifyAccounts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var window = new Window { Width = 900, Height = 800 };
        try
        {
            var signedIn = false;
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // Stand-in for a sign-in command: prints a link, waits for a pasted code, then succeeds.
            var provider = new AccountProvider("Claude", "Test provider",
                _ => Task.FromResult(signedIn ? new AccountStatus(true, "me@example.com · Max") : new AccountStatus(false, "Not signed in")),
                () =>
                {
                    var session = new CliSession(cmd, ["/v:on", "/c", "echo Open https://example.com/oauth?client=1 to sign in & set /p CODE=Paste code: & echo received !CODE!"]);
                    session.Line += line => { if (line.Contains("received abc123", StringComparison.Ordinal)) signedIn = true; };
                    return session;
                },
                _ => { signedIn = false; return Task.FromResult(0); });
            var changes = new List<(string Name, AccountStatus Status)>();
            var view = new AccountsView([provider]);
            view.StatusChanged += (name, status) => changes.Add((name, status));
            window.Content = view;
            window.Show();
            Pump(Task.Delay(200));

            TextBlock Text(string value) => view.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Text == value);
            Button Named(string name) => view.GetLogicalDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
            Assert.NotNull(Text("Not signed in"));
            Assert.Equal("Sign in", Named("Sign in to Claude").Content);
            Assert.False(Named("Sign out of Claude").IsVisible);

            Named("Sign in to Claude").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(800));
            var open = view.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Open sign-in page"));
            Assert.True(open.IsVisible); // the link from the command output became a button
            var code = view.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText?.StartsWith("If the browser shows a code", StringComparison.Ordinal) == true);
            Assert.True(code.IsEffectivelyVisible);
            code.Text = "abc123";
            view.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Send code")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(1500));

            Assert.True(signedIn);
            Assert.NotNull(Text("Signed in"));
            Assert.NotNull(Text("me@example.com · Max"));
            Assert.Equal("Sign in again", Named("Sign in to Claude").Content);
            Assert.True(Named("Sign out of Claude").IsVisible);
            Assert.Contains(changes, change => change.Name == "Claude" && change.Status.SignedIn);

            Named("Sign out of Claude").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(300));
            Assert.False(signedIn);
            Assert.NotNull(Text("Not signed in"));
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifyFloatingAgent()
    {
        var agent = new FloatingAgent(() => Task.CompletedTask);
        try
        {
            agent.Show();
            var button = Assert.IsType<Button>(agent.Content);
            button.ApplyTemplate();
            foreach (var state in Enum.GetValues<AgentState>())
            {
                agent.SetState(state);
                Assert.True(agent.IsVisible);
                Assert.True(button.IsEnabled);
                Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(button)));
            }
        }
        finally
        {
            agent.Close();
        }
    }

    /// <summary>Runs the UI loop until <paramref name="done"/> is true (at most 8 seconds).</summary>
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!done() && DateTime.UtcNow < deadline) Pump(Task.Delay(20));
    }

    /// <summary>
    /// Waits until exactly one button with this accessible name is in the view. The session list is rebuilt on the UI
    /// loop after a save, so the button may appear later than the file on disk. On timeout, lists the buttons it saw.
    /// </summary>
    private static Button WaitForButton(Control view, string name)
    {
        Button[] Matches() => view.GetLogicalDescendants().OfType<Button>().Where(button => AutomationProperties.GetName(button) == name).ToArray();
        PumpUntil(() => Matches().Length == 1);
        var found = Matches();
        if (found.Length == 1) return found[0];
        var seen = view.GetLogicalDescendants().OfType<Button>()
            .Select(button => AutomationProperties.GetName(button) ?? button.Content?.ToString() ?? "(unnamed)");
        throw new Xunit.Sdk.XunitException(
            $"Expected one button \"{name}\", found {found.Length}. Buttons in the view: {string.Join(" | ", seen)}");
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Dispatcher.UIThread.PushFrame(frame);
        task.WaitAsync(timeout.Token).GetAwaiter().GetResult();
    }

    private sealed class TestRecorder : IAudioRecorder
    {
        public IReadOnlyList<Microphone> ListMicrophones() => [new("test", "Test microphone")];

        public async Task<int> RecordAsync(string? deviceId, byte[] pcm, IProgress<double> peak,
            CancellationToken stop, CancellationToken cancel)
        {
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(stop, cancel);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stopped.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancel.IsCancellationRequested) { }
            return 2;
        }
    }

    private sealed class TestTranscriber : ISpeechTranscriber
    {
        public Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm, string language, CancellationToken cancellationToken) =>
            Task.FromResult("Test transcript");
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
