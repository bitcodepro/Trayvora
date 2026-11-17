using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VoiceAgent.Desktop;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class VoiceSettingsPanel : StackPanel
{
    public VoiceSettingsPanel(VoiceHotkeys initial, Func<VoiceHotkeys, string?> apply, SpeechSettings speech, Action changed)
    {
        Spacing = 8;
        Children.Add(new TextBlock { Text = "Shortcuts", FontSize = 22, FontWeight = FontWeight.SemiBold });
        Action? shortcutsChanged = null;
        void OnShortcutChanged() => shortcutsChanged?.Invoke();
        Children.Add(new TextBlock
        {
            Text = "These keys work everywhere in Windows, even when Trayvora is hidden. Press once to start recording, press the same keys again to stop. Esc in the Trayvora window cancels.",
            TextWrapping = TextWrapping.Wrap, Foreground = TrayvoraTheme.Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
        });
        var preview = AddShortcut("Record with preview", initial.Preview, OnShortcutChanged,
            "Speak, press again: the text appears in a small panel at the top of the screen. Check or edit it, then send it with “Send preview”. Goes to the mode chosen in the main window (Codex, questions or Claude).");
        var background = AddShortcut("Record and send at once", initial.Background, OnShortcutChanged,
            "Like the one above, but without the preview: the recognized text goes straight to Codex. A notification appears when the reply is ready.");
        var send = AddShortcut("Send preview", initial.Send, OnShortcutChanged,
            "Sends the text shown in the preview panel. Works only while the preview is open.");
        var dictate = AddShortcut("Dictate into Codex Desktop", initial.DictationShortcut, OnShortcutChanged,
            "Codex Desktop must be the active window. Your speech is typed into its message box; nothing is sent until you press Enter there.");
        var shared = AddShortcut("Send to Codex Desktop chat", initial.SharedShortcut, OnShortcutChanged,
            "Records and sends to the Codex Desktop chat selected in the main window (Codex mode → Choose Codex Desktop chat). Works with the app minimized.");
        var longQuestions = AddShortcut("Deep question (GPT, long memory)", initial.LongShortcut, OnShortcutChanged,
            "Ask GPT by voice; the answer appears and can be read aloud. Remembers earlier questions (saved in Memory). Needs an OpenAI API key.");
        var shortQuestions = AddShortcut("Quick question (GPT, last six exchanges)", initial.ShortShortcut, OnShortcutChanged,
            "Same as above, but remembers only the last six questions while the app runs. Needs an OpenAI API key.");
        var windowsAudio = AddShortcut("Record computer sound and send", initial.LoopbackShortcut, OnShortcutChanged,
            "Records what plays on the computer (a video, a call) instead of the microphone, transcribes it and sends it like “Record with preview”.");
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap };
        shortcutsChanged = () =>
        {
            try
            {
                feedback.Text = apply(new VoiceHotkeys(preview(), background(), send(), dictate(), shared(), longQuestions(), shortQuestions(), windowsAudio())) ?? "Shortcuts saved.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                feedback.Text = "Could not save shortcuts. Previous shortcuts are still active.";
            }
        };
        Children.Add(feedback);

        Children.Add(new TextBlock { Text = "Speech recognition", FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        var provider = new ComboBox { ItemsSource = new[] { "Local Whisper", "OpenAI Audio API" }, SelectedIndex = speech.Current.Cloud ? 1 : 0 };
        var local = new ComboBox { ItemsSource = SpeechSettings.LocalModels, SelectedIndex = speech.Current.Local };
        var remote = new ComboBox { ItemsSource = SpeechSettings.CloudModels, SelectedIndex = speech.Current.Remote };
        var key = new TextBox { PasswordChar = '●', MaxLength = 512, PlaceholderText = "OpenAI API key" };
        var hints = new TextBox
        {
            Text = speech.Current.EffectiveWhisperHints,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 104,
            MaxHeight = 180,
            PlaceholderText = "One word or phrase per line",
        };
        var vocabulary = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Label { Content = "Whisper hints", Target = hints },
                new TextBlock { Text = "Add names, repository names or jargon, one entry per line.", TextWrapping = TextWrapping.Wrap },
                hints,
            },
        };
        TrayvoraTheme.StyleControls(provider, local, remote, key, hints);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var remove = new Button { Content = "Forget API key", IsEnabled = speech.Current.ProtectedKey is not null };

        void Update()
        {
            var cloud = provider.SelectedIndex == 1;
            local.IsVisible = vocabulary.IsVisible = !cloud;
            remote.IsVisible = key.IsVisible = cloud;
            remove.IsEnabled = speech.Current.ProtectedKey is not null;
            key.PlaceholderText = remove.IsEnabled ? "API key saved · type to replace" : "OpenAI API key";
            note.Text = cloud
                ? "Recordings are sent to OpenAI. Audio API usage is billed separately."
                : "If a model is missing, launch a download from this panel.";
        }
        void SaveSpeech()
        {
            try
            {
                speech.Save(provider.SelectedIndex == 1, local.SelectedIndex, remote.SelectedIndex, key.Text ?? "", hints.Text ?? "");
                if (!speech.Current.Cloud) hints.Text = speech.Current.EffectiveWhisperHints;
                key.Clear();
                Update();
                changed();
                note.Text = speech.Current.Cloud ? "OpenAI Audio enabled. Audio usage is billed separately." : "Local Whisper settings saved for the next recording.";
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                note.Text = error is InvalidOperationException ? error.Message : "Could not save speech settings.";
            }
        }

        provider.SelectionChanged += (_, _) => { Update(); SaveSpeech(); };
        local.SelectionChanged += (_, _) => SaveSpeech();
        remote.SelectionChanged += (_, _) => SaveSpeech();
        hints.LostFocus += (_, _) =>
        {
            if (hints.Text != speech.Current.EffectiveWhisperHints) SaveSpeech();
        };
        key.LostFocus += (_, _) => { if (!string.IsNullOrWhiteSpace(key.Text)) SaveSpeech(); };
        key.KeyDown += (_, args) =>
        {
            if (args.Key != Avalonia.Input.Key.Enter || string.IsNullOrWhiteSpace(key.Text)) return;
            SaveSpeech();
            args.Handled = true;
        };
        remove.Click += (_, _) =>
        {
            try
            {
                speech.RemoveKey();
                key.Clear();
                provider.SelectedIndex = 0;
                Update();
                changed();
                note.Text = "API key removed. Local Whisper selected.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                note.Text = "Could not remove the key.";
            }
        };

        var download = new Button { Content = "Download selected Whisper model" };
        var cancel = new Button { Content = "Cancel download", IsVisible = false };
        TrayvoraTheme.StyleControls(remove, download, cancel);
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, IsVisible = false };
        CancellationTokenSource? pending = null;
        DetachedFromVisualTree += (_, _) => pending?.Cancel();
        cancel.Click += (_, _) => pending?.Cancel();
        download.Click += async (_, _) =>
        {
            using var operation = new CancellationTokenSource();
            pending = operation;
            download.IsEnabled = local.IsEnabled = false;
            progress.Value = 0;
            progress.IsVisible = cancel.IsVisible = true;
            try
            {
                await speech.ModelAt(local.SelectedIndex).DownloadAsync(new Progress<double>(value => progress.Value = value), operation.Token);
                note.Text = "Model downloaded and verified.";
                changed();
            }
            catch (OperationCanceledException) { note.Text = "Download stopped."; }
            catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException)
            {
                note.Text = "Download failed. Check your connection and free space on E:.";
            }
            finally
            {
                pending = null;
                progress.IsVisible = cancel.IsVisible = false;
                download.IsEnabled = local.IsEnabled = true;
            }
        };
        local.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty) download.IsVisible = local.IsVisible;
        };
        Children.AddRange([provider, local, vocabulary, remote, key, note, remove, download, cancel, progress]);
        Update();
        download.IsVisible = local.IsVisible;
    }

    private Func<HotkeyShortcut> AddShortcut(string caption, HotkeyShortcut initial, Action changed, string? hint = null)
    {
        var ctrl = new CheckBox { Content = "Ctrl", IsChecked = initial.Modifiers.HasFlag(HotkeyModifiers.Ctrl) };
        var alt = new CheckBox { Content = "Alt", IsChecked = initial.Modifiers.HasFlag(HotkeyModifiers.Alt) };
        var shift = new CheckBox { Content = "Shift", IsChecked = initial.Modifiers.HasFlag(HotkeyModifiers.Shift) };
        var win = new CheckBox { Content = "Win", IsChecked = initial.Modifiers.HasFlag(HotkeyModifiers.Win) };
        var codes = ShortcutKeys.ToArray();
        var key = new ComboBox
        {
            ItemsSource = codes.Select(HotkeyShortcut.KeyName).ToArray(),
            SelectedIndex = Math.Max(0, Array.IndexOf(codes, initial.Key)),
            MaxDropDownHeight = 320,
        };
        var record = new Button { Content = "Record shortcut" };
        ToolTip.SetTip(record, "Click, then press the key combination. Esc cancels. Windows keeps some Win shortcuts for itself; tick Win instead if a combination does not register.");
        Avalonia.Automation.AutomationProperties.SetName(record, $"Record shortcut: {caption}");
        TrayvoraTheme.StyleControls(record);
        var recording = false;
        var suppress = false;
        void Stop(string text) { recording = false; record.Content = text; }
        record.Click += (_, _) =>
        {
            recording = true;
            record.Content = "Press keys… (Esc cancels)";
        };
        record.LostFocus += (_, _) => { if (recording) Stop("Record shortcut"); };
        record.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            if (e.Key == Avalonia.Input.Key.Escape) { Stop("Record shortcut"); return; }
            if (HotkeyShortcut.VirtualKey(e.Key) is not { } code) return; // still holding modifiers only
            var mods = e.KeyModifiers;
            var shortcut = new HotkeyShortcut(
                (mods.HasFlag(Avalonia.Input.KeyModifiers.Control) ? HotkeyModifiers.Ctrl : 0) |
                (mods.HasFlag(Avalonia.Input.KeyModifiers.Alt) ? HotkeyModifiers.Alt : 0) |
                (mods.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? HotkeyModifiers.Shift : 0) |
                (mods.HasFlag(Avalonia.Input.KeyModifiers.Meta) ? HotkeyModifiers.Win : 0), code);
            if (!shortcut.IsValid)
            {
                record.Content = $"{shortcut} needs Ctrl, Alt or Win — try again";
                return;
            }
            suppress = true;
            ctrl.IsChecked = shortcut.Modifiers.HasFlag(HotkeyModifiers.Ctrl);
            alt.IsChecked = shortcut.Modifiers.HasFlag(HotkeyModifiers.Alt);
            shift.IsChecked = shortcut.Modifiers.HasFlag(HotkeyModifiers.Shift);
            win.IsChecked = shortcut.Modifiers.HasFlag(HotkeyModifiers.Win);
            key.SelectedIndex = Array.IndexOf(codes, code);
            suppress = false;
            Stop($"Recorded {shortcut}");
            changed();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Children.Add(new TextBlock { Text = caption, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        if (hint is not null) Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = TrayvoraTheme.Muted, FontSize = 12 });
        Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { record, ctrl, alt, shift, win, key } });
        void Changed() { if (!suppress) changed(); }
        ctrl.IsCheckedChanged += (_, _) => Changed();
        alt.IsCheckedChanged += (_, _) => Changed();
        shift.IsCheckedChanged += (_, _) => Changed();
        win.IsCheckedChanged += (_, _) => Changed();
        key.SelectionChanged += (_, _) => Changed();
        return () => new HotkeyShortcut(
            (ctrl.IsChecked == true ? HotkeyModifiers.Ctrl : 0) |
            (alt.IsChecked == true ? HotkeyModifiers.Alt : 0) |
            (shift.IsChecked == true ? HotkeyModifiers.Shift : 0) |
            (win.IsChecked == true ? HotkeyModifiers.Win : 0),
            codes[Math.Max(0, key.SelectedIndex)]);
    }

    /// <summary>Keys offered for shortcuts: Space, A–Z, 0–9, F1–F24.</summary>
    internal static IEnumerable<uint> ShortcutKeys =>
        new[] { 0x20u }.Concat(Enumerable.Range('A', 26).Select(c => (uint)c))
            .Concat(Enumerable.Range('0', 10).Select(c => (uint)c))
            .Concat(Enumerable.Range(0x70, 24).Select(c => (uint)c));

}
