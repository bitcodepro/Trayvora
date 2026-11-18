using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VoiceAgent.Application;
using VoiceAgent.Codex;

namespace VoiceAgent.Desktop;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class SettingsWindow : Window
{
    /// <summary>Interface language; applied the next time Trayvora starts.</summary>
    private static Control LanguageCard(string? settingsDirectory)
    {
        if (settingsDirectory is null) return new Panel { IsVisible = false };
        var saved = UiLanguage.Saved(settingsDirectory);
        var choice = new ComboBox
        {
            ItemsSource = UiLanguage.Choices.Select(item => item.Name).ToArray(), MinWidth = 200,
            SelectedIndex = Math.Max(0, Array.FindIndex(UiLanguage.Choices, item => item.Code == saved)),
        };
        TrayvoraTheme.StyleControls(choice);
        var feedback = TrayvoraTheme.Hint("");
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex < 0) return;
            try
            {
                UiLanguage.Save(settingsDirectory, UiLanguage.Choices[choice.SelectedIndex].Code);
                feedback.Text = UiLanguage.Choices[choice.SelectedIndex].Code == UiLanguage.Current
                    ? "" : "Saved. Restart Trayvora (tray → Exit, then start it again) to switch the language. · Збережено. Перезапустіть Trayvora.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { feedback.Text = "Could not save the setting."; }
        };
        return TrayvoraTheme.Card(
            new TextBlock { Text = "Interface language", FontSize = 22, FontWeight = FontWeight.SemiBold },
            TrayvoraTheme.Field("Language", choice),
            TrayvoraTheme.Hint("Language of buttons and messages. Speech recognition language is chosen separately in the main window."),
            feedback);
    }

    /// <summary>Sessions: how many exchanges a "discuss with …" request may use.</summary>
    private static Control SessionsCard(DiscussionSettings? discussion)
    {
        if (discussion is null) return new Panel { IsVisible = false };
        var rounds = new NumericUpDown
        {
            Minimum = DiscussionOptions.MinRounds, Maximum = DiscussionOptions.MaxRounds, Increment = 1, FormatString = "F0",
            Value = discussion.Rounds, MinWidth = 130, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        TrayvoraTheme.StyleControls(rounds);
        Avalonia.Automation.AutomationProperties.SetName(rounds, "Discussion rounds");
        var feedback = TrayvoraTheme.Hint("");
        rounds.ValueChanged += (_, _) =>
        {
            if (rounds.Value is not { } value) return;
            try
            {
                discussion.SaveRounds((int)value);
                feedback.Text = $"Saved. The next discussion allows up to {(int)value} exchange{((int)value == 1 ? "" : "s")}.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                feedback.Text = "Could not save the setting.";
            }
        };
        return TrayvoraTheme.Card(
            new TextBlock { Text = "Sessions", FontSize = 22, FontWeight = FontWeight.SemiBold },
            TrayvoraTheme.Field("Discussion rounds", rounds),
            TrayvoraTheme.Hint($"How many times the leading agent may send its plan to the other agent after \"обсуди с Клодом\" / \"discuss with Codex\" ({DiscussionOptions.MinRounds}–{DiscussionOptions.MaxRounds}). After the last round it must send you the final answer."),
            feedback);
    }

    public SettingsWindow(VoiceHotkeys initial, Func<VoiceHotkeys, string?> apply, CodexSession codex, SpeechSettings speech, Action changed,
        DiscussionSettings? discussion = null, string? settingsDirectory = null)
    {
        Title = "Trayvora · Settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        TrayvoraTheme.Apply(this);
        var mcpButton = new Button { Content = "Read MCP configuration" };
        TrayvoraTheme.StyleControls(mcpButton);
        var mcpStatus = new TextBlock { Text = "Codex profile: read-only", TextWrapping = TextWrapping.Wrap };
        mcpButton.Click += async (_, _) =>
        {
            mcpButton.IsEnabled = false;
            try
            {
                var servers = await codex.ConfiguredMcpServersAsync();
                mcpStatus.Text = servers.Count == 0
                    ? "No MCP servers configured in the Trayvora Codex profile."
                    : string.Join(Environment.NewLine, servers);
            }
            catch (Exception)
            {
                mcpStatus.Text = "Could not read MCP configuration. Check the profile and wait for any running turn.";
            }
            finally
            {
                mcpButton.IsEnabled = true;
            }
        };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                Close();
                args.Handled = true;
            }
        };
        MaxHeight = 860;
        // Keep the whole window, title bar included, inside the screen's work area so it can always be closed.
        Opened += (_, _) =>
        {
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen) return;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            MaxHeight = Math.Min(860, area.Height / scale - 60);
            var width = (int)(Bounds.Width * scale);
            var height = (int)(Math.Min(Bounds.Height, MaxHeight) * scale) + 40;
            Position = new PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2), area.Y + Math.Max(0, (area.Height - height) / 2));
        };
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 10,
                Children = { LanguageCard(settingsDirectory), Appearance.Card(), TrayvoraTheme.Card(new VoiceSettingsPanel(initial, apply, speech, changed)), SessionsCard(discussion), KeyPhrases.Card(), TrayvoraTheme.Card(mcpButton, mcpStatus) },
            },
        };
    }
}
