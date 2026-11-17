using System.Diagnostics;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class VolumeWheelTests
{
    [Fact]
    public void HookRunsOnItsOwnThreadAndStopsCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var wheel = new WindowsVolumeWheel(_ => { });
        Assert.True(wheel.Registered);
        var watch = Stopwatch.StartNew();
        wheel.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "The hook thread did not stop promptly.");
        wheel.Dispose(); // idempotent
    }
}
