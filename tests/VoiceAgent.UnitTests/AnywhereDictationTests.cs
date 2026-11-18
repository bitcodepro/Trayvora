using VoiceAgent.Application;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class AnywhereDictationTests
{
    [Fact]
    public void DictateAnywhereGetsItsOwnShortcut()
    {
        var defaults = VoiceHotkeys.Default(HotkeyShortcut.Default);
        Assert.Equal("Ctrl+Alt+Shift+A", defaults.AnywhereShortcut.ToString());
        Assert.Equal(defaults.Entries.Count, defaults.Entries.Values.Distinct().Count());
        var custom = new HotkeyShortcut(HotkeyModifiers.Alt, 'V');
        var json = System.Text.Json.JsonSerializer.Serialize(defaults with { Anywhere = custom });
        Assert.Equal(custom, System.Text.Json.JsonSerializer.Deserialize<VoiceHotkeys>(json)!.Entries[VoiceAction.Anywhere]);
        Assert.NotEqual("Ctrl+Alt+Shift+A", (defaults with { Preview = defaults.AnywhereShortcut }).AnywhereShortcut.ToString());
    }

    [Fact]
    public void SettingsSavedBeforeTheShortcutExistedStillLoad()
    {
        var saved = System.Text.Json.JsonSerializer.Deserialize<VoiceHotkeys>(
            """{"Preview":{"Modifiers":6,"Key":90},"Background":{"Modifiers":7,"Key":90},"Send":{"Modifiers":7,"Key":88},"Loopback":{"Modifiers":1,"Key":88},"StopSpeech":{"Modifiers":1,"Key":67}}""")!;
        Assert.Equal(saved.Entries.Count, saved.Entries.Values.Distinct().Count());
        Assert.True(saved.AnywhereShortcut.IsValid);
    }

    [Theory]
    [InlineData("  hello\r\nworld\t! ", "hello  world !")]
    [InlineData("привіт", "привіт")]
    public void TypedTextNeverContainsEnterOrTabs(string recognized, string typed) =>
        Assert.Equal(typed, AnywhereDictation.Clean(recognized));
}

/// <summary>Writes the real Windows clipboard, so it runs only when TRAYVORA_LIVE_CLIPBOARD=1.</summary>
public sealed class AnywhereClipboardLiveTests
{
    [Fact]
    public void TextReallyLandsOnTheClipboard()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("TRAYVORA_LIVE_CLIPBOARD") != "1") return;
        const string text = "Trayvora clipboard test · привіт";
        Assert.True(AnywhereDictation.SetClipboard(text));
        var thread = new Thread(() => Assert.Equal(text, ReadClipboard()));
        thread.Start();
        thread.Join();
    }

    private static string? ReadClipboard()
    {
        if (!OpenClipboard(0)) return null;
        try
        {
            var handle = GetClipboardData(13);
            if (handle == 0) return null;
            var pointer = GlobalLock(handle);
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(pointer); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
}
