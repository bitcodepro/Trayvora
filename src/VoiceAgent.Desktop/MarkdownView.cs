using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;

namespace VoiceAgent.Desktop;

/// <summary>
/// Shows an agent reply written in Markdown: headings, emphasis, lists, quotes, tables, links and code blocks
/// with a Copy button. Raw HTML is shown as text. Calling <see cref="Show"/> again with a longer text (a reply
/// that is still being written) rebuilds only the blocks that changed.
/// </summary>
internal sealed class MarkdownView : StackPanel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly IBrush CodeBackground = Brush.Parse("#0B0D13");
    private static readonly IBrush CodeText = Brush.Parse("#D7DCEB");
    private static readonly IBrush InlineCodeText = Brush.Parse("#F2C79A");
    private static readonly Cursor Hand = new(StandardCursorType.Hand);

    private readonly List<(string Source, Control Control)> _blocks = [];
    private readonly double _baseSize;
    private double _size;
    private string? _text;

    /// <param name="fontSize">Size at the default theme; the Theme setting for chat text shifts it.</param>
    public MarkdownView(double fontSize = 13.5)
    {
        _baseSize = fontSize;
        _size = ThemedSize();
        Spacing = 8;
        AttachedToVisualTree += (_, _) => { Appearance.Changed += Restyle; Restyle(); };
        DetachedFromVisualTree += (_, _) => Appearance.Changed -= Restyle;
    }

    private double ThemedSize() => Math.Clamp(_baseSize + Appearance.ChatSize - Appearance.DefaultChatSize, Appearance.MinSize, Appearance.MaxSize);

    /// <summary>Redraws with the new chat text size.</summary>
    private void Restyle()
    {
        var size = ThemedSize();
        if (size == _size) return;
        _size = size;
        var text = _text;
        _text = null;
        Truncate(0);
        if (text is not null) Show(text);
    }

    /// <summary>Number of top-level blocks built by the last <see cref="Show"/> calls (for tests of incremental updates).</summary>
    internal int Rebuilt { get; private set; }

    private TextBox? _selectBox;

    /// <summary>True while the message is shown as one plain text box where any part can be selected.</summary>
    public bool Selecting => _selectBox is not null;

    /// <summary>Raised when select mode starts or ends, so the toolbar can update its button.</summary>
    public event Action? SelectingChanged;

    /// <summary>
    /// Switches between the formatted view and one read-only text box with the whole message. Formatted text is split
    /// into blocks (paragraphs, lists, code), and a selection cannot cross from one block to the next; the box can.
    /// </summary>
    public void ToggleSelecting(bool selectAll = false)
    {
        if (_selectBox is { } open)
        {
            Children.Remove(open);
            _selectBox = null;
            foreach (var (_, control) in _blocks) control.IsVisible = true;
            SelectingChanged?.Invoke();
            return;
        }
        var box = new TextBox
        {
            Text = _text ?? "", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            FontSize = _size, MinHeight = 0, CaretBrush = Brushes.Transparent,
            // Set here so the Theme setting for typed text does not restyle a message.
            FontFamily = Appearance.Font(Appearance.Current.ChatLook), Foreground = Appearance.Brush(Appearance.Current.ChatLook),
            LineHeight = LineHeight(_size),
        };
        box.Classes.Add("bare");
        box.Classes.Add("message-select");
        Avalonia.Automation.AutomationProperties.SetName(box, "Message text");
        box.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            ToggleSelecting();
        };
        foreach (var (_, control) in _blocks) control.IsVisible = false;
        _selectBox = box;
        Children.Add(box);
        SelectingChanged?.Invoke();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            if (selectAll) box.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    public void Show(string text)
    {
        if (_selectBox is { } selecting && text != _text) selecting.Text = text;
        if (text == _text) return;
        _text = text;
        var document = Markdown.Parse(text, Pipeline);
        var index = 0;
        foreach (var block in document)
        {
            if (block is LinkReferenceDefinitionGroup) continue;
            var source = Slice(text, block);
            if (index < _blocks.Count && _blocks[index].Source == source)
            {
                index++;
                continue;
            }
            Truncate(index);
            var control = Render(block);
            _blocks.Add((source, control));
            control.IsVisible = _selectBox is null;
            Children.Insert(_selectBox is { } box ? Children.IndexOf(box) : Children.Count, control);
            Rebuilt++;
            index++;
        }
        Truncate(index);
    }

    private void Truncate(int count)
    {
        while (_blocks.Count > count)
        {
            Children.Remove(_blocks[^1].Control);
            _blocks.RemoveAt(_blocks.Count - 1);
        }
    }

    private static string Slice(string text, Block block)
    {
        var start = Math.Clamp(block.Span.Start, 0, text.Length);
        var end = Math.Clamp(block.Span.End + 1, start, text.Length);
        return text[start..end];
    }

    // ── Blocks ───────────────────────────────────────────────────────────

    private Control Render(Block block) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => TextOf(paragraph.Inline),
        FencedCodeBlock fenced => Code(fenced.Lines.ToString(), fenced.Info),
        CodeBlock code => Code(code.Lines.ToString(), null),
        QuoteBlock quote => Quote(quote),
        ListBlock list => List(list),
        ThematicBreakBlock => new Border { Height = 1, Background = TrayvoraTheme.Border, Margin = new Thickness(0, 4) },
        Table table => TableOf(table),
        ContainerBlock container => Container(container, 8),
        LeafBlock leaf when leaf.Inline is not null => TextOf(leaf.Inline),
        LeafBlock leaf => Plain(leaf.Lines.ToString()),
        _ => Plain(""),
    };

    private StackPanel Container(ContainerBlock container, double spacing)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in container)
            if (child is not LinkReferenceDefinitionGroup) panel.Children.Add(Render(child));
        return panel;
    }

    private SelectableTextBlock Plain(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = _size, LineHeight = LineHeight(_size),
    };

    private static double LineHeight(double size) => Math.Round(size * 1.48);

    private Control Heading(HeadingBlock heading)
    {
        var size = _size + heading.Level switch { 1 => 5, 2 => 3, 3 => 1.5, _ => 0.5 };
        var text = TextOf(heading.Inline, size);
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = new Thickness(0, 4, 0, 0);
        return text;
    }

    private Control Quote(QuoteBlock quote) => new Border
    {
        BorderBrush = TrayvoraTheme.Faint,
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(12, 2, 0, 2),
        Opacity = .88,
        Child = Container(quote, 8),
    };

    private Control List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = list.IsLoose ? 8 : 3 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var task = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList };
            var marker = new TextBlock
            {
                Text = task ? "" : list.IsOrdered ? $"{number}." : "•",
                Foreground = TrayvoraTheme.Muted, FontSize = _size, LineHeight = LineHeight(_size),
                MinWidth = task ? 0 : 14, TextAlignment = TextAlignment.Right,
            };
            number++;
            var body = Container(item, 4);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = task ? 0 : 8 };
            Grid.SetColumn(body, 1);
            row.Children.AddRange([marker, body]);
            panel.Children.Add(row);
        }
        return panel;
    }

    private Control Code(string code, string? language)
    {
        code = code.TrimEnd('\r', '\n');
        var text = new SelectableTextBlock
        {
            Text = code, FontFamily = Mono, FontSize = _size - 0.5, LineHeight = Math.Round(_size * 1.42),
            TextWrapping = TextWrapping.NoWrap, Foreground = CodeText,
        };
        var label = new TextBlock { Text = "Copy", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        var copy = new Button
        {
            Padding = new Thickness(7, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5,
                Children = { new TextBlock { Text = TrayvoraTheme.IconCopy, FontFamily = IconFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, label },
            },
        };
        TrayvoraTheme.Ghost(copy);
        Avalonia.Automation.AutomationProperties.SetName(copy, "Copy code");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is not { } clipboard) return;
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, code);
            label.Text = "Copied";
        };
        copy.PointerExited += (_, _) => label.Text = "Copy";

        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "code" : language.Trim(), FontSize = 11, Foreground = TrayvoraTheme.Faint,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 4, 4, 0) };
        Grid.SetColumn(copy, 1);
        header.Children.AddRange([name, copy]);
        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 2, 12, 12), Content = text,
        };
        return new Border
        {
            Background = CodeBackground, BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Child = new StackPanel { Children = { header, body } },
        };
    }

    private Control TableOf(Table table)
    {
        var rows = table.OfType<TableRow>().ToArray();
        var columns = rows.Length == 0 ? 0 : rows.Max(row => row.Count);
        var grid = new Grid();
        for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var index = 0; index < rows.Length; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var row = rows[index];
            for (var column = 0; column < row.Count; column++)
            {
                if (row[column] is not TableCell cell) continue;
                var content = Container(cell, 4);
                content.MaxWidth = 380;
                var box = new Border
                {
                    BorderBrush = TrayvoraTheme.Border,
                    BorderThickness = new Thickness(0, 0, column < columns - 1 ? 1 : 0, index < rows.Length - 1 ? 1 : 0),
                    Padding = new Thickness(10, 6),
                    Background = row.IsHeader ? TrayvoraTheme.Elevated : null,
                    Child = content,
                };
                if (row.IsHeader) TextElement.SetFontWeight(box, FontWeight.SemiBold);
                Grid.SetRow(box, index);
                Grid.SetColumn(box, column);
                grid.Children.Add(box);
            }
        }
        return new Border
        {
            BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = grid,
            },
        };
    }

    // ── Inline text ──────────────────────────────────────────────────────

    private sealed class LinkMap
    {
        public int Position;
        public List<(int Start, int End, string Url)> Links { get; } = [];
    }

    private SelectableTextBlock TextOf(ContainerInline? inline, double? size = null)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = size ?? _size, LineHeight = LineHeight(size ?? _size),
        };
        if (inline is null) return block;
        // Text without formatting stays a plain string: cheaper, and simple replies read the same as before.
        if (PlainText(inline) is { } plain)
        {
            block.Text = plain;
            return block;
        }
        var map = new LinkMap();
        Add(block.Inlines ??= [], inline, map);
        if (map.Links.Count > 0) AttachLinks(block, map.Links);
        return block;
    }

    private static string? PlainText(ContainerInline container)
    {
        var text = new System.Text.StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case LineBreakInline: text.Append('\n'); break;
                default: return null;
            }
        }
        return text.ToString();
    }

    private void Add(InlineCollection target, ContainerInline container, LinkMap map)
    {
        // Line breaks are written as "\n" runs so every character position is known for link hit-testing.
        void Text(string text, Action<Run>? style = null)
        {
            if (text.Length == 0) return;
            var run = new Run(text);
            style?.Invoke(run);
            target.Add(run);
            map.Position += text.Length;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Text(literal.Content.ToString());
                    break;
                case LineBreakInline:
                    Text("\n");
                    break;
                case CodeInline code:
                    Text(code.Content, run =>
                    {
                        run.FontFamily = Mono;
                        run.Foreground = InlineCodeText;
                        run.Background = TrayvoraTheme.Elevated;
                    });
                    break;
                case TaskList task:
                    Text(task.Checked ? "☑ " : "☐ ");
                    break;
                case AutolinkInline auto:
                {
                    var start = map.Position;
                    var url = SafeUrl(auto.IsEmail ? null : auto.Url);
                    Text(auto.Url, run =>
                    {
                        if (url is null) return;
                        run.Foreground = TrayvoraTheme.Accent;
                        run.TextDecorations = TextDecorations.Underline;
                    });
                    if (url is not null) map.Links.Add((start, map.Position, url));
                    break;
                }
                case LinkInline { IsImage: true } image:
                    Text("[image");
                    if (image.FirstChild is not null)
                    {
                        Text(": ");
                        Add(target, image, map);
                    }
                    Text("]");
                    break;
                case LinkInline link:
                {
                    var start = map.Position;
                    var url = SafeUrl(link.Url);
                    var span = new Span();
                    if (url is not null)
                    {
                        span.Foreground = TrayvoraTheme.Accent;
                        span.TextDecorations = TextDecorations.Underline;
                    }
                    Add(span.Inlines, link, map);
                    target.Add(span);
                    if (url is not null && map.Position > start) map.Links.Add((start, map.Position, url));
                    break;
                }
                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeight.SemiBold;
                    else span.FontStyle = FontStyle.Italic;
                    Add(span.Inlines, emphasis, map);
                    target.Add(span);
                    break;
                }
                case HtmlEntityInline entity:
                    Text(entity.Transcoded.ToString());
                    break;
                case ContainerInline nested:
                    Add(target, nested, map);
                    break;
                default:
                    Text(Literal(inline));
                    break;
            }
        }
    }

    private static string Literal(MarkdownInline inline) => inline.ToString() ?? "";

    /// <summary>Only web links open; file paths and other schemes stay plain text.</summary>
    internal static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri : null;

    private static void AttachLinks(SelectableTextBlock block, List<(int Start, int End, string Url)> links)
    {
        string? shown = null;

        string? At(PointerEventArgs args)
        {
            var point = args.GetPosition(block);
            var hit = block.TextLayout.HitTestPoint(new Point(point.X - block.Padding.Left, point.Y - block.Padding.Top));
            if (!hit.IsInside) return null;
            foreach (var link in links)
                if (hit.TextPosition >= link.Start && hit.TextPosition < link.End) return link.Url;
            return null;
        }

        block.PointerMoved += (_, args) =>
        {
            var url = At(args);
            if (url == shown) return;
            shown = url;
            if (url is null) block.ClearValue(CursorProperty);
            else block.Cursor = Hand;
            ToolTip.SetTip(block, url);
        };
        block.PointerReleased += (_, args) =>
        {
            if (args.InitialPressMouseButton != MouseButton.Left || !string.IsNullOrEmpty(block.SelectedText)) return;
            if (At(args) is { } url && TopLevel.GetTopLevel(block)?.Launcher is { } launcher)
                _ = launcher.LaunchUriAsync(new Uri(url));
        };
    }
}
