using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace VoiceAgent.Desktop;

/// <summary>Font, size and colour of one kind of text; null means the built-in default.</summary>
internal sealed record TextLook(string? Font = null, double? Size = null, string? Color = null);

/// <summary>Text looks the user chose: chat replies, the text you type, and the interface (menus, buttons, labels).</summary>
internal sealed record AppearanceOptions(TextLook? Chat = null, TextLook? Input = null, TextLook? Interface = null, bool PlainReplies = false)
{
    public TextLook ChatLook => Chat ?? new();
    public TextLook InputLook => Input ?? new();
    public TextLook InterfaceLook => Interface ?? new();
}

/// <summary>
/// The theme part of Settings: keeps settings\appearance.json and applies it to every open window at once.
/// </summary>
internal static class Appearance
{
    public const double MinSize = 10, MaxSize = 28;
    public const string DefaultFont = "Segoe UI Variable Text, Segoe UI, Inter";
    public const double DefaultChatSize = 14, DefaultInputSize = 14, DefaultInterfaceSize = 13.5;
    public static readonly string DefaultColor = "#E9ECF5";

    /// <summary>Colours offered in the lists; any #RRGGBB can be typed as well.</summary>
    public static readonly (string Name, string? Hex)[] Colors =
    [
        ("Default", null), ("White", "#FFFFFF"), ("Soft grey", "#C9CED9"), ("Warm", "#F3E3C8"),
        ("Green", "#9FE0B8"), ("Blue", "#A9C8FF"), ("Yellow", "#FFE08A"), ("Pink", "#F6B3C8"),
    ];

    private static string? _file;
    private static Styles? _overrides;

    public static AppearanceOptions Current { get; private set; } = new();

    /// <summary>Raised after a change so text that sets its own size (chat replies) can redraw.</summary>
    public static event Action? Changed;

    public static void Load(string? settingsDirectory)
    {
        _file = settingsDirectory is null ? null : Path.Combine(settingsDirectory, "appearance.json");
        Current = Read(_file);
    }

    /// <summary>A missing, unreadable or damaged file gives the built-in look; it never stops Trayvora from starting.</summary>
    internal static AppearanceOptions Read(string? file)
    {
        try
        {
            return (file is null ? null : SettingsFile.Read<AppearanceOptions>(file)) is { } saved ? Clean(saved) : new();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new();
        }
    }

    public static void Save(AppearanceOptions options)
    {
        Current = Clean(options);
        if (_file is not null) SettingsFile.Write(_file, Current);
        ApplyEverywhere();
        Changed?.Invoke();
    }

    /// <summary>Drops sizes outside the allowed range and colours that are not #RRGGBB.</summary>
    internal static AppearanceOptions Clean(AppearanceOptions options)
    {
        static TextLook? One(TextLook? look) => look is null ? null : new(
            string.IsNullOrWhiteSpace(look.Font) || look.Font.Length > 100 ? null : look.Font.Trim(),
            look.Size is >= MinSize and <= MaxSize ? Math.Round(look.Size.Value * 2) / 2 : null,
            ParseColor(look.Color) is null ? null : look.Color!.Trim().ToUpperInvariant());
        return new(One(options.Chat), One(options.Input), One(options.Interface), options.PlainReplies);
    }

