using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;

namespace VoiceAgent.Desktop;

internal sealed class TranscriptPreview : Window
{
    private readonly TextBox _editor = new()
    {
        PlaceholderText = "Transcript",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 80,
        MaxHeight = 190,
        MaxLength = 16000,
        IsVisible = false,
    };
    private readonly TextBlock _instructions = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _copy = new() { Content = "Copy text", IsVisible = false };
    private readonly Button _send = new() { Content = "Send to Codex", IsVisible = false };

    public TranscriptPreview(Action send)
    {
        Title = "Trayvora transcript";
        TrayvoraTheme.Apply(this);
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 560;
        SizeToContent = SizeToContent.Height;

        TrayvoraTheme.Primary(_send);
        _send.Click += (_, _) => send();
        _copy.Click += async (_, _) =>
        {
            try
            {
                if (Clipboard is { } clipboard && _editor.Text is { } text)
                {
                    await clipboard.SetTextAsync(text);
                    _instructions.Text = "Copied. Paste into your Codex chat.";
                }
            }
            catch (Exception)
            {
                _instructions.Text = "Could not copy. You can select and copy the text manually.";
            }
        };
        var dismiss = new Button { Content = "Close" };
        dismiss.Click += (_, _) => Hide();
        var footer = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children = { _send, _copy, dismiss },
        };
        Content = new Border
        {
            BorderBrush = Brush.Parse("#54658F"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Voice transcript", FontSize = 17, FontWeight = FontWeight.SemiBold },
                    _editor,
                    _instructions,
                    footer,
                },
            },
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Hide();
            args.Handled = true;
        };
    }

    public void ShowStatus(string status)
    {
        _editor.Clear();
        _editor.IsReadOnly = false;
        _editor.IsVisible = _send.IsVisible = _copy.IsVisible = false;
        _instructions.Text = status;
        ShowAtTop(activate: false);
    }

    public void ShowDraft(string text, string? shortcut, string? desktopTarget = null, bool question = false)
    {
        _editor.Text = text;
        _editor.IsReadOnly = false;
        _editor.IsVisible = _send.IsVisible = true;
        _copy.IsVisible = false;
        _send.Content = question ? "Ask" : desktopTarget is null ? "Send to Codex" : "Send to Desktop";
        var instructions = shortcut is null ? "Esc to close" : $"{shortcut} to send · Esc to close";
        _instructions.Text = desktopTarget is null ? instructions : $"{desktopTarget} · {instructions}";
        ShowAtTop(activate: true);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || !_editor.IsVisible) return;
            _editor.Focus();
            _editor.CaretIndex = _editor.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    public void ShowAnswer(string text)
    {
        _editor.Text = text;
        _editor.IsReadOnly = true;
        _editor.IsVisible = true;
        _send.IsVisible = _copy.IsVisible = false;
        _instructions.Text = "Answer · GPT-6 Luna";
        ShowAtTop(activate: false);
    }

    public void ShowDictationDraft(string text)
    {
        _editor.Text = text;
        _editor.IsReadOnly = false;
        _editor.IsVisible = _copy.IsVisible = true;
        _send.IsVisible = false;
        _instructions.Text = "The original Codex input is unavailable. Copy the draft and paste it into your chat.";
        ShowAtTop(activate: false);
    }

    private void ShowAtTop(bool activate)
    {
        var screen = IsVisible ? Screens.ScreenFromWindow(this) : Screens.ScreenFromPoint(GetPointerPosition());
        screen ??= Screens.Primary;
        if (screen is not null)
        {
            var work = screen.WorkingArea;
            Width = Math.Min(560, work.Width / screen.Scaling - 24);
            Position = new PixelPoint(work.X + (work.Width - (int)(Width * screen.Scaling)) / 2,
                work.Y + (int)(12 * screen.Scaling));
        }

        ShowActivated = activate;
        if (!IsVisible) Show();
        if (activate) Activate();
    }

    private static PixelPoint GetPointerPosition() =>
        OperatingSystem.IsWindows() && GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : default;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    public bool HasDraft => IsVisible && _editor.IsVisible && !string.IsNullOrWhiteSpace(_editor.Text);

    public string? TakeDraft()
    {
        if (!HasDraft || !_send.IsVisible) return null;
        var draft = _editor.Text;
        _editor.Clear();
        Hide();
        return draft;
    }
}
