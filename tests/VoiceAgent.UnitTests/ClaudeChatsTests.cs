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
    public async Task StopSendsAnInterruptAndWaitsForTheMatchingReceiptAndResult()
    {
        using var stop = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var output = new CliOutput();
        var stdin = new CliInput(packet =>
        {
            if (packet.GetProperty("type").GetString() == "user")
            {
                stop.Cancel();
                return;
            }
            Assert.Equal("interrupt", packet.GetProperty("request").GetProperty("subtype").GetString());
            var id = packet.GetProperty("request_id").GetString();
            output.Write(System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "control_response", response = new { subtype = "success", request_id = id },
            }));
            output.Write("""{"type":"result","result":"partial","is_error":false}""");
        });
        var parser = new ClaudeStreamParser(null);
        var interrupted = await ClaudeToolPrompts.RunAsync(stdin, output, "question", parser,
            (_, _, _) => throw new InvalidOperationException("Unexpected tool prompt"), lifetime.Token, stop.Token);

        Assert.True(interrupted);
        Assert.Equal("partial", parser.Result);
    }

    private sealed class CliInput(Action<System.Text.Json.JsonElement> send) : StringWriter
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var packet = System.Text.Json.JsonDocument.Parse(buffer);
            send(packet.RootElement);
            return Task.CompletedTask;
        }
    }

    private sealed class CliOutput : TextReader
    {
        private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();
        public void Write(string line) => _lines.Writer.TryWrite(line);
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _lines.Reader.ReadAsync(cancellationToken);
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