    internal static Color? ParseColor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit) && Avalonia.Media.Color.TryParse(hex, out var color) ? color : null;

    public static FontFamily Font(TextLook look) => new(look.Font ?? DefaultFont);
    public static IBrush Brush(TextLook look) => ParseColor(look.Color) is { } color ? new SolidColorBrush(color) : TrayvoraTheme.Text;
    public static double ChatSize => Current.ChatLook.Size ?? DefaultChatSize;

    /// <summary>Called once with the application styles; later styles win in Avalonia, so these override the theme.</summary>
    public static void Install(Styles applicationStyles)
    {
        _overrides = [];
        applicationStyles.Add(_overrides);
        Rebuild();
    }

    /// <summary>Window-wide interface font, size and colour.</summary>
    public static void Apply(Window window)
    {
        var look = Current.InterfaceLook;
        window.FontFamily = Font(look);
        window.FontSize = look.Size ?? DefaultInterfaceSize;
        window.Foreground = Brush(look);
    }

    private static void ApplyEverywhere()
    {
        Rebuild();
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var window in desktop.Windows) Apply(window);
    }

    private static void Rebuild()
    {
        if (_overrides is null) return;
        _overrides.Clear();
        var input = Current.InputLook;
        // Same selectors as the theme's own TextBox styles (plain, .field, .bare) so these win for every box.
        foreach (var inputStyle in new Style[] { new(s => s.OfType<TextBox>()), new(s => s.OfType<TextBox>().Class("field")), new(s => s.OfType<TextBox>().Class("bare")) })
        {
            if (input.Font is not null) inputStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, Font(input)));
            if (input.Size is { } inputSize) inputStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty, inputSize));
            if (input.Color is not null) inputStyle.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, Brush(input)));
            if (inputStyle.Setters.Count > 0) _overrides.Add(inputStyle);
        }

        var chat = Current.ChatLook;
        var chatStyle = new Style(s => s.OfType<MarkdownView>().Descendant().OfType<SelectableTextBlock>());
        if (chat.Font is not null) chatStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, Font(chat)));
        if (chat.Color is not null) chatStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush(chat)));
        if (chatStyle.Setters.Count > 0) _overrides.Add(chatStyle);

        var ui = Current.InterfaceLook;
        foreach (var menu in new Style[] { new(s => s.OfType<MenuItem>()), new(s => s.OfType<ComboBoxItem>()) })
        {
            if (ui.Font is not null) menu.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, Font(ui)));
            if (ui.Size is { } size) menu.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty, size));
            if (menu.Setters.Count > 0) _overrides.Add(menu);
        }
    }

    /// <summary>The Theme card of the Settings window.</summary>
    public static Control Card()
    {
        var fonts = new[] { "Default" }.Concat(FontManager.Current.SystemFonts.Select(font => font.Name).Distinct().Order()).ToArray();
        var feedback = TrayvoraTheme.Hint("");
        var rows = new StackPanel { Spacing = 14 };
        var plain = new CheckBox { Content = "Plain text replies (continuous mouse selection)", IsChecked = Current.PlainReplies };
        var editors = new List<Func<TextLook>>();
        var loading = true;

        void Changed()
        {
            if (loading) return;
            try
            {
                Save(new AppearanceOptions(editors[0](), editors[1](), editors[2](), plain.IsChecked == true));
                feedback.Text = "Saved and applied.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { feedback.Text = "Could not save the theme."; }
        }

        Func<TextLook> Editor(string title, string hint, TextLook look, double defaultSize)
        {
            var font = new ComboBox { ItemsSource = fonts, MinWidth = 200, SelectedIndex = Math.Max(0, Array.IndexOf(fonts, look.Font ?? "Default")) };
            var size = new NumericUpDown { Minimum = (decimal)MinSize, Maximum = (decimal)MaxSize, Increment = 0.5m, FormatString = "0.#", Value = (decimal)(look.Size ?? defaultSize), MinWidth = 110 };
            var color = new ComboBox { ItemsSource = Colors.Select(item => item.Name).ToArray(), MinWidth = 120 };
            var hex = new TextBox { Text = look.Color ?? "", PlaceholderText = "#RRGGBB", MaxLength = 7, Width = 100 };
            var preset = Array.FindIndex(Colors, item => string.Equals(item.Hex, look.Color, StringComparison.OrdinalIgnoreCase));
            color.SelectedIndex = look.Color is null ? 0 : preset;
            var sample = new TextBlock { Text = "The quick brown fox · Швидка руда лисиця", TextWrapping = TextWrapping.Wrap };
            void Preview()
            {
                var current = Read();
                sample.FontFamily = Font(current);
                sample.FontSize = current.Size ?? defaultSize;
                sample.Foreground = Brush(current);
            }
            TextLook Read() => new(
                font.SelectedIndex > 0 ? fonts[font.SelectedIndex] : null,
                size.Value is { } value && (double)value != defaultSize ? (double)value : null,
                ParseColor(hex.Text?.Trim()) is null ? null : hex.Text!.Trim());
            var reset = new Button { Content = "Default" };
            TrayvoraTheme.StyleControls(font, size, color, hex, reset);
            TrayvoraTheme.Ghost(reset);
            font.SelectionChanged += (_, _) => { Preview(); Changed(); };
            size.ValueChanged += (_, _) => { Preview(); Changed(); };
            color.SelectionChanged += (_, _) =>
            {
                if (color.SelectedIndex < 0) return;
                hex.Text = Colors[color.SelectedIndex].Hex ?? "";
            };
            hex.TextChanged += (_, _) =>
            {
                var text = hex.Text?.Trim() ?? "";
                if (text.Length != 0 && ParseColor(text) is null) return;
                Preview();
                Changed();
            };
            reset.Click += (_, _) =>
            {
                font.SelectedIndex = 0;
                size.Value = (decimal)defaultSize;
                color.SelectedIndex = 0;
                hex.Text = "";
            };
            Avalonia.Automation.AutomationProperties.SetName(font, title + " font");
            Avalonia.Automation.AutomationProperties.SetName(size, title + " size");
            Avalonia.Automation.AutomationProperties.SetName(hex, title + " colour");
            rows.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    TrayvoraTheme.Heading(title),
                    TrayvoraTheme.Hint(hint),
                    new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { font, size, color, hex, reset } },
                    sample,
                },
            });
            Preview();
            return Read;
        }

        editors.Add(Editor("Chat text", "Replies and messages in the main window and in Sessions.", Current.ChatLook, DefaultChatSize));
        editors.Add(Editor("Text you type", "Message boxes and other fields where you type.", Current.InputLook, DefaultInputSize));
        editors.Add(Editor("Interface and menus", "Labels, buttons, lists and right-click menus.", Current.InterfaceLook, DefaultInterfaceSize));
        plain.IsCheckedChanged += (_, _) => Changed();
        rows.Children.Add(plain);
        loading = false;
        return TrayvoraTheme.Card(
            new TextBlock { Text = "Theme", FontSize = 22, FontWeight = FontWeight.SemiBold },
            TrayvoraTheme.Hint("Font, size and colour for each kind of text. Changes apply at once; Default returns to the built-in look."),
            rows, feedback);
    }
}
