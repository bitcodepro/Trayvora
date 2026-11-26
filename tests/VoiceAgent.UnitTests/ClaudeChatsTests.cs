using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ClaudeChatsTests
{
    [Theory]
    [InlineData("mcp__example__lookup", true, "allow")]
    [InlineData("mcp__example__lookup", false, "deny")]
    [InlineData("Bash", true, "deny")]
    public async Task HostRespondsOnlyToTheRequestedTool(string tool, bool answer, string expected)
    {
        var stdout = new StringReader(string.Join("\n",
            System.Text.Json.JsonSerializer.Serialize(new { type = "control_request", request_id = "abc",
                request = new { subtype = "can_use_tool", tool_name = tool, input = new { query = "example" } } }),
            """{"type":"result","result":"finished","is_error":false}"""));
        var stdin = new StringWriter();
        var parser = new ClaudeStreamParser(null);
        var asked = 0;
        await ClaudeToolPrompts.RunAsync(stdin, stdout, "test", parser, (name, input, _) =>
        {
            asked++;
            Assert.Equal(tool, name);
            Assert.Equal("example", input.GetProperty("query").GetString());
            return Task.FromResult(answer);
        }, default);
        Assert.Equal(tool == "Bash" ? 0 : 1, asked);
        var lines = stdin.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var packet = System.Text.Json.JsonDocument.Parse(lines[1]);
        var response = packet.RootElement.GetProperty("response");
        Assert.Equal("abc", response.GetProperty("request_id").GetString());
        var decision = response.GetProperty("response");
        Assert.Equal(expected, decision.GetProperty("behavior").GetString());
        Assert.Equal(expected == "allow", decision.TryGetProperty("updatedInput", out _));
        Assert.Equal("finished", parser.Result);
    }

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
            Assert.False(folders.ContainsKey("TEMP"));
            Assert.False(folders.ContainsKey("TMP"));
            Assert.True(folders.ContainsKey("APPDATA"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previous);
        }
    }
}
