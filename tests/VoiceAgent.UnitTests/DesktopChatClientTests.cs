using System.Text.Json;
using VoiceAgent.Codex;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class DesktopChatClientTests
{
    [Fact]
    public async Task SubmissionReturnsOnlyTheReplyToItsOwnMessage()
    {
        await using var connection = new TestConnection();
        var chat = Guid.NewGuid();
        var acknowledgments = 0;
        var reply = await DesktopChatClient.SubmitAsync(connection, chat, "Привет!", new Progress<string>(), default,
            onAccepted: () =>
            {
                Assert.Equal(["thread/queue/add"], connection.Methods);
                acknowledgments++;
            });

        Assert.Equal(1, acknowledgments);
        Assert.NotNull(reply);
        Assert.Equal("Связь работает.", reply.Answer);
        Assert.Equal("Привет!", reply.Question);
        Assert.Equal("completed", reply.Status);
        Assert.Equal(new[] { "thread/queue/add", "thread/turns/list" }, connection.Methods);
        Assert.Equal(chat.ToString(), connection.ChatId);
    }

    [Fact]
    public async Task FirstMessageCanWaitForTheNewChatsHistory()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var chat = Guid.NewGuid();
        var delivery = Guid.NewGuid();
        var polls = 0;
        await using var connection = new ScriptConnection((method, _, _) => method switch
        {
            "thread/start" => JsonSerializer.SerializeToElement(new { thread = new { id = chat } }),
            "thread/name/set" => JsonSerializer.SerializeToElement(new { }),
            "thread/queue/add" => JsonSerializer.SerializeToElement(new { queuedSubmission = new { id = "queue-1", clientUserMessageId = delivery } }),
            "thread/turns/list" when ++polls == 1 => throw new AppServerRpcException(JsonSerializer.SerializeToElement(new
            {
                error = new { code = -32600, message = $"thread {chat} is not materialized yet; thread/turns/list is unavailable before first user message" },
            })),
            "thread/turns/list" => JsonSerializer.SerializeToElement(new
            {
                data = new[]
                {
                    new { id = "first-turn", status = "completed", items = new object[]
                    {
                        new { type = "userMessage", clientId = delivery, content = Array.Empty<object>() },
                        new { type = "agentMessage", phase = "final_answer", text = "Reply" },
                    } },
                },
            }),
            _ => throw new InvalidOperationException(method),
        });

        var result = await DesktopChatClient.StartChatAsync(connection, "workspace", "Name", "Question",
            new Progress<string>(), new Progress<string>(), ct.Token, _ => Task.FromResult(delivery));
        Assert.Equal("Reply", Assert.IsType<DesktopTurn>(result.Turn).Answer);
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task FirstReplyCanBeRecoveredAfterItsAcknowledgmentIsLost()
    {
        var chatId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var sent = new List<string>();
        Guid? savedChat = null;
        string? receivedId = null;
        var room = Directory.CreateTempSubdirectory("trayvora-recover-first-");
        try
        {
            await using (var connection = new ScriptConnection((method, args, _) =>
            {
                sent.Add(method);
                switch (method)
                {
                    case "thread/start":
                        return JsonSerializer.SerializeToElement(new { thread = new { id = chatId } });
                    case "thread/name/set":
                        return JsonSerializer.SerializeToElement(new { });
                    case "thread/queue/add":
                        Assert.Equal(chatId, savedChat);
                        Assert.Equal(chatId.ToString(), args.GetProperty("threadId").GetString());
                        receivedId = args.GetProperty("clientUserMessageId").GetString();
                        Assert.Equal(deliveryId.ToString(), receivedId);
                        throw new TimeoutException("Lost the acknowledgment after accepting the request.");
                    default: throw new InvalidOperationException(method);
                }
            }))
            {
                var error = await Assert.ThrowsAsync<IOException>(() => DesktopChatClient.StartChatAsync(connection, room.FullName,
                    "Sample chat", "Same question", new Progress<string>(), new Progress<string>(), default, chat =>
                    {
                        savedChat = chat.Id;
                        return Task.FromResult(deliveryId);
                    }));
                Assert.Contains("Check reply", error.Message);
            }

            await using var fresh = new ScriptConnection((method, args, _) =>
            {
                Assert.Equal("thread/turns/list", method);
                Assert.Equal(savedChat!.Value.ToString(), args.GetProperty("threadId").GetString());
                return JsonSerializer.SerializeToElement(new
                {
                    data = new[] { Turn("different client", "An unrelated reply"), Turn(receivedId!, "The first reply") },
                });
            });

            var recovered = await DesktopChatClient.CheckDeliveryAsync(fresh, savedChat!.Value, deliveryId, default);
            Assert.Equal("The first reply", Assert.IsType<DesktopTurn>(recovered).Answer);
            Assert.Equal(["thread/start", "thread/name/set", "thread/queue/add"], sent);
        }
        finally { room.Delete(); }

        static object Turn(string clientId, string reply) => new
        {
            id = Guid.NewGuid(), status = "completed",
            items = new object[]
            {
                new { type = "userMessage", clientId, content = new[] { new { type = "text", text = "Same question" } } },
                new { type = "agentMessage", phase = "final_answer", text = reply },
            },
        };
    }

    [Theory]
    [InlineData(true, "removed")]
    [InlineData(false, "stopRequested")]
    public async Task StopUsesTheRemoteSubmissionOrTheMatchingTurn(bool queued, string expected)
    {
        using var cancellation = new CancellationTokenSource();
        var chat = Guid.NewGuid();
        var delivery = Guid.NewGuid();
        var methods = new List<string>();
        await using var connection = new ScriptConnection((method, args, ct) =>
        {
            methods.Add(method);
            Assert.Equal(chat.ToString(), args.GetProperty("threadId").GetString());
            Assert.False(ct.IsCancellationRequested);
            switch (method)
            {
                case "thread/queue/add":
                    Assert.Equal(delivery.ToString(), args.GetProperty("clientUserMessageId").GetString());
                    cancellation.Cancel();
                    return JsonSerializer.SerializeToElement(new { queuedSubmission = new { id = "server-queue-id", clientUserMessageId = delivery.ToString() } });
                case "thread/queue/delete":
                    Assert.Equal("server-queue-id", args.GetProperty("queuedSubmissionId").GetString());
                    return JsonSerializer.SerializeToElement(new { deleted = queued });
                case "thread/turns/list":
                    return JsonSerializer.SerializeToElement(new
                    {
                        data = new[] { Turn("other-turn", "other-client"), Turn("our-turn", delivery.ToString()) }
                    });
                case "turn/interrupt":
                    Assert.Equal("our-turn", args.GetProperty("turnId").GetString());
                    return JsonSerializer.SerializeToElement(new { });
                default: throw new InvalidOperationException(method);
            }
        });
        var reply = await DesktopChatClient.SubmitAsync(connection, chat, "question", new Progress<string>(), cancellation.Token, delivery: delivery);

        Assert.Equal(expected, Assert.IsType<DesktopTurn>(reply).Status);
        Assert.Equal(queued ? new[] { "thread/queue/add", "thread/queue/delete" }
            : ["thread/queue/add", "thread/queue/delete", "thread/turns/list", "turn/interrupt"], methods);

        static object Turn(string id, string clientId) => new
        {
            id, status = "inProgress", items = new[] { new { type = "userMessage", clientId, content = Array.Empty<object>() } }
        };
    }

    [Fact]
    public async Task StopBeforeDispatchDoesNotReadOrModifyTheRemoteQueue()
    {
        using var cancellation = new CancellationTokenSource();
        await using var connection = new ScriptConnection((method, _, _) => throw new InvalidOperationException(method));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DesktopChatClient.SubmitAsync(connection,
            Guid.NewGuid(), "question", new Progress<string>(), cancellation.Token,
            beforeDispatch: () => { cancellation.Cancel(); return Task.CompletedTask; }));
    }

    private sealed class ScriptConnection(Func<string, JsonElement, CancellationToken, JsonElement> script) : ICodexConnection
    {
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public IAsyncEnumerable<JsonElement> ReadEventsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct) =>
            Task.FromResult(script(method, JsonSerializer.SerializeToElement(parameters), ct));
    }

    [Fact]
    public async Task DiscoveryReadsPastFirstPageAndKeepsFullTitles()
    {
        await using var connection = new PagedConnection();
        var chats = await DesktopChatClient.ListAsync(connection, default);
        Assert.Equal(2, chats.Count);
        Assert.Equal(new string('а', 120), chats[1].Title);
        Assert.Equal(new string?[] { null, "next-page" }, connection.Cursors);
    }

    private sealed class PagedConnection : ICodexConnection
    {
        private readonly Guid _first = Guid.NewGuid();
        private readonly Guid _second = Guid.NewGuid();
        public List<string?> Cursors { get; } = [];
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public IAsyncEnumerable<JsonElement> ReadEventsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            Assert.Equal("thread/list", method);
            var cursor = JsonSerializer.SerializeToElement(parameters).GetProperty("cursor").GetString();
            Cursors.Add(cursor);
            var first = new { id = _first, name = "First chat" };
            var second = new { id = _second, name = new string('а', 120) };
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                data = cursor is null ? new[] { first } : new[] { first, second },
                nextCursor = cursor is null ? "next-page" : null,
            }));
        }
    }

    private sealed class TestConnection : ICodexConnection
    {
        private string? _clientId;
        public string? ChatId { get; private set; }
        public List<string> Methods { get; } = [];

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public IAsyncEnumerable<JsonElement> ReadEventsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Polling should only use the turn history.");

        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            Methods.Add(method);
            var json = JsonSerializer.SerializeToElement(parameters);
            if (method == "thread/queue/add")
            {
                ChatId = json.GetProperty("threadId").GetString();
                _clientId = json.GetProperty("clientUserMessageId").GetString();
                Assert.Equal("Привет!", json.GetProperty("input")[0].GetProperty("text").GetString());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { queuedSubmission = new { clientUserMessageId = _clientId } }));
            }

            Assert.Equal(ChatId, json.GetProperty("threadId").GetString());
            using var response = JsonDocument.Parse($$$"""
                {"data":[
                  {"id":"other","status":"completed","items":[
                    {"type":"userMessage","clientId":"other-user","content":[{"type":"text","text":"Other question"}]},
                    {"type":"agentMessage","phase":"final_answer","text":"Different reply"}
                  ]},
                  {"id":"ours","status":"completed","items":[
                    {"type":"userMessage","clientId":"{{{_clientId}}}","content":[{"type":"text","text":"Привет!"}]},
                    {"type":"agentMessage","phase":"commentary","text":"Progress message"},
                    {"type":"agentMessage","phase":"final_answer","text":"Связь работает."}
                  ]}
                ]}
                """);
            return Task.FromResult(response.RootElement.Clone());
        }
    }
}
