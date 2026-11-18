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
