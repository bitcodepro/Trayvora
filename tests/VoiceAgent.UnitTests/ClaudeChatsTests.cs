using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ClaudeChatsTests
{
    [Fact]
    public void FailuresShowClaudeCodesOwnReason()
    {
        Assert.Equal("Claude Code: Not logged in · Please run /login", ClaudeChats.FailureMessage("Not logged in · Please run /login", "", ""));
        Assert.Equal("Claude Code: line two line three line four",
            ClaudeChats.FailureMessage(null, "line one\r\nline two\n\nline three\nline four\n", ""));
        Assert.StartsWith("Claude Code exited without an answer", ClaudeChats.FailureMessage(null, " ", ""));
        Assert.EndsWith("…", ClaudeChats.FailureMessage(new string('x', 1000), "", ""));
    }

    [Fact]
    public void ClaudeRunsWithTheRealUserFoldersNotTheLauncherRedirects()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", @"E:\redirected\localappdata");
            var folders = ClaudeChats.RealUserFolders().ToDictionary(item => item.Name, item => item.Value);
            Assert.NotEqual(@"E:\redirected\localappdata", folders["LOCALAPPDATA"]);
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folders["LOCALAPPDATA"], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(folders["LOCALAPPDATA"], "Temp"), folders["TEMP"]);
            Assert.True(folders.ContainsKey("APPDATA"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previous);
        }
    }
}
