using System.Net;
using System.Text;
using VoiceAgent.Speech;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class OpenAiTranscriberTests
{
    [Fact]
    public async Task SendsWavOnlyToAudioEndpointAndReturnsDraft()
    {
        using var client = new HttpClient(new Stub(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal("RIFF", Encoding.ASCII.GetString((await parts[0].ReadAsByteArrayAsync())[..4]));
            Assert.Equal("gpt-transcribe", await parts[1].ReadAsStringAsync());
            Assert.Equal("uk", await parts[2].ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":" Готово "}""") };
        }));
        var transcriber = new OpenAiTranscriber("gpt-transcribe", "test-token", client);
        Assert.Equal("Готово", await transcriber.TranscribeAsync(new byte[] { 0, 1 }, "uk", default));
    }

    [Fact]
    public async Task UnauthorizedResponseDoesNotRevealServerPayload()
    {
        using var client = new HttpClient(new Stub(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret payload") })));
        var transcriber = new OpenAiTranscriber("gpt-transcribe", "test-token", client);
        var error = await Assert.ThrowsAsync<IOException>(() => transcriber.TranscribeAsync(new byte[] { 0, 1 }, "auto", default));
        Assert.Contains("API key", error.Message);
        Assert.DoesNotContain("secret payload", error.Message);
    }

    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
