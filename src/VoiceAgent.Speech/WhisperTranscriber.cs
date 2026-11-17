using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using VoiceAgent.Application;
using Whisper.net;

namespace VoiceAgent.Speech;

public sealed class WhisperTranscriber(WhisperModel model, string? initialPrompt = null) : IBatchSpeechTranscriber
{
    public async Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var samples = Decode(pcm.Span);
        try
        {
            if (!samples.Any(sample => Math.Abs(sample) >= 0.002))
            {
                return string.Empty;
            }

            await using var verified = await model.OpenVerifiedAsync(cancellationToken);
            using var factory = WhisperFactory.FromPath(model.Path);
            var builder = factory.CreateBuilder().WithLanguage(language);
            if (!string.IsNullOrWhiteSpace(initialPrompt)) builder.WithPrompt(initialPrompt);
            await using var processor = builder.Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(segment.Text.Trim());
            }

            cancellationToken.ThrowIfCancellationRequested();
            return text.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(samples.AsSpan()));
        }
    }

    /// <summary>Verifies and loads the model once for all pieces of a long recording.</summary>
    public async Task<ISpeechBatch> StartBatchAsync(string language, CancellationToken cancellationToken)
    {
        var verified = await model.OpenVerifiedAsync(cancellationToken);
        try
        {
            return new Batch(verified, WhisperFactory.FromPath(model.Path), language, initialPrompt);
        }
        catch
        {
            await verified.DisposeAsync();
            throw;
        }
    }

    private sealed class Batch(FileStream verified, WhisperFactory factory, string language, string? prompt) : ISpeechBatch
    {
        public async Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = Decode(pcm.Span);
            try
            {
                if (!samples.Any(sample => Math.Abs(sample) >= 0.002)) return string.Empty;
                var builder = factory.CreateBuilder().WithLanguage(language);
                if (!string.IsNullOrWhiteSpace(prompt)) builder.WithPrompt(prompt);
                await using var processor = builder.Build();
                var text = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
                {
                    if (text.Length > 0) text.Append(' ');
                    text.Append(segment.Text.Trim());
                }
                cancellationToken.ThrowIfCancellationRequested();
                return text.ToString();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(samples.AsSpan()));
            }
        }

        public async ValueTask DisposeAsync()
        {
            factory.Dispose();
            await verified.DisposeAsync();
        }
    }

    private static float[] Decode(ReadOnlySpan<byte> pcm)
    {
        var result = new float[pcm.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(2 * i)..]) / 32768f;
        }

        return result;
    }
}
