using System.Runtime.InteropServices;

namespace VoiceAgent.Desktop;

/// <summary>
/// "Dictate anywhere": types recognized text where the text cursor is in any app (Notepad, Word, a browser field),
/// or puts it on the clipboard when the focused window has no text cursor.
/// </summary>
internal static class AnywhereDictation
{
    /// <summary>The window and focused control at the moment the shortcut was pressed.</summary>
    internal readonly record struct Target(nint Window, nint Focus, uint Thread);

    public static Target? Capture()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var window = GetForegroundWindow();
        if (window == 0) return null;
        var thread = GetWindowThreadProcessId(window, out _);
        var info = Info(thread);
        return thread == 0 || info is null ? null : new Target(window, info.Value.Focus, thread);
    }

    /// <summary>True when the focused control shows a text cursor (a Windows caret).</summary>
    public static bool HasTextCursor(Target target) =>
        Info(target.Thread) is { } info && info.Caret != 0 && GetForegroundWindow() == target.Window;

    public enum Result { Typed, Clipboard, Failed }

    /// <summary>Types <paramref name="text"/> at the cursor, or copies it when there is no cursor or typing fails.</summary>
    public static async Task<Result> DeliverAsync(Target target, string text, CancellationToken cancel)
    {
        for (var attempt = 0; attempt < 40 && AnyModifierPressed(); attempt++)
            await Task.Delay(25, cancel);
        cancel.ThrowIfCancellationRequested();
        if (!AnyModifierPressed() && HasTextCursor(target) && Type(Clean(text)))
            return Result.Typed;
        return SetClipboard(text) ? Result.Clipboard : Result.Failed;
    }

    /// <summary>Line breaks become spaces so typing never presses Enter (which could send a chat message).</summary>
    internal static string Clean(string text) =>
        new(text.Trim().Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private static bool Type(string text)
    {
        if (text.Length == 0) return false;
        var input = new Input[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            input[2 * i] = new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Scan = text[i], Flags = 4 } } };
            input[2 * i + 1] = new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Scan = text[i], Flags = 6 } } };
        }
        return SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) == input.Length;
    }

    /// <summary>Puts Unicode text on the Windows clipboard; retries briefly while another app holds it.</summary>
    internal static bool SetClipboard(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // The clipboard must be opened by a window: with no owner, EmptyClipboard leaves it ownerless and
        // SetClipboardData fails, which would wipe the clipboard without putting the text there.
        var owner = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
        if (owner == 0) return false;
        try
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (!OpenClipboard(owner)) { Thread.Sleep(30); continue; }
                var memory = nint.Zero;
                try
                {
                    // Prepare the text first so the clipboard is only emptied when there is something to put in it.
                    memory = GlobalAlloc(0x0002, (nuint)((text.Length + 1) * 2));
                    if (memory == 0) return false;
                    var pointer = GlobalLock(memory);
                    if (pointer == 0) return false;
                    Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                    Marshal.WriteInt16(pointer, text.Length * 2, 0);
                    GlobalUnlock(memory);
                    if (!EmptyClipboard()) return false;
                    if (SetClipboardData(13, memory) == 0) return false;
                    memory = 0; // the clipboard owns it now
                    return true;
                }
                finally
                {
                    if (memory != 0) GlobalFree(memory);
                    CloseClipboard();
                }
            }
            return false;
        }
        finally { DestroyWindow(owner); }
    }

    private static GuiThreadInfo? Info(uint thread)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info : null;
    }

    private static bool AnyModifierPressed() =>
        (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
