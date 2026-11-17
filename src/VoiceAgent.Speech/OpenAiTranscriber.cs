using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceAgent.Application;

namespace VoiceAgent.Speech;

public sealed class OpenAiTranscriber(string model, string apiKey, HttpClient? client = null) : ISpeechTranscriber
{
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/audio/transcriptions");

    public async Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pcm.IsEmpty) return "";
        var wav = new byte[44 + pcm.Length];
        try
        {
            WriteWavHeader(wav, pcm.Length);
            pcm.CopyTo(wav.AsMemory(44));
            using var body = new MultipartFormDataContent();
            using var audio = new ByteArrayContent(wav);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            body.Add(audio, "file", "recording.wav");
            body.Add(new StringContent(model), "model");
            if (language is "ru" or "uk" or "en")
                body.Add(new StringContent(language), "language");
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = body };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await (client ?? DefaultClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new IOException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "OpenAI Audio rejected the API key or model access.",
                    HttpStatusCode.TooManyRequests => "OpenAI Audio rate limit or quota reached. Check API billing.",
                    _ => $"OpenAI Audio request failed (HTTP {(int)response.StatusCode}).",
                });
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return json.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("OpenAI Audio timed out.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wav);
        }
    }

    private static void WriteWavHeader(Span<byte> buffer, int length)
    {
        "RIFF"u8.CopyTo(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], length + 36);
        "WAVEfmt "u8.CopyTo(buffer[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[24..], VoiceCapture.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[28..], VoiceCapture.SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[34..], 16);
        "data"u8.CopyTo(buffer[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[40..], length);
    }
}
