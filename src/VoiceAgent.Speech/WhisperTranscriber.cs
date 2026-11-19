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

            using var lease = await WarmModel.AcquireAsync(model, cancellationToken);
            var factory = lease.Factory;
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

    // Short dictations share one verified, read-locked model. Processors keep their own prompt and language.
    private sealed class WarmModel : IDisposable
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static FileStream? _verified;
        private static WhisperFactory? _factory;
        private static long _lastUse;
        private static readonly Timer Idle = new(_ =>
        {
            if (!Gate.Wait(0)) return;
            try
            {
                if (Environment.TickCount64 - _lastUse >= 120_000) ReleaseModel();
            }
            finally { Gate.Release(); }
        }, null, 60_000, 60_000);
        public WhisperFactory Factory => _factory!;

        public static async Task<WarmModel> AcquireAsync(WhisperModel model, CancellationToken ct)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_factory is null || !string.Equals(_verified?.Name, Path.GetFullPath(model.Path), StringComparison.OrdinalIgnoreCase))
                {
                    ReleaseModel();
                    _verified = await model.OpenVerifiedAsync(ct).ConfigureAwait(false);
                    _factory = WhisperFactory.FromPath(model.Path);
                }
                return new WarmModel();
            }
            catch { ReleaseModel(); Gate.Release(); throw; }
        }

        public static async Task InvalidateAsync(string path, CancellationToken ct)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try { if (string.Equals(_verified?.Name, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) ReleaseModel(); }
            finally { Gate.Release(); }
        }

        private static void ReleaseModel()
        {
            _factory?.Dispose();
            _factory = null;
            _verified?.Dispose();
            _verified = null;
        }

        public void Dispose() { _lastUse = Environment.TickCount64; Gate.Release(); }
    }

    internal static Task ReleaseCachedAsync(string path, CancellationToken ct) => WarmModel.InvalidateAsync(path, ct);

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
