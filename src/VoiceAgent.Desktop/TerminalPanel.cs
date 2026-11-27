using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace VoiceAgent.Desktop;

/// <summary>What the built-in terminal starts.</summary>
internal enum TerminalKind { Claude, Codex, PowerShell }

/// <summary>
/// A real Windows console (conhost with PowerShell) shown inside the Trayvora window, with Claude Code or Codex
/// already started, so you can sign in, change the model, run /mcp or any command without opening another app.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TerminalPanel : UserControl
{
    private readonly Func<TerminalKind, ProcessStartInfo> _start;
    private readonly Border _host = new() { Background = Brush.Parse("#0C0C0C"), CornerRadius = new CornerRadius(10), ClipToBounds = true };
    private readonly TextBlock _status = new() { Foreground = TrayvoraTheme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14) };
    private readonly Dictionary<TerminalKind, Button> _chips = [];
    private ConsoleHost? _console;
    private TerminalKind _kind = TerminalKind.Claude;

    public event Action? CloseRequested;

    public TerminalPanel(Func<TerminalKind, ProcessStartInfo> start)
    {
        _start = start;
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (kind, label, tip) in new[]
        {
            (TerminalKind.Claude, "Claude Code", "PowerShell with claude started: /login, /model, /mcp, /config, /permissions"),
            (TerminalKind.Codex, "Codex", "PowerShell with codex started: /model, /mcp, codex login"),
            (TerminalKind.PowerShell, "PowerShell", "Plain PowerShell with claude and codex on PATH"),
        })
        {
            var chip = TrayvoraTheme.Chip(label);
            ToolTip.SetTip(chip, tip);
            chip.Click += (_, _) => Start(kind);
            chips.Children.Add(chip);
            _chips[kind] = chip;
        }
        var restart = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
        var close = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
        TrayvoraTheme.Ghost(restart);
        TrayvoraTheme.Ghost(close);
        ToolTip.SetTip(restart, "Restart the terminal");
        ToolTip.SetTip(close, "Close the terminal");
        restart.Click += (_, _) => Start(_kind);
        close.Click += (_, _) => CloseRequested?.Invoke();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock { Text = "Terminal", FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
        Grid.SetColumn(chips, 1);
        Grid.SetColumn(restart, 2);
        Grid.SetColumn(close, 3);
        header.Children.AddRange([title, chips, restart, close]);
        var hint = TrayvoraTheme.Hint("Click inside to type. In Claude Code, enter /mcp to select a server and reconnect. Press Esc to leave the menu.");
        hint.Margin = new Thickness(2, 6, 0, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        dock.Children.AddRange([header, hint, _host]);
        _host.Child = _status;
        Content = new Border
        {
            Background = TrayvoraTheme.Sidebar, BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(12, 14, 12, 12), Child = dock,
        };
    }

    public bool IsRunning(TerminalKind kind) => _kind == kind && _console?.Running == true;

    /// <summary>Gives the keyboard to the console (after the panel is shown or clicked).</summary>
    public void FocusConsole() => _console?.FocusConsole();

    /// <summary>Starts (or restarts) the terminal with <paramref name="kind"/>.</summary>
    public void Start(TerminalKind kind)
    {
        _kind = kind;
        foreach (var (key, chip) in _chips) TrayvoraTheme.SetSelected(chip, key == kind);
        Stop();
        try
        {
            _console = new ConsoleHost(_start(kind));
            _console.Exited += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_host.Child is ConsoleHost host && !host.Running)
                {
                    _status.Text = "The terminal was closed. Choose Claude Code, Codex or PowerShell above to start it again.";
                    _host.Child = _status;
                }
            });
            _host.Child = _console;
            var console = _console;
            Avalonia.Threading.Dispatcher.UIThread.Post(console.FocusConsole, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status.Text = "Could not start the terminal: " + error.Message;
            _host.Child = _status;
        }
    }

    public void Stop()
    {
        _console?.Kill();
        _console = null;
        _host.Child = _status;
    }
}

