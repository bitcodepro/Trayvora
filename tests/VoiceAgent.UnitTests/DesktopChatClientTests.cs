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
