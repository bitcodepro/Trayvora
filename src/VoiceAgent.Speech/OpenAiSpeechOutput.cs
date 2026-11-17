using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace VoiceAgent.Speech;

public sealed class OpenAiSpeechOutput(string apiKey, HttpClient? client = null)
{
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    public const int MaxInputLength = 4096;

    public async Task<Stream> GenerateAsync(string text, string voice, double speed, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxInputLength)
            throw new ArgumentException("For OpenAI playback, enter up to 4,096 characters.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech")
        {
            Content = JsonContent.Create(new { model = "gpt-4o-mini-tts", input = text, voice, speed, response_format = "wav" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await (client ?? DefaultClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new IOException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "OpenAI rejected the speech API key or model access.",
                    HttpStatusCode.TooManyRequests => "OpenAI speech rate limit or quota reached. Check API billing.",
                    _ => $"OpenAI speech failed (HTTP {(int)response.StatusCode}).",
                });
            await using var audio = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new MemoryStream();
            try
            {
                var chunk = new byte[65536];
                int size;
                while ((size = await audio.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    if (buffer.Length + size > 32 * 1024 * 1024)
                        throw new InvalidDataException("OpenAI returned too much audio.");
                    buffer.Write(chunk, 0, size);
                }
                buffer.Position = 0;
                return buffer;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("OpenAI speech request timed out.");
        }
    }
}
