using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VoiceAgent.Desktop;

internal sealed partial class WindowsDesktopEvents : IDisposable
{
    private const uint PowerBroadcast = 0x0218;
    private const int SuspendCode = 0x0004;
    private const int ResumeAutomatic = 0x0012;
    private const int ResumeInteractive = 0x0007;
    private const uint AddIcon = 0;
    private const uint ModifyIcon = 1;
    private const uint DeleteIcon = 2;
    private const uint Info = 0x10;
    private readonly Window _window;
    private readonly nint _handle;
    private readonly nint _registration;
    private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromSeconds(35) };
    private bool _notificationActive;
    private bool _suspended;

    public event EventHandler? Suspending;
    public event EventHandler? Resumed;
    public bool Registered => _registration != 0;

    public WindowsDesktopEvents(Window window)
    {
        _window = window;
        _handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (_handle != 0)
        {
            Win32Properties.AddWndProcHookCallback(window, OnWindowMessage);
            _registration = RegisterSuspendResumeNotification(_handle, 0);
        }

        _expiry.Tick += (_, _) => ClearNotification();
    }

    public bool Notify(bool succeeded)
    {
        if (_handle == 0)
        {
            return false;
        }

        var icon = CreateIcon();
        if (!_notificationActive)
        {
            icon.Icon = LoadIcon(0, 32512);
            icon.Flags = 0x2 | 0x4;
            icon.Tip = "Trayvora";
            _notificationActive = ShellNotifyIcon(AddIcon, ref icon);
            if (!_notificationActive) return false;
        }

        icon.Flags = Info;
        icon.Title = "Trayvora";
        icon.Message = succeeded ? "Codex completed the request." : "A Codex request ended. Open History for its status.";
        icon.InfoFlags = 0x1 | 0x10 | 0x80; // Info icon, no sound, respect quiet time.
        var shown = ShellNotifyIcon(ModifyIcon, ref icon);
        _expiry.Stop();
        _expiry.Start();
        return shown;
    }

    internal static bool? DecodePowerMessage(uint message, nint code) => message == PowerBroadcast
        ? (int)code switch { SuspendCode => true, ResumeAutomatic or ResumeInteractive => false, _ => null }
        : null;

    private nint OnWindowMessage(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (DecodePowerMessage(message, wParam) is { } suspended && suspended != _suspended)
        {
            _suspended = suspended;
            if (suspended) Suspending?.Invoke(this, EventArgs.Empty);
            else Resumed?.Invoke(this, EventArgs.Empty);
        }
        return 0;
    }

    private NotifyIconData CreateIcon() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _handle, Id = 1,
        Tip = "", Title = "", Message = "",
    };

    private void ClearNotification()
    {
        _expiry.Stop();
        if (!_notificationActive) return;
        var icon = CreateIcon();
        ShellNotifyIcon(DeleteIcon, ref icon);
        _notificationActive = false;
    }

    public void Dispose()
    {
        ClearNotification();
        if (_registration != 0) UnregisterSuspendResumeNotification(_registration);
        if (_handle != 0) Win32Properties.RemoveWndProcHookCallback(_window, OnWindowMessage);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Message;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData icon);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial nint LoadIcon(nint instance, nint name);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterSuspendResumeNotification(nint recipient, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterSuspendResumeNotification(nint registration);
}
