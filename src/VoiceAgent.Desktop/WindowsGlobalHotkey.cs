using System.ComponentModel;
using System.Runtime.InteropServices;
using VoiceAgent.Application;

namespace VoiceAgent.Desktop;

internal sealed partial class WindowsGlobalHotkey : IGlobalHotkeyService
{
    private const int HotkeyId = 1;
    private const uint DefaultModifiers = 0x0001 | 0x0002 | 0x4000; // Alt, Ctrl, MOD_NOREPEAT
    private const uint DefaultKey = 0x20;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private readonly TaskCompletionSource<HotkeyRegistration> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly uint _modifiers;
    private readonly uint _key;
    private Thread? _thread;
    private uint _threadId;
    private bool _disposed;

    public WindowsGlobalHotkey() : this(DefaultModifiers, DefaultKey)
    {
    }

    internal WindowsGlobalHotkey(uint modifiers, uint key)
    {
        _modifiers = modifiers;
        _key = key;
    }

    internal uint MessageThreadId => _threadId;

    public event EventHandler? Pressed;

    // The desktop lifetime calls Start and Dispose on its UI thread.
    public HotkeyRegistration Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is null)
        {
            _thread = new Thread(Pump) { IsBackground = true, Name = "VoiceAgent hotkey" };
            _thread.Start();
        }

        return _started.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_thread is { IsAlive: true } && _started.Task.Result.Registered)
        {
            if (!PostThreadMessage(_threadId, WmQuit, 0, 0) && _thread.IsAlive)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            _thread.Join();
        }
    }

    private void Pump()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0); // Ensure the thread message queue exists.
        if (!RegisterHotKey(0, HotkeyId, _modifiers, _key))
        {
            _started.SetResult(new HotkeyRegistration(false, Marshal.GetLastPInvokeError()));
            return;
        }

        _started.SetResult(new HotkeyRegistration(true, 0));
        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == WmHotkey && message.WParam == HotkeyId)
                {
                    Pressed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            UnregisterHotKey(0, HotkeyId);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Message message, nint window, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
}