/// <summary>Hosts a conhost console window as a child window of the Trayvora window.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ConsoleHost : NativeControlHost
{
    private readonly Process _process;
    private IntPtr _window;
    private uint _consoleThread, _uiThread;
    private bool _attached;
    private bool _wasDown;
    private readonly Avalonia.Threading.DispatcherTimer _clicks = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly HashSet<IntPtr> _existing;
    private readonly DateTime _started;

    public event Action? Exited;

    public ConsoleHost(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        DisableQuickEdit();
        _existing = Consoles().Select(item => item.Window).ToHashSet();
        _started = DateTime.Now.AddSeconds(-1);
        _process = Process.Start(start) ?? throw new InvalidOperationException("The console did not start.");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke();
    }

    public bool Running
    {
        get
        {
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _window = FindConsole(TimeSpan.FromSeconds(5));
        if (_window == IntPtr.Zero) return base.CreateNativeControlCore(parent);
        var style = (long)GetWindowLongPtr(_window, GwlStyle);
        style &= ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox | WsPopup);
        style |= WsChild | WsVisible;
        SetWindowLongPtr(_window, GwlStyle, (IntPtr)style);
        var extended = (long)GetWindowLongPtr(_window, GwlExStyle);
        SetWindowLongPtr(_window, GwlExStyle, (IntPtr)(extended & ~WsExAppWindow));
        SetParent(_window, parent.Handle);
        ShowWindow(_window, 5);
        // The console window belongs to another process. Sharing its keyboard input with ours lets a click
        // inside it take the keyboard focus, so typing, Enter and the arrow keys reach Claude Code.
        _consoleThread = GetWindowThreadProcessId(_window, out _);
        _uiThread = GetCurrentThreadId();
        if (_consoleThread != 0 && _consoleThread != _uiThread) _attached = AttachThreadInput(_uiThread, _consoleThread, true);
        FocusConsole();
        // A click inside the console does not move the keyboard back from Trayvora's own controls by itself:
        // watch for a mouse press over the console and give it the keyboard.
        _clicks.Tick += (_, _) =>
        {
            // 0x8000: held now; 0x0001: pressed since the last check (a short click between two ticks).
            var down = (GetAsyncKeyState(0x01) & 0x8001) != 0 | (GetAsyncKeyState(0x02) & 0x8001) != 0;
            if (down && GetCursorPos(out var point) && WindowFromPoint(point) == _window && GetFocus() != _window) FocusConsole();
            _wasDown = down;
        };
        _clicks.Start();
        return new PlatformHandle(_window, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (control.Handle == _window) Kill();
        else base.DestroyNativeControlCore(control);
    }

    /// <summary>Gives the keyboard to the console.</summary>
    public void FocusConsole()
    {
        if (_window != IntPtr.Zero && Running) SetFocus(_window);
    }

    public void Kill()
    {
        _clicks.Stop();
        if (_attached)
        {
            AttachThreadInput(_uiThread, _consoleThread, false);
            _attached = false;
        }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// A click in a console with QuickEdit starts selecting text, and while it selects, Enter, Tab and the arrows
    /// do not reach the program. Turns QuickEdit off only for consoles started as conhost.exe directly
    /// (HKCU\Console\…conhost.exe), which is how Trayvora starts its terminal; other consoles keep their settings.
    /// </summary>
    private static void DisableQuickEdit()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var name in new[] { Path.Combine(system, "conhost.exe").Replace('\\', '_'), Path.Combine("%SystemRoot%", Path.GetRelativePath(windows, system), "conhost.exe").Replace('\\', '_') })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Console\" + name);
                key.SetValue("QuickEdit", 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
    }

    /// <summary>Every console window with the process Windows reports for it (the shell attached to it, not conhost).</summary>
    private static List<(IntPtr Window, int Process)> Consoles()
    {
        var found = new List<(IntPtr, int)>();
        EnumWindows((window, _) =>
        {
            var name = new char[64];
            var length = GetClassName(window, name, name.Length);
            if (new string(name, 0, length) == "ConsoleWindowClass")
            {
                GetWindowThreadProcessId(window, out var owner);
                found.Add((window, owner));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The new console window opened by our conhost: one that did not exist before and belongs to a shell started after it.</summary>
    private IntPtr FindConsole(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var (window, owner) in Consoles())
            {
                if (_existing.Contains(window)) continue;
                try
                {
                    using var shell = Process.GetProcessById(owner);
                    if (owner == _process.Id || shell.StartTime >= _started) return window;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            Thread.Sleep(50);
        }
        return IntPtr.Zero;
    }

    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsChild = 0x40000000, WsVisible = 0x10000000, WsCaption = 0x00C00000, WsThickFrame = 0x00040000,
        WsSysMenu = 0x00080000, WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000, WsPopup = 0x80000000, WsExAppWindow = 0x00040000;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [Out] char[] name, int length);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetParent(IntPtr child, IntPtr parent);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool join);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetFocus(IntPtr window);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetFocus();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);
}
