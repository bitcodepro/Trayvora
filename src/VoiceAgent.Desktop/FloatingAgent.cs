using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;

namespace VoiceAgent.Desktop;

internal enum AgentState { Ready, Recording, Transcribing, Sending, Busy }

internal sealed class FloatingAgent : Window
{
    private static readonly Geometry Speaker = Geometry.Parse("M3 9 H7 L12 5 V19 L7 15 H3 Z M15 9 Q19 12 15 15 M18 6 Q25 12 18 18");
    private static readonly Geometry Stop = Geometry.Parse("M7 7 H17 V17 H7 Z");
    private static readonly Geometry Wave = Geometry.Parse("M2 12 H6 L9 5 L13 19 L16 9 L18 12 H22");
    private static readonly Geometry Send = Geometry.Parse("M4 11 L21 4 L16 21 L12 13 L4 11 Z M12 13 L21 4");
    private static readonly Geometry Busy = Geometry.Parse("M12 3 A9 9 0 1 1 3 12");

    private readonly Border _ring = new() { CornerRadius = new CornerRadius(31), BorderThickness = new Thickness(1) };
    private readonly Border _face = new() { CornerRadius = new CornerRadius(26), Margin = new Thickness(5) };
    private readonly Avalonia.Controls.Shapes.Path _glyph = new()
    {
        Width = 25, Height = 25, Stretch = Stretch.Uniform,
        Stroke = Brushes.White, StrokeThickness = 1.9,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
    private readonly Button _button;
    private readonly DispatcherTimer _animation;
    private AgentState? _state;

    public FloatingAgent(Func<Task> toggle)
    {
        Title = "Trayvora floating agent";
        Width = Height = 64;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        _face.Child = _glyph;
        _ring.Child = _face;
        _button = new Button
        {
            Margin = new Thickness(1),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Template = new FuncControlTemplate<Button>((_, _) => _ring),
        };
        _button.PointerEntered += (_, _) => UpdateHighlight();
        _button.PointerExited += (_, _) => UpdateHighlight();
        _button.GotFocus += (_, _) => UpdateHighlight();
        _button.LostFocus += (_, _) => UpdateHighlight();
        _button.Click += async (_, _) =>
        {
            if (_state is AgentState.Ready or AgentState.Recording) await toggle();
        };
        _animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _animation.Tick += (_, _) => Animate();
        Opened += (_, _) => UpdateAnimation();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty) UpdateAnimation();
        };
        Closed += (_, _) => _animation.Stop();
        Content = _button;
        SetState(AgentState.Ready);

        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            Position = new PixelPoint(area.Right - (int)(82 * screen.Scaling), area.Y + (int)(80 * screen.Scaling));
        }
        PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(this).Properties.IsRightButtonPressed)
                BeginMoveDrag(args);
        };
    }

    public void SetState(AgentState state)
    {
        if (_state == state) return;
        _state = state;
        var (symbol, label, light, dark) = state switch
        {
            AgentState.Recording => (Stop, "Recording · click to stop", "#B93967", "#76213E"),
            AgentState.Transcribing => (Wave, "Transcribing audio", "#684ACD", "#3D2689"),
            AgentState.Sending => (Send, "Sending to Codex", "#217D91", "#16485D"),
            AgentState.Busy => (Busy, "Agent busy", "#47546E", "#293246"),
            _ => (Speaker, "Start recording and send to Codex", "#4B61E8", "#29399D"),
        };
        _glyph.Data = symbol;
        _glyph.Opacity = 1;
        _face.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(light), 0), new GradientStop(Color.Parse(dark), 1) },
        };
        AutomationProperties.SetName(_button, label);
        UpdateHighlight();
        UpdateAnimation();
    }

    private void UpdateHighlight()
    {
        var highlighted = _button.IsPointerOver || _button.IsFocused;
        _ring.BorderBrush = Brush.Parse(highlighted ? "#C9DBFF" : _state == AgentState.Recording ? "#DD789A" : "#7897E9");
        _ring.Background = Brush.Parse(highlighted ? "#344267" : "#19233D");
    }

    private void UpdateAnimation()
    {
        if (IsVisible && _state is AgentState.Recording or AgentState.Transcribing or AgentState.Sending)
            _animation.Start();
        else
            _animation.Stop();
    }

    private void Animate()
    {
        var phase = (Environment.TickCount64 % 1400) / 1400.0;
        _glyph.Opacity = 0.84 + 0.16 * Math.Cos(2 * Math.PI * phase);
    }
}
