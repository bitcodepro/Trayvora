using NAudio.Wave;
using VoiceAgent.Application;
using VoiceAgent.Speech;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class WhisperTests
{
    [Fact]
    public async Task SilentAudioDoesNotRequireAModel()
    {
        var transcriber = new WhisperTranscriber(new WhisperModel("missing-model.bin"));
        Assert.Empty(await transcriber.TranscribeAsync(new byte[3200], "auto", default));
    }

    [Fact]
    public async Task CancelledTranscriptionDoesNotReadModel()
    {
        var transcriber = new WhisperTranscriber(new WhisperModel("missing-model.bin"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transcriber.TranscribeAsync(new byte[] { 0, 127 }, "en", new CancellationToken(true)));
    }

    [LocalSpeechTheory]
    [InlineData("en", "microphone")]
    [InlineData("ru", "микрофон")]
    [InlineData("uk", "взяв на себе")]
    public async Task TranscribesKnownPhraseFromLocalAudio(string language, string expected)
    {
        var folder = Environment.GetEnvironmentVariable("VOICEAGENT_SPEECH_FIXTURES")!;

        using var reader = new WaveFileReader(Path.Combine(folder, language + ".wav"));
        Assert.Equal(VoiceCapture.SampleRate, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        var pcm = new byte[reader.Length];
        await reader.ReadExactlyAsync(pcm);
        var model = new WhisperModel(Path.Combine(folder, "ggml-base.bin"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var hint = language == "ru" ? VoiceAgent.Desktop.SpeechOptions.DefaultWhisperHints.Replace("\n", ", ") : null;
        var text = await new WhisperTranscriber(model, hint).TranscribeAsync(pcm, language, timeout.Token);
        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
    }
}
internal sealed class LocalSpeechTheoryAttribute : TheoryAttribute
{
    public LocalSpeechTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOICEAGENT_SPEECH_FIXTURES")))
        {
            Skip = "Set VOICEAGENT_SPEECH_FIXTURES to the verified model and test audio directory.";
        }
    }
}
