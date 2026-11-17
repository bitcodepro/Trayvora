using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace VoiceAgent.Desktop;

internal static class TrayvoraTheme
{
    private static IBrush Color(string value) => Brush.Parse(value);

    public static readonly IBrush Background = Color("#0E1016");
    public static readonly IBrush Sidebar = Color("#12141C");
    public static readonly IBrush Surface = Color("#171A24");
    public static readonly IBrush Elevated = Color("#1F2330");
    public static readonly IBrush Hover = Color("#272C3B");
    public static readonly IBrush Border = Color("#2A2F3E");
    public static readonly IBrush Text = Color("#E9ECF5");
    public static readonly IBrush Muted = Color("#8E96AB");
    public static readonly IBrush Faint = Color("#5E667A");
    public static readonly IBrush Accent = Color("#7C8CFF");
    public static readonly IBrush AccentSoft = Color("#262B4D");
    public static readonly IBrush Danger = Color("#F0627A");
    public static readonly IBrush Success = Color("#4FD1A1");
    public static readonly IBrush Warning = Color("#F5B65C");

    public static readonly IBrush CodexColor = Color("#6EA8FF");
    public static readonly IBrush ClaudeColor = Color("#F19A6B");
    public static readonly IBrush BossColor = Color("#4FD1A1");

    private static Selector Presenter(Func<Selector?, Selector> button) =>
        button(null).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");

