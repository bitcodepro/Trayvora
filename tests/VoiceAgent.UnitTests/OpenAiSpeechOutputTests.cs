using System.Net;
using System.Text.Json;
using VoiceAgent.Speech;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class OpenAiSpeechOutputTests
{
    [Fact]
    public async Task SendsEditedTextVoiceAndSpeedToSpeechEndpoint()
    {
        using var handler = new SpeechHandler();
        using var client = new HttpClient(handler);
        var speech = new OpenAiSpeechOutput("test-key", client);
        using var audio = await speech.GenerateAsync("Проверка звука", "marin", 1.2, default);
        Assert.Equal("RIFF", new StreamReader(audio).ReadToEnd());
    }

    private sealed class SpeechHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/audio/speech", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("Проверка звука", data.RootElement.GetProperty("input").GetString());
            Assert.Equal("gpt-4o-mini-tts", data.RootElement.GetProperty("model").GetString());
            Assert.Equal("marin", data.RootElement.GetProperty("voice").GetString());
            Assert.Equal(1.2, data.RootElement.GetProperty("speed").GetDouble());
            Assert.Equal("wav", data.RootElement.GetProperty("response_format").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("RIFF"u8.ToArray()) };
        }
    }
}
