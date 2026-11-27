using System.Text.Json;
using System.Threading.Channels;
using VoiceAgent.Codex;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class CodexSessionTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("voiceagent-codex-");

    [Fact]
    public async Task ReplacesMissingSavedThreadBeforeSendingExactlyOneTurn()
    {
        File.WriteAllText(Path.Combine(_folder.FullName, "thread.txt"), "missing-thread");
        var server = new FakeConnection(_folder.FullName) { MissingThread = true };
        server.Finish("completed");
        Assert.Equal(CodexTurnStatus.Completed, await RunAsync(server, new CollectedOutput()));
        Assert.Contains("thread/resume", server.Methods);
        Assert.Contains("thread/start", server.Methods);
        Assert.Single(server.Methods, method => method == "turn/start");
    }

    [Fact]
    public async Task StartsANewThreadWhenTheSavedIdIsMalformed()
    {
        File.WriteAllText(Path.Combine(_folder.FullName, "thread.txt"), "not a thread ID");
        var server = new FakeConnection(_folder.FullName);
        server.Finish("completed");
        Assert.Equal(CodexTurnStatus.Completed, await RunAsync(server, new CollectedOutput()));
        Assert.Contains("thread/start", server.Methods);
        Assert.Equal("thread-1", File.ReadAllText(Path.Combine(_folder.FullName, "thread.txt")));
    }

    [Fact]
    public async Task StreamsOnlyMatchingDeltasAndResumesTheSavedThread()
    {
        var initial = new FakeConnection(_folder.FullName);
        initial.Publish("""{"method":"item/agentMessage/delta","params":{"threadId":"thread-1","turnId":"another","delta":"ignore"}}""");
        initial.Publish("""{"method":"item/agentMessage/delta","params":{"threadId":"thread-1","turnId":"turn-1","delta":"Hello "}}""");
        initial.Publish("""{"method":"item/agentMessage/delta","params":{"threadId":"thread-1","turnId":"turn-1","delta":"world"}}""");
        initial.Publish("""{"method":"item/completed","params":{"threadId":"thread-1","turnId":"another","item":{"id":"0","type":"webSearch"}}}""");
        initial.Publish("""{"method":"item/completed","params":{"threadId":"thread-1","turnId":"turn-1","item":{"id":"1","type":"mcpToolCall","tool":"read_document","status":"completed","arguments":"secret"}}}""");
        initial.Finish("completed");
        var output = new CollectedOutput();
        IReadOnlyList<ToolActivity>? tools = null;
        var accepted = false;
        Assert.Equal(CodexTurnStatus.Completed, await CodexSession.RunTurnAsync(initial, _folder.FullName,
            Path.Combine(_folder.FullName, "thread.txt"), "Hello", output, default, activity => tools = activity,
            onAccepted: () =>
            {
                Assert.Empty(output.Deltas);
                accepted = true;
            }));
        Assert.True(accepted);
        Assert.Equal(new ToolActivity("MCP · read_document", "Completed"), Assert.Single(tools!));
        Assert.Equal(new[] { "Hello ", "world" }, output.Deltas);
        Assert.Contains("thread/start", initial.Methods);

        var resumed = new FakeConnection(_folder.FullName);
        resumed.Finish("failed");
        Assert.Equal(CodexTurnStatus.Failed, await RunAsync(resumed, new CollectedOutput()));
        Assert.Contains("thread/resume", resumed.Methods);
    }

    [Fact]
    public async Task SameChatResumesAcrossToolChangesAndAnotherChatStartsFresh()
    {
        const string revqora = @"E:\test\revqora.exe";
        var chatId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var file = Path.Combine(_folder.FullName, CodexSession.ThreadFileName(chatId));
        var other = Path.Combine(_folder.FullName, CodexSession.ThreadFileName(otherId));
        try
        {
            var initial = new FakeConnection(_folder.FullName) { Revqora = revqora };
            initial.Finish("completed");
            await CodexSession.RunTurnAsync(initial, _folder.FullName, file, "List repositories", new CollectedOutput(), default, revqora: revqora);
            Assert.Contains("mcpServerStatus/list", initial.Methods);

            var next = new FakeConnection(_folder.FullName);
            next.Finish("completed");
            await CodexSession.RunTurnAsync(next, _folder.FullName, file, "Which of those was first?", new CollectedOutput(), default);
            Assert.Equal("thread-1", next.ResumedId);
            Assert.DoesNotContain("mcpServerStatus/list", next.Methods);

            var fresh = new FakeConnection(_folder.FullName);
            fresh.Finish("completed");
            await CodexSession.RunTurnAsync(fresh, _folder.FullName, other, "New subject", new CollectedOutput(), default);
            Assert.Contains("thread/start", fresh.Methods);
            Assert.DoesNotContain("thread/resume", fresh.Methods);
        }
        finally
        {
            File.Delete(file);
            File.Delete(other);
        }
    }

    [Fact]
    public async Task CancellationSendsInterruptForTheActiveTurnAndWaitsForTerminalStatus()
    {
        using var cancel = new CancellationTokenSource();
        var server = new FakeConnection(_folder.FullName);
        var run = RunAsync(server, new CollectedOutput(), cancel.Token);
        await server.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        var request = await server.Interrupted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("thread-1", request.GetProperty("threadId").GetString());
        Assert.Equal("turn-1", request.GetProperty("turnId").GetString());
        Assert.False(run.IsCompleted);
        server.Finish("interrupted");
        Assert.Equal(CodexTurnStatus.Interrupted, await run);
    }

    [Fact]
    public async Task DisconnectDoesNotReportSuccessfulCompletion()
    {
        var server = new FakeConnection(_folder.FullName);
        server.Disconnect();
        Assert.Equal(CodexTurnStatus.Unknown, await RunAsync(server, new CollectedOutput()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MissingChatGptLoginOrEnabledMcpPreventsSending(bool loggedIn, bool unsafeConfig)
    {
        var server = new FakeConnection(_folder.FullName) { LoggedIn = loggedIn, UnsafeConfig = unsafeConfig };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(server, new CollectedOutput()));
        Assert.DoesNotContain("turn/start", server.Methods);
    }

    [Fact]
    public void IncompleteSessionConfigIsReportedAsInvalidData()
    {
        using var config = JsonDocument.Parse("{}");
        var error = Assert.Throws<InvalidDataException>(() => CodexSession.VerifyConfig(config.RootElement, null));
        Assert.Equal("Codex App Server returned an incomplete configuration.", error.Message);
    }

    [Theory]
    [InlineData("request_action")]
    [InlineData("open_view")]
    public void RevqoraProfileRefusesAnExpandedToolList(string unexpectedTool)
    {
        const string executable = @"E:\test\revqora.exe";
        var config = JsonSerializer.SerializeToElement(new
        {
            model_provider = "openai",
            web_search = "disabled",
            features = CodexSession.DisabledFeatures.ToDictionary(name => name, _ => false),
            mcp_servers = new
            {
                revqora = new
                {
                    enabled = true,
                    command = executable,
                    args = new[] { "--mcp" },
                    enabled_tools = CodexSession.RevqoraTools.Append(unexpectedTool).ToArray(),
                },
            },
        });

        Assert.Throws<InvalidOperationException>(() => CodexSession.VerifyConfig(config, executable));
    }

    private Task<CodexTurnStatus> RunAsync(FakeConnection server, CollectedOutput output, CancellationToken ct = default) =>
        CodexSession.RunTurnAsync(server, _folder.FullName, Path.Combine(_folder.FullName, "thread.txt"), "Hello", output, ct);

    public void Dispose()
    {
        File.Delete(Path.Combine(_folder.FullName, "thread.txt"));
        _folder.Delete();
    }

    private sealed class CollectedOutput : IProgress<string>
    {
        public List<string> Deltas { get; } = [];
        public void Report(string value) => Deltas.Add(value);
    }

    private sealed class FakeConnection(string workspace) : ICodexConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public List<string> Methods { get; } = [];
        public bool MissingThread { get; init; }
        public bool LoggedIn { get; init; } = true;
        public bool UnsafeConfig { get; init; }
        public string? Revqora { get; init; }
        public string? ResumedId { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Publish(string message)
        {
            using var document = JsonDocument.Parse(message);
            _events.Writer.TryWrite(document.RootElement.Clone());
        }

        public void Finish(string status) => Publish(
            $$$$"""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"{{{{status}}}}"}}}""");

        public void Disconnect() => _events.Writer.TryComplete(new IOException("Server disconnected."));
        public IAsyncEnumerable<JsonElement> ReadEventsAsync(CancellationToken ct) => _events.Reader.ReadAllAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Methods.Add(method);
            if (method == "thread/resume") ResumedId = JsonSerializer.SerializeToElement(parameters).GetProperty("threadId").GetString();
            object result = method switch
            {
                "account/read" => new { requiresOpenaiAuth = true, account = LoggedIn ? new { type = "chatgpt" } : null },
                "config/read" => new
                {
                    config = new
                    {
                        model_provider = "openai", web_search = "disabled",
                        features = CodexSession.DisabledFeatures.ToDictionary(key => key, _ => false),
                        mcp_servers = Revqora is not null
                            ? new Dictionary<string, object>
                            {
                                ["revqora"] = new { enabled = true, command = Revqora, args = new[] { "--mcp" }, enabled_tools = CodexSession.RevqoraTools },
                            }
                            : UnsafeConfig ? new Dictionary<string, object> { ["sample"] = new { enabled = true } } : [],
                    },
                },
                "thread/resume" when MissingThread => throw new AppServerRpcException(JsonSerializer.SerializeToElement(
                    new { error = new { code = -32600, message = "no rollout found for thread missing-thread" } })),
                "thread/start" or "thread/resume" => new
                {
                    thread = new { id = "thread-1" }, cwd = workspace, approvalPolicy = "never",
                    modelProvider = "openai", sandbox = new { type = "readOnly" },
                },
                "mcpServerStatus/list" => new { data = new[] { new { name = "revqora", tools = new { list_repositories = new { } } } } },
                "turn/start" => Start(),
                "turn/interrupt" => Interrupt(parameters),
                _ => throw new InvalidOperationException(method),
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }

        private object Start()
        {
            Started.TrySetResult();
            return new { turn = new { id = "turn-1" } };
        }

        private object Interrupt(object parameters)
        {
            Interrupted.TrySetResult(JsonSerializer.SerializeToElement(parameters));
            return new { };
        }
    }
}
