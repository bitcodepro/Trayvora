using System.Net;
using System.Text.Json;
using VoiceAgent.Speech;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class OpenAiQuestionsTests
{
    [Fact]
    public async Task SendsOnlyRecentConversationAndUsesNoTools()
    {
        using var client = new HttpClient(new TestHandler());
        var questions = new OpenAiQuestions(client);
        var history = Enumerable.Range(0, 7).Select(i => new QuestionExchange($"Question {i}", $"Answer {i}")).ToArray();
        Assert.Equal("Земля.", await questions.AskAsync("test-key", "Какая планета?", history.TakeLast(6).ToArray(), default));
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.TryGetProperty("tools", out _));
            var input = root.GetProperty("input");
            Assert.Equal(13, input.GetArrayLength());
            Assert.Equal("Question 1", input[0].GetProperty("content").GetString());
            Assert.Equal("assistant", input[1].GetProperty("role").GetString());
            Assert.Equal("Какая планета?", input[12].GetProperty("content").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"output":[{"type":"message","content":[{"type":"output_text","text":"Земля."}]}]}"""),
            };
        }
    }
}
