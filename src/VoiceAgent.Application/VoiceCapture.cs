namespace VoiceAgent.Application;

public sealed record Microphone(string Id, string Name)
{
    public override string ToString() => Name;
}

public interface IAudioRecorder
{
    IReadOnlyList<Microphone> ListMicrophones();

    Task<int> RecordAsync(string? deviceId, byte[] pcm, IProgress<double> peak,
        CancellationToken stop, CancellationToken cancel);
}

public interface ISpeechTranscriber
{
    Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm, string language, CancellationToken cancellationToken);
}

/// <summary>Recognizes many pieces of one long recording with the same language and a model loaded once.</summary>
public interface ISpeechBatch : IAsyncDisposable
{
    Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken);
}

/// <summary>Transcribers that can keep their model loaded across pieces implement this.</summary>
public interface IBatchSpeechTranscriber : ISpeechTranscriber
{
    Task<ISpeechBatch> StartBatchAsync(string language, CancellationToken cancellationToken);
}

public static class SpeechBatches
{
    public static Task<ISpeechBatch> StartBatchAsync(this ISpeechTranscriber transcriber, string language, CancellationToken cancellationToken) =>
        transcriber is IBatchSpeechTranscriber batch
            ? batch.StartBatchAsync(language, cancellationToken)
            : Task.FromResult<ISpeechBatch>(new PerCall(transcriber, language));

    private sealed class PerCall(ISpeechTranscriber transcriber, string language) : ISpeechBatch
    {
        public Task<string> TranscribeAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken) =>
            transcriber.TranscribeAsync(pcm, language, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public static class VoiceCapture
{
    public const int SampleRate = 16000;
    public const int BytesPerSample = 2;
    public const int MaxSeconds = 120;
    public const int Capacity = SampleRate * BytesPerSample * MaxSeconds;
}
