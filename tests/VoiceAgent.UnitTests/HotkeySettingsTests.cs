using VoiceAgent.Application;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class HotkeySettingsTests
{
    [Fact]
    public void RejectedShortcutKeepsPreviousRegistration()
    {
        var previous = new FakeHotkey(true);
        var candidate = new FakeHotkey(false);
        var saves = 0;
        using var controller = new HotkeyController(
            shortcut => shortcut == HotkeyShortcut.Default ? previous : candidate, _ => saves++);
        Assert.True(controller.Apply(HotkeyShortcut.Default, persist: false).Registered);
        var alternate = new HotkeyShortcut(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'U');
        Assert.False(controller.Apply(alternate, persist: true).Registered);
        Assert.Equal(HotkeyShortcut.Default, controller.ActiveShortcut);
        Assert.False(previous.Disposed);
        Assert.True(candidate.Disposed);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void SaveFailureReleasesCandidateAndKeepsPreviousRegistration()
    {
        var previous = new FakeHotkey(true);
        var candidate = new FakeHotkey(true);
        using var controller = new HotkeyController(
            shortcut => shortcut == HotkeyShortcut.Default ? previous : candidate,
            _ => throw new IOException());
        controller.Apply(HotkeyShortcut.Default, persist: false);
        var alternate = new HotkeyShortcut(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'U');
        Assert.Throws<IOException>(() => controller.Apply(alternate, persist: true));
        Assert.Equal(HotkeyShortcut.Default, controller.ActiveShortcut);
        Assert.False(previous.Disposed);
        Assert.True(candidate.Disposed);
    }

    [Fact]
    public void SuccessfulChangePersistsNewShortcutAndReleasesPreviousRegistration()
    {
        var previous = new FakeHotkey(true);
        var candidate = new FakeHotkey(true);
        var folder = Directory.CreateTempSubdirectory("voiceagent-settings-");
        try
        {
            var settings = new HotkeySettings(folder.FullName);
            using var controller = new HotkeyController(
                shortcut => shortcut == HotkeyShortcut.Default ? previous : candidate, settings.Save);
            controller.Apply(HotkeyShortcut.Default, persist: false);
            var alternate = new HotkeyShortcut(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'U');
            Assert.True(controller.Apply(alternate, persist: true).Registered);
            Assert.Equal(alternate, controller.ActiveShortcut);
            Assert.Equal(alternate, new HotkeySettings(folder.FullName).Load().Shortcut);
            Assert.True(previous.Disposed);
        }
        finally
        {
            File.Delete(Path.Combine(folder.FullName, "hotkey.json"));
            folder.Delete();
        }
    }

    private sealed class FakeHotkey(bool registered) : IGlobalHotkeyService
    {
        public event EventHandler? Pressed { add { } remove { } }
        public bool Disposed { get; private set; }
        public HotkeyRegistration Start() => new(registered, registered ? 0 : HotkeyRegistration.AlreadyRegistered);
        public void Dispose() => Disposed = true;
    }
}
