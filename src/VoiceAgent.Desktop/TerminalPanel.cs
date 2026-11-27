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
internal sealed record TerminalContext(string Workspace, string Profile);

/// <summary>
/// A real Windows console (conhost with PowerShell) shown inside the Trayvora window, with Claude Code or Codex
/// already started, so you can sign in, change the model, run /mcp or any command without opening another app.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TerminalPanel : UserControl
{
    private readonly Func<TerminalKind, TerminalContext, ProcessStartInfo> _start;
    private readonly TextBlock _replaceMessage = TrayvoraTheme.Hint("");
    private readonly StackPanel _replacePrompt;
    private (TerminalKind Kind, TerminalContext Context)? _pending;
    private TerminalContext? _context;
    private readonly Border _host = new() { Background = Brush.Parse("#0C0C0C"), CornerRadius = new CornerRadius(10), ClipToBounds = true };
    private readonly Grid _surface = new();
    private readonly TextBlock _status = new() { Foreground = TrayvoraTheme.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14) };
    private readonly Dictionary<TerminalKind, Button> _chips = [];
    private ConsoleHost? _console;
    private TerminalKind _kind = TerminalKind.Claude;

    public event Action? CloseRequested;

    public TerminalPanel(Func<TerminalKind, TerminalContext, ProcessStartInfo> start)
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
            chip.Click += (_, _) => { if (_context is { } context) Start(kind, context); };
            chips.Children.Add(chip);
            _chips[kind] = chip;
        }
        var restart = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
        var close = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
        TrayvoraTheme.Ghost(restart);
        TrayvoraTheme.Ghost(close);
        ToolTip.SetTip(restart, "Restart the terminal");
        ToolTip.SetTip(close, "Close the terminal");
        restart.Click += (_, _) => { if (_context is { } context) Start(_kind, context, restart: true); };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock { Text = "Terminal", FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
        Grid.SetColumn(chips, 1);
        Grid.SetColumn(restart, 2);
        Grid.SetColumn(close, 3);
        header.Children.AddRange([title, chips, restart, close]);
        var hint = TrayvoraTheme.Hint("Click inside to type. In Claude Code, enter /mcp to select a server and reconnect. Press Esc to leave the menu.");
        hint.Margin = new Thickness(2, 6, 0, 0);
        var keep = new Button { Content = "Keep current console" };
        var replace = new Button { Content = "Replace console" };
        TrayvoraTheme.StyleControls(keep);
        TrayvoraTheme.DangerButton(replace);
        keep.Click += (_, _) => { ClearReplacement(); FocusConsole(); };
        replace.Click += (_, _) => { if (_pending is { } next) StartCore(next.Kind, next.Context); };
        _replaceMessage.TextWrapping = TextWrapping.Wrap;
        _replacePrompt = new StackPanel
        {
            IsVisible = false, Spacing = 6, Margin = new Thickness(0, 0, 0, 8),
            Children = { _replaceMessage, new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Children = { keep, replace } } },
        };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_replacePrompt, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        dock.Children.AddRange([header, _replacePrompt, hint, _host]);
        _surface.Children.Add(_status);
        _host.Child = _surface;
        Content = new Border
        {
            Background = TrayvoraTheme.Sidebar, BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(12, 14, 12, 12), Child = dock,
        };
    }

    public bool Running => _console?.Running == true;

    /// <summary>Gives the keyboard to the console (after the panel is shown or clicked).</summary>
    public void FocusConsole() => _console?.FocusConsole();

    public void Start(TerminalKind kind, TerminalContext context, bool restart = false)
    {
        if (!Running)
        {
            StartCore(kind, context);
            return;
        }

        if (!restart && _kind == kind && _context == context)
        {
            ClearReplacement();
            FocusConsole();
            return;
        }

        _pending = (kind, context);
        _replaceMessage.Text = $"{UiLanguage.T("Replacing the console will stop its running processes.")}\n{context.Workspace}";
        _replacePrompt.IsVisible = true;
    }

    private void ClearReplacement()
    {
        _pending = null;
        _replacePrompt.IsVisible = false;
    }

    private void StartCore(TerminalKind kind, TerminalContext context)
    {
        ConsoleHost? candidate = null;
        try
        {
            candidate = new ConsoleHost(_start(kind, context)) { IsVisible = false };
            candidate.Exited += ConsoleExited;
            _surface.Children.Add(candidate);
            // Avalonia attaches the native window when the control enters the live visual tree.
            if (!candidate.Running || (TopLevel.GetTopLevel(this) is not null && !candidate.Embedded))
                throw new IOException(UiLanguage.T("Windows could not open the new console window."));
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (candidate is not null) RemoveConsole(candidate);
            var message = "Could not start the terminal: " + error.Message;
            if (Running)
            {
                _replaceMessage.Text = message;
                _replacePrompt.IsVisible = true;
            }
            else
            {
                _context = context;
                _kind = kind;
                _status.Text = message;
                _status.IsVisible = true;
            }
            return;
        }

        var previous = _console;
        _console = candidate;
        _context = context;
        _kind = kind;
        foreach (var (key, chip) in _chips) TrayvoraTheme.SetSelected(chip, key == kind);
        _status.IsVisible = false;
        candidate.IsVisible = true;
        ClearReplacement();
        if (previous is not null) RemoveConsole(previous);
        Avalonia.Threading.Dispatcher.UIThread.Post(candidate.FocusConsole, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void ConsoleExited(ConsoleHost console) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_console != console) return;
        _console = null;
        RemoveConsole(console);
        _status.Text = "The terminal was closed. Choose Claude Code, Codex or PowerShell above to start it again.";
        _status.IsVisible = true;
    });

    private void RemoveConsole(ConsoleHost console)
    {
        console.Exited -= ConsoleExited;
        _surface.Children.Remove(console);
        console.Kill();
    }

    public void Stop()
    {
        ClearReplacement();
        if (_console is not { } console) return;
        _console = null;
        RemoveConsole(console);
        _status.IsVisible = true;
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
    private bool _killed;
    private readonly Avalonia.Threading.DispatcherTimer _clicks = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly HashSet<IntPtr> _existing;
    public event Action<ConsoleHost>? Exited;

    public ConsoleHost(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        if (Path.GetFileName(start.FileName).Equals("conhost.exe", StringComparison.OrdinalIgnoreCase))
            DisableQuickEdit();
        _existing = Consoles().Select(item => item.Window).ToHashSet();
        _process = Process.Start(start) ?? throw new InvalidOperationException("The console did not start.");
        try
        {
            _process.Exited += (_, _) => Exited?.Invoke(this);
            _process.EnableRaisingEvents = true;
        }
        catch
        {
            Kill();
            throw;
        }
    }

    public bool Running
    {
        get
        {
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public bool Embedded => _window != IntPtr.Zero;

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
        if (GetParent(_window) != parent.Handle)
        {
            _window = IntPtr.Zero;
            return base.CreateNativeControlCore(parent);
        }
        ShowWindow(_window, 4);
        // The console window belongs to another process. Sharing its keyboard input with ours lets a click
        // inside it take the keyboard focus, so typing, Enter and the arrow keys reach Claude Code.
        _consoleThread = GetWindowThreadProcessId(_window, out _);
        _uiThread = GetCurrentThreadId();
        if (_consoleThread != 0 && _consoleThread != _uiThread) _attached = AttachThreadInput(_uiThread, _consoleThread, true);
        // A click inside the console does not move the keyboard back from Trayvora's own controls by itself:
        // watch for a mouse press over the console and give it the keyboard.
        _clicks.Tick += (_, _) =>
        {
            // 0x8000: held now; 0x0001: pressed since the last check (a short click between two ticks).
            var down = (GetAsyncKeyState(0x01) & 0x8001) != 0 | (GetAsyncKeyState(0x02) & 0x8001) != 0;
            if (down && GetCursorPos(out var point) && WindowFromPoint(point) == _window && GetFocus() != _window) FocusConsole();
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
        if (_killed) return;
        _killed = true;
        _clicks.Stop();
        if (_attached)
        {
            AttachThreadInput(_uiThread, _consoleThread, false);
            _attached = false;
        }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { _process.Dispose(); }
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

    /// <summary>Find the new window owned by our conhost or the shell it started.</summary>
    private IntPtr FindConsole(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (Running && DateTime.UtcNow < deadline)
        {
            foreach (var (window, owner) in Consoles())
            {
                if (_existing.Contains(window)) continue;
                if (owner == _process.Id || IsChildProcess(owner, _process.Id)) return window;
            }
            Thread.Sleep(50);
        }
        return IntPtr.Zero;
    }

    private static unsafe bool IsChildProcess(int childId, int parentId)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) return false;
        var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
        if (!Process32First(snapshot, ref entry)) return false;
        do
        {
            if (entry.ProcessId == childId) return entry.ParentProcessId == parentId;
        } while (Process32Next(snapshot, ref entry));
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        public uint Size, Usage;
        public int ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads;
        public int ParentProcessId, BasePriority;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll")]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32First(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32Next(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry entry);

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
    private static partial IntPtr GetParent(IntPtr child);

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
