using System.Runtime.InteropServices;
using VoiceAgent.Application;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class HotkeyTests
{
    [Fact]
    public void SecondRegistrationConflictsAndCanRegisterAfterRelease()
    {
        Assert.True(OperatingSystem.IsWindows());

        // Ctrl+Shift+F10 leaves the actual application shortcut available to the user.
        const uint modifiers = 0x0002 | 0x0004 | 0x4000;
        const uint f10 = 0x79;
        using var first = new WindowsGlobalHotkey(modifiers, f10);
        Assert.True(first.Start().Registered);
        Assert.True(first.Start().Registered);

        using (var second = new WindowsGlobalHotkey(modifiers, f10))
        {
            Assert.Equal(new HotkeyRegistration(false, HotkeyRegistration.AlreadyRegistered), second.Start());
        }

        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Start());

        using var third = new WindowsGlobalHotkey(modifiers, f10);
        Assert.True(third.Start().Registered);
    }

    [Fact]
    public async Task DispatchesHotkeyFromTheWindowsMessageQueue()
    {
        using var service = new WindowsGlobalHotkey(0x0002 | 0x0004 | 0x4000, 0x7A);
        var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Pressed += (_, _) => pressed.TrySetResult();
        Assert.True(service.Start().Registered);
        Assert.True(PostThreadMessage(service.MessageThreadId, 0x0312, 1, 0));
        await pressed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
}
