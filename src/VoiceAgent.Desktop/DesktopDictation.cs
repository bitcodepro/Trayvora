using System.Runtime.InteropServices;
using System.Text;

namespace VoiceAgent.Desktop;

internal static class DesktopDictation
{
    internal readonly record struct Target(nint Window, nint Focus, uint ProcessId);

    public static Target? Capture()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var window = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(window, out var processId);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Focus == 0) return null;

        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) return null;
        var family = new StringBuilder(256);
        uint size = (uint)family.Capacity;
        if (GetPackageFamilyName(process, ref size, family) != 0 ||
            !string.Equals(family.ToString(), "OpenAI.Codex_2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)) return null;

        return new Target(window, info.Focus, processId);
    }

    public static async Task<bool> InsertAsync(Target target, string draft, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 12 && AnyModifierPressed(); attempt++)
            await Task.Delay(25, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (AnyModifierPressed() || Capture() != target) return false;

        // Keep the payload to text, including when recognition adds line breaks.
        var text = new string(draft.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        var input = new Input[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            input[2 * i] = new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Scan = text[i], Flags = 4 } } };
            input[2 * i + 1] = new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Scan = text[i], Flags = 6 } } };
        }
        return Capture() == target && SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) == input.Length;
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
    [DllImport("kernel32.dll")] private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, ref uint size, StringBuilder family);
}