    private static void ButtonVariant(Styles styles, string cls, string bg, string fg, string border, string hover, string pressed)
    {
        styles.Add(new Style(s => s.OfType<Button>().Class("surface").Class(cls))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, Color(bg)),
                new Setter(Button.ForegroundProperty, Color(fg)),
                new Setter(Button.BorderBrushProperty, Color(border)),
            },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("surface").Class(cls).Class(":pointerover")))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Color(hover)),
                new Setter(ContentPresenter.BorderBrushProperty, Color(border)),
                new Setter(ContentPresenter.ForegroundProperty, Color(fg)),
            },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("surface").Class(cls).Class(":pressed")))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Color(pressed)) },
        });
    }

    public static void AddStyles(Styles styles)
    {
        // Base button
        styles.Add(new Style(s => s.OfType<Button>().Class("surface"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, Elevated),
                new Setter(Button.ForegroundProperty, Text),
                new Setter(Button.BorderBrushProperty, Border),
                new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                new Setter(Button.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(Button.PaddingProperty, new Thickness(14, 8)),
                new Setter(Button.MinHeightProperty, 36d),
                new Setter(Button.FontSizeProperty, 13d),
                new Setter(Button.FontWeightProperty, FontWeight.Medium),
                new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
            },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("surface")))
        {
            Setters =
            {
                new Setter(ContentPresenter.TransitionsProperty, new Transitions
                {
                    new BrushTransition { Property = ContentPresenter.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(120), Easing = new CubicEaseOut() },
                }),
            },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("surface").Class(":pointerover")))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Hover),
                new Setter(ContentPresenter.BorderBrushProperty, Color("#3A4054")),
                new Setter(ContentPresenter.ForegroundProperty, Text),
            },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("surface").Class(":pressed")))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Color("#2F3546")) },
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("surface").Class(":disabled"))
        {
            Setters = { new Setter(Button.OpacityProperty, .45) },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("surface").Class(":disabled")))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Elevated),
                new Setter(ContentPresenter.ForegroundProperty, Muted),
            },
        });

        ButtonVariant(styles, "primary", "#7C8CFF", "#0E1130", "#7C8CFF", "#919FFF", "#6B7AF0");
        ButtonVariant(styles, "ghost", "#00000000", "#C9CEDD", "#00000000", "#1F2330", "#272C3B");
        ButtonVariant(styles, "danger", "#3A1C26", "#FF9DAE", "#5A2635", "#4A2230", "#552838");
        ButtonVariant(styles, "recording", "#F0627A", "#FFFFFF", "#F0627A", "#F47A8E", "#E0566E");
        styles.Add(new Style(s => s.OfType<Button>().Class("surface").Class("primary"))
        {
            Setters = { new Setter(Button.FontWeightProperty, FontWeight.SemiBold) },
        });

        // Round icon button (mic)
        styles.Add(new Style(s => s.OfType<Button>().Class("surface").Class("round"))
        {
            Setters =
            {
                new Setter(Button.CornerRadiusProperty, new CornerRadius(22)),
                new Setter(Button.WidthProperty, 44d),
                new Setter(Button.HeightProperty, 44d),
                new Setter(Button.PaddingProperty, new Thickness(0)),
                new Setter(Button.FontSizeProperty, 17d),
            },
        });

        // Sidebar navigation item
        styles.Add(new Style(s => s.OfType<Button>().Class("nav"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, Brushes.Transparent),
                new Setter(Button.ForegroundProperty, Color("#B7BDCD")),
                new Setter(Button.BorderThicknessProperty, new Thickness(0)),
                new Setter(Button.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(Button.PaddingProperty, new Thickness(12, 9)),
                new Setter(Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("nav").Class(":pointerover")))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Elevated),
                new Setter(ContentPresenter.ForegroundProperty, Text),
            },
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("nav").Class("selected"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, AccentSoft),
                new Setter(Button.ForegroundProperty, Text),
            },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("nav").Class("selected").Class(":pointerover")))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Color("#2D3360")) },
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("nav").Class(":disabled"))
        {
            Setters = { new Setter(Button.OpacityProperty, .5) },
        });

        // Chip (toggle-like pill)
        styles.Add(new Style(s => s.OfType<Button>().Class("chip"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, Elevated),
                new Setter(Button.ForegroundProperty, Muted),
                new Setter(Button.BorderBrushProperty, Border),
                new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                new Setter(Button.CornerRadiusProperty, new CornerRadius(14)),
                new Setter(Button.PaddingProperty, new Thickness(12, 4)),
                new Setter(Button.MinHeightProperty, 28d),
                new Setter(Button.FontSizeProperty, 12.5),
                new Setter(Button.FontWeightProperty, FontWeight.Medium),
            },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("chip").Class(":pointerover")))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Hover),
                new Setter(ContentPresenter.ForegroundProperty, Text),
                new Setter(ContentPresenter.BorderBrushProperty, Color("#3A4054")),
            },
        });
        styles.Add(new Style(s => s.OfType<Button>().Class("chip").Class("selected"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, AccentSoft),
                new Setter(Button.ForegroundProperty, Text),
                new Setter(Button.BorderBrushProperty, Accent),
            },
        });
        styles.Add(new Style(_ => Presenter(s => s!.OfType<Button>().Class("chip").Class("selected").Class(":pointerover")))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Color("#2D3360")),
                new Setter(ContentPresenter.BorderBrushProperty, Accent),
            },
        });

        // Text fields
        styles.Add(new Style(s => s.OfType<TextBox>().Class("field"))
        {
            Setters =
            {
                new Setter(TextBox.BackgroundProperty, Color("#12151E")),
                new Setter(TextBox.ForegroundProperty, Text),
                new Setter(TextBox.BorderBrushProperty, Border),
                new Setter(TextBox.BorderThicknessProperty, new Thickness(1)),
                new Setter(TextBox.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(TextBox.PaddingProperty, new Thickness(12, 9)),
                new Setter(TextBox.FontSizeProperty, 13.5),
                new Setter(TextBox.CaretBrushProperty, Accent),
                new Setter(TextBox.SelectionBrushProperty, Color("#3D4785")),
            },
        });
        styles.Add(new Style(s => s.OfType<TextBox>().Class("field").Class(":pointerover")
            .Template().OfType<Border>().Name("PART_BorderElement"))
        {
            Setters =
            {
                new Setter(Avalonia.Controls.Border.BackgroundProperty, Color("#151924")),
                new Setter(Avalonia.Controls.Border.BorderBrushProperty, Color("#3A4054")),
            },
        });
        styles.Add(new Style(s => s.OfType<TextBox>().Class("field").Class(":focus")
            .Template().OfType<Border>().Name("PART_BorderElement"))
        {
            Setters =
            {
                new Setter(Avalonia.Controls.Border.BackgroundProperty, Color("#12151E")),
                new Setter(Avalonia.Controls.Border.BorderBrushProperty, Accent),
                new Setter(Avalonia.Controls.Border.BorderThicknessProperty, new Thickness(1)),
            },
        });
        // Borderless composer text box
        styles.Add(new Style(s => s.OfType<TextBox>().Class("bare"))
        {
            Setters =
            {
                new Setter(TextBox.BackgroundProperty, Brushes.Transparent),
                new Setter(TextBox.BorderThicknessProperty, new Thickness(0)),
                new Setter(TextBox.PaddingProperty, new Thickness(4, 6)),
                new Setter(TextBox.FontSizeProperty, 14d),
                new Setter(TextBox.CaretBrushProperty, Accent),
                new Setter(TextBox.SelectionBrushProperty, Color("#3D4785")),
            },
        });
        foreach (var state in new[] { ":pointerover", ":focus" })
            styles.Add(new Style(s => s.OfType<TextBox>().Class("bare").Class(state)
                .Template().OfType<Border>().Name("PART_BorderElement"))
            {
                Setters =
                {
                    new Setter(Avalonia.Controls.Border.BackgroundProperty, Brushes.Transparent),
                    new Setter(Avalonia.Controls.Border.BorderThicknessProperty, new Thickness(0)),
                },
            });

        styles.Add(new Style(s => s.OfType<ComboBox>().Class("field"))
        {
            Setters =
            {
                new Setter(ComboBox.BackgroundProperty, Elevated),
                new Setter(ComboBox.ForegroundProperty, Text),
                new Setter(ComboBox.BorderBrushProperty, Border),
                new Setter(ComboBox.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(ComboBox.PaddingProperty, new Thickness(12, 7)),
                new Setter(ComboBox.MinHeightProperty, 36d),
                new Setter(ComboBox.FontSizeProperty, 13d),
                new Setter(ComboBox.HorizontalAlignmentProperty, HorizontalAlignment.Stretch),
            },
        });
        styles.Add(new Style(s => s.OfType<ComboBox>().Class("field").Class(":pointerover")
            .Template().OfType<Border>().Name("Background"))
        {
            Setters =
            {
                new Setter(Avalonia.Controls.Border.BackgroundProperty, Hover),
                new Setter(Avalonia.Controls.Border.BorderBrushProperty, Color("#3A4054")),
            },
        });

        styles.Add(new Style(s => s.OfType<NumericUpDown>().Class("field"))
        {
            Setters =
            {
                new Setter(NumericUpDown.BackgroundProperty, Elevated),
                new Setter(NumericUpDown.ForegroundProperty, Text),
                new Setter(NumericUpDown.BorderBrushProperty, Border),
                new Setter(NumericUpDown.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(NumericUpDown.MinHeightProperty, 36d),
            },
        });
        styles.Add(new Style(s => s.OfType<Expander>())
        {
            Setters =
            {
                new Setter(Expander.BackgroundProperty, Surface),
                new Setter(Expander.BorderBrushProperty, Border),
                new Setter(Expander.CornerRadiusProperty, new CornerRadius(12)),
                new Setter(Expander.HorizontalAlignmentProperty, HorizontalAlignment.Stretch),
            },
        });
        styles.Add(new Style(s => s.OfType<Expander>().Template().OfType<ToggleButton>().Name("ExpanderHeader"))
        {
            Setters =
            {
                new Setter(ToggleButton.BackgroundProperty, Surface),
                new Setter(ToggleButton.ForegroundProperty, Text),
                new Setter(ToggleButton.BorderBrushProperty, Border),
                new Setter(ToggleButton.MinHeightProperty, 46d),
                new Setter(ToggleButton.FontWeightProperty, FontWeight.Medium),
            },
        });
        foreach (var state in new[] { ":pointerover", ":pressed", ":checked" })
            styles.Add(new Style(s => s.OfType<Expander>().Template().OfType<ToggleButton>().Name("ExpanderHeader").Class(state)
                .Template().OfType<Avalonia.Controls.Border>().Name("ToggleButtonBackground"))
            {
                Setters =
                {
                    new Setter(Avalonia.Controls.Border.BackgroundProperty, state == ":checked" ? Surface : Elevated),
                    new Setter(Avalonia.Controls.Border.BorderBrushProperty, Border),
                },
            });
        styles.Add(new Style(s => s.OfType<Expander>().Template().OfType<Avalonia.Controls.Border>().Name("ExpanderContent"))
        {
            Setters =
            {
                new Setter(Avalonia.Controls.Border.BackgroundProperty, Color("#141720")),
                new Setter(Avalonia.Controls.Border.BorderBrushProperty, Border),
            },
        });
        styles.Add(new Style(s => s.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(ListBoxItem.PaddingProperty, new Thickness(10, 7)),
                new Setter(ListBoxItem.MarginProperty, new Thickness(0, 1)),
            },
        });
        foreach (var (state, brush) in new[] { (":pointerover", Elevated), (":selected", AccentSoft), (":selected:pointerover", Color("#2D3360")) })
            styles.Add(new Style(s => (state switch
            {
                ":pointerover" => s.OfType<ListBoxItem>().Class(":pointerover"),
                ":selected" => s.OfType<ListBoxItem>().Class(":selected"),
                _ => s.OfType<ListBoxItem>().Class(":selected").Class(":pointerover"),
            }).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, brush),
                    new Setter(ContentPresenter.ForegroundProperty, Text),
                },
            });
        styles.Add(new Style(s => s.OfType<Label>())
        {
            Setters =
            {
                new Setter(Label.ForegroundProperty, Muted),
                new Setter(Label.FontSizeProperty, 12d),
                new Setter(Label.FontWeightProperty, FontWeight.Medium),
                new Setter(Label.PaddingProperty, new Thickness(2, 0, 0, 2)),
            },
        });
        styles.Add(new Style(s => s.OfType<ProgressBar>())
        {
            Setters =
            {
                new Setter(ProgressBar.ForegroundProperty, Accent),
                new Setter(ProgressBar.BackgroundProperty, Elevated),
                new Setter(ProgressBar.MinHeightProperty, 4d),
                new Setter(ProgressBar.HeightProperty, 4d),
                new Setter(ProgressBar.CornerRadiusProperty, new CornerRadius(2)),
            },
        });
        styles.Add(new Style(s => s.OfType<ScrollBar>())
        {
            Setters = { new Setter(ScrollBar.OpacityProperty, .7) },
        });
    }

    public static void Apply(Window window)
    {
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Background = Background;
        window.Foreground = Text;
        window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Inter");
        window.FontSize = 13.5;
        if (AppIcon is { } icon) window.Icon = icon;
    }

    /// <summary>The Trayvora icon (all sizes) for windows and the taskbar.</summary>
    public static WindowIcon? AppIcon { get; } = LoadIcon();

    private static WindowIcon? LoadIcon()
    {
        using var stream = typeof(TrayvoraTheme).Assembly.GetManifestResourceStream("VoiceAgent.Desktop.Assets.trayvora.ico");
        return stream is null ? null : new WindowIcon(stream);
    }

    public static void Primary(Button button)
    {
        if (!button.Classes.Contains("surface")) button.Classes.Add("surface");
        button.Classes.Add("primary");
    }

    public static void Ghost(Button button)
    {
        if (!button.Classes.Contains("surface")) button.Classes.Add("surface");
        button.Classes.Add("ghost");
    }

    public static void DangerButton(Button button)
    {
        if (!button.Classes.Contains("surface")) button.Classes.Add("surface");
        button.Classes.Add("danger");
    }

    public static void StyleControls(params Control[] controls)
    {
        foreach (var control in controls)
        {
            if (control is Button && !control.Classes.Contains("surface") && !control.Classes.Contains("nav") && !control.Classes.Contains("chip"))
                control.Classes.Add("surface");
            if (control is TextBox or ComboBox or NumericUpDown && !control.Classes.Contains("bare")) control.Classes.Add("field");
        }
    }

    public static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15 };

    public static TextBlock Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold,
        Foreground = Faint, LetterSpacing = 0.8, Margin = new Thickness(4, 0, 0, 0),
    };

    public static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
    };

    public static Border Card(params Control[] controls)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.AddRange(controls);
        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = Surface,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Child = stack,
        };
    }

    public static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange(controls);
        return row;
    }

    public static WrapPanel Wrap(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        panel.Children.AddRange(controls);
        return panel;
    }

    public static Control Field(string label, Control input) =>
        new StackPanel { Spacing = 4, Children = { new Label { Content = label, Target = input }, input } };

    public static Border Avatar(string initials, IBrush color, double size = 32) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
        Background = color,
        VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock
        {
            Text = initials, FontSize = size * .38, FontWeight = FontWeight.Bold,
            Foreground = Color("#0E1016"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    public static Border Dot(IBrush color, double size = 8) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = color,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Divider() => new() { Height = 1, Background = Border, Margin = new Thickness(0, 4) };

    /// <summary>Sidebar navigation button with an icon glyph and label.</summary>
    public static Button NavItem(string glyph, string label, string? hint = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), };
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13.5, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        if (hint is not null)
            text.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.AddRange([icon, text]);
        var button = new Button { Content = grid };
        button.Classes.Add("nav");
        AutomationHelper(button, label);
        return button;
    }

    public static Button Chip(string label)
    {
        var button = new Button { Content = label };
        button.Classes.Add("chip");
        return button;
    }

    public static Border Pill(TextBlock text, IBrush dot) => Pill(text, Dot(dot, 7));

    public static Border Pill(TextBlock text, Border dot)
    {
        text.FontSize = 12;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        return new Border
        {
            Background = Elevated, BorderBrush = Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, text } },
        };
    }

    public static void SetSelected(Control control, bool selected)
    {
        if (selected && !control.Classes.Contains("selected")) control.Classes.Add("selected");
        if (!selected) control.Classes.Remove("selected");
    }

    private static void AutomationHelper(Control control, string name) =>
        Avalonia.Automation.AutomationProperties.SetName(control, name);

    public const string IconMic = "";
    public const string IconStop = "";
    public const string IconSend = "";
    public const string IconCode = "";
    public const string IconChat = "";
    public const string IconBrain = "";
    public const string IconMemory = "";
    public const string IconSpark = "";
    public const string IconGroup = "";
    public const string IconHistory = "";
    public const string IconSettings = "";
    public const string IconCopy = "";
    public const string IconClear = "";
    public const string IconPause = "";
}
