using NAudio.Wave;
using VoiceAgent.Application;
using VoiceAgent.Desktop;
using VoiceAgent.Speech;
using Xunit;

namespace VoiceAgent.UnitTests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AudioFileTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("trayvora-audio-");

    public void Dispose() => _folder.Delete(recursive: true);

    /// <summary>44.1 kHz stereo WAV: tone, then a short silence, then tone again.</summary>
    private string ToneWithGap(string name, double toneSeconds, double gapSeconds, double tailSeconds)
    {
        var path = Path.Combine(_folder.FullName, name);
        var format = new WaveFormat(44100, 16, 2);
        using var writer = new WaveFileWriter(path, format);
        void Write(double seconds, bool tone)
        {
            var frames = (int)(seconds * format.SampleRate);
            var buffer = new byte[frames * 4];
            for (var i = 0; i < frames; i++)
            {
                var value = tone ? (short)(Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * 9000) : (short)0;
                BitConverter.TryWriteBytes(buffer.AsSpan(i * 4), value);
                BitConverter.TryWriteBytes(buffer.AsSpan(i * 4 + 2), value);
            }
            writer.Write(buffer, 0, buffer.Length);
        }
        Write(toneSeconds, true);
        Write(gapSeconds, false);
        Write(tailSeconds, true);
        return path;
    }

    [Fact]
    public void LongFilesAreReadAsMonoPcmPartsCutAtTheQuietMoment()
    {
        if (!OperatingSystem.IsWindows()) return;
        var wav = ToneWithGap("long.wav", toneSeconds: 18, gapSeconds: 1, tailSeconds: 11);
        using var chunks = AudioFileChunks.Open(wav, targetSeconds: 20, searchSeconds: 4);
        Assert.InRange(chunks.Duration.TotalSeconds, 29.9, 30.1);

        var first = chunks.Next()!;
        var firstSeconds = first.Length / 32000.0;
        Assert.InRange(firstSeconds, 18.0, 19.0); // cut inside the silent gap, not at 20 s
        var rest = 0;
        while (chunks.Next() is { } piece) rest += piece.Length;
        Assert.InRange((first.Length + rest) / 32000.0, 29.8, 30.2);
        Assert.Null(chunks.Next());
    }

    [Fact]
    public void Mp3IsWrittenAndReadBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        var wav = ToneWithGap("tone.wav", 4, 0.5, 2);
        var mp3 = Path.Combine(_folder.FullName, "tone.mp3");
        SpeechFiles.WavToMp3(wav, mp3);
        Assert.True(new FileInfo(mp3).Length > 1000);
        using var chunks = AudioFileChunks.Open(mp3);
        Assert.InRange(chunks.Duration.TotalSeconds, 6.3, 6.8);
        var pcm = chunks.Next()!;
        Assert.InRange(pcm.Length / 32000.0, 6.3, 6.8);
    }

    [Fact]
    public void UnreadableFilesGiveAClearMessage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var bogus = Path.Combine(_folder.FullName, "notes.mp3");
        File.WriteAllText(bogus, "this is not audio");
        var error = Assert.Throws<InvalidDataException>(() => AudioFileChunks.Open(bogus));
        Assert.Contains("cannot decode", error.Message);
    }

    [Fact]
    public void GeneratedSpeechPartsJoinIntoOneWav()
    {
        if (!OperatingSystem.IsWindows()) return;
        var a = File.OpenRead(ToneWithGap("a.wav", 1, 0, 0));
        var b = File.OpenRead(ToneWithGap("b.wav", 2, 0, 0));
        var joined = Path.Combine(_folder.FullName, "joined.wav");
        using (a) using (b) SpeechFiles.JoinWav([a, b], joined);
        Assert.InRange(SpeechFiles.Duration(joined).TotalSeconds, 2.99, 3.01);
    }

    [Fact]
    public void LongTextIsSplitAtSentenceEnds()
    {
        var sentence = "Это одно предложение для проверки. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 300));
        var pieces = SpeechFiles.Split(text, 4000);
        Assert.True(pieces.Count >= 3);
        Assert.All(pieces, piece => Assert.True(piece.Length <= 4000));
        Assert.All(pieces.Take(pieces.Count - 1), piece => Assert.EndsWith(".", piece));
        Assert.Equal(text.Replace(" ", "").Length, string.Concat(pieces).Replace(" ", "").Length);
        Assert.Equal(["short"], SpeechFiles.Split("  short  ", 4000));
    }

    [Fact]
    public async Task WindowsVoiceToFileAndBackToTextWithWhisper()
    {
        if (!OperatingSystem.IsWindows()) return;
        var model = Environment.GetEnvironmentVariable("TRAYVORA_WHISPER_MODEL");
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model)) return; // needs the real Whisper Base model
        var wav = Path.Combine(_folder.FullName, "spoken.wav");
        WindowsSpeechOutput.SaveWave("Hello. The weather is sunny today and the meeting starts at ten.", 0, null, wav, CancellationToken.None);
        Assert.True(SpeechFiles.Duration(wav).TotalSeconds > 2);

        var mp3 = Path.Combine(_folder.FullName, "spoken.mp3");
        SpeechFiles.WavToMp3(wav, mp3);
        var transcriber = new WhisperTranscriber(new WhisperModel(model));
        await using var batch = await transcriber.StartBatchAsync("en", CancellationToken.None);
        using var chunks = AudioFileChunks.Open(mp3);
        var text = "";
        while (chunks.Next() is { } pcm) text += await batch.TranscribeAsync(pcm, CancellationToken.None);
        Assert.Contains("weather", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("meeting", text, StringComparison.OrdinalIgnoreCase);
    }
}
