using System.Runtime.InteropServices;

namespace VoiceAgent.Desktop;

/// <summary>
/// Alt+mouse wheel adjusts playback volume system-wide. The low-level mouse hook runs on its own thread with
/// its own message loop, so mouse input elsewhere in Windows never waits for the Trayvora UI thread.
/// </summary>
internal sealed class WindowsVolumeWheel : IDisposable
{
    private const uint WmQuit = 0x0012;
    private readonly MouseHook _callback;
    private readonly Action<int> _change;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private nint _hook;
    private uint _threadId;
    private int _delta;

    public WindowsVolumeWheel(Action<int> change)
    {
        _change = change;
        _callback = OnMouse;
        _thread = new Thread(Run) { IsBackground = true, Name = "Trayvora volume wheel" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public bool Registered => _hook != 0;

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(14, _callback, GetModuleHandleW(null), 0);
        _ready.Set();
        if (_hook == 0) return;
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
        UnhookWindowsHookEx(_hook);
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code == 0 && message == 0x020A && Down(0x12) &&
            !Down(0x11) && !Down(0x10) && !Down(0x5B) && !Down(0x5C) &&
            (Marshal.ReadInt32(data, 12) & 1) == 0)
        {
            _delta += (short)(Marshal.ReadInt32(data, 8) >> 16);
            var steps = _delta / 120;
            _delta %= 120;
            if (steps != 0) Avalonia.Threading.Dispatcher.UIThread.Post(() => _change(steps));
            return 1;
        }

        if (message == 0x020A) _delta = 0;
        return CallNextHookEx(0, code, message, data);
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (_threadId != 0 && _thread.IsAlive)
        {
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    private delegate nint MouseHook(int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int type, MouseHook hook, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Msg message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string? name);
}
