using System.Buffers.Binary;
using System.Runtime.Versioning;
using NAudio.Wave;
using VoiceAgent.Application;

namespace VoiceAgent.Speech;

/// <summary>
/// Reads an audio file (mp3, wav, m4a, aac, wma, flac … anything Windows Media Foundation can decode) as
/// 16 kHz mono 16-bit PCM in pieces of about two minutes. Each piece ends at the quietest moment near the
/// boundary so words are not cut, and memory use does not grow with the file length.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioFileChunks : IDisposable
{
    public static readonly string[] Extensions = ["mp3", "wav", "m4a", "aac", "wma", "flac", "ogg", "opus", "mp4"];
    private const int BytesPerSecond = VoiceCapture.SampleRate * VoiceCapture.BytesPerSample;
    private readonly WaveStream _reader;
    private readonly IWaveProvider _pcm;
    private readonly int _targetBytes;
    private readonly int _searchBytes;
    private byte[] _carry = [];
    private bool _ended;
    private long _consumed;

    private AudioFileChunks(WaveStream reader, int targetSeconds, int searchSeconds)
    {
        _reader = reader;
        var format = new WaveFormat(VoiceCapture.SampleRate, 16, 1);
        _pcm = new MediaFoundationResampler(reader, format) { ResamplerQuality = 60 };
        _targetBytes = targetSeconds * BytesPerSecond;
        _searchBytes = searchSeconds * BytesPerSecond;
    }

    public static AudioFileChunks Open(string path, int targetSeconds = 120, int searchSeconds = 15)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The audio file was not found.", path);
        try
        {
            return new AudioFileChunks(new MediaFoundationReader(path), targetSeconds, searchSeconds);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("Windows cannot decode this audio file. Try MP3, WAV or M4A.", error);
        }
    }

    public TimeSpan Duration => _reader.TotalTime;

    /// <summary>Audio already returned by <see cref="Next"/>.</summary>
    public TimeSpan Position => TimeSpan.FromSeconds((double)_consumed / BytesPerSecond);

    /// <summary>The next piece of PCM, or null at the end of the file.</summary>
    public byte[]? Next(CancellationToken cancellationToken = default)
    {
        var wanted = _targetBytes + _searchBytes;
        if (!_ended && _carry.Length < wanted)
        {
            var buffer = new MemoryStream(wanted);
            buffer.Write(_carry);
            var block = new byte[BytesPerSecond];
            while (buffer.Length < wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = _pcm.Read(block.AsSpan());
                if (read <= 0) { _ended = true; break; }
                buffer.Write(block, 0, read - read % 2);
            }
            _carry = buffer.ToArray();
        }

        if (_carry.Length == 0) return null;
        var split = _ended && _carry.Length <= wanted ? _carry.Length : QuietestSplit(_carry, _targetBytes - _searchBytes, _targetBytes + _searchBytes);
        var piece = _carry[..split];
        _carry = _carry[split..];
        _consumed += piece.Length;
        return piece;
    }

    /// <summary>Byte offset (sample-aligned) of the quietest 100 ms window between <paramref name="from"/> and <paramref name="to"/>.</summary>
    public static int QuietestSplit(ReadOnlySpan<byte> pcm, int from, int to)
    {
        const int window = VoiceCapture.SampleRate / 10 * 2;
        const int step = window / 2;
        from = Math.Max(0, from - from % 2);
        to = Math.Min(pcm.Length - window, to);
        if (to <= from) return Math.Min(pcm.Length, Math.Max(from, 2)) & ~1;
        var best = to;
        var bestEnergy = double.MaxValue;
        for (var start = from; start <= to; start += step)
        {
            double energy = 0;
            for (var i = start; i < start + window; i += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
                energy += (double)sample * sample;
            }
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                best = start + window / 2;
            }
        }
        return best & ~1;
    }

    public void Dispose()
    {
        (_pcm as IDisposable)?.Dispose();
        _reader.Dispose();
    }
}

/// <summary>Helpers for writing generated speech to files.</summary>
[SupportedOSPlatform("windows")]
public static class SpeechFiles
{
    /// <summary>Splits text into pieces of at most <paramref name="limit"/> characters, preferring paragraph and sentence ends.</summary>
    public static IReadOnlyList<string> Split(string text, int limit)
    {
        var pieces = new List<string>();
        var rest = text.Trim();
        while (rest.Length > limit)
        {
            var window = rest[..limit];
            var cut = window.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (cut < limit / 2) cut = Math.Max(window.LastIndexOfAny(['.', '!', '?', '…', '\n']), cut);
            if (cut < limit / 2) cut = window.LastIndexOf(' ');
            if (cut <= 0) cut = limit - 1;
            pieces.Add(rest[..(cut + 1)].Trim());
            rest = rest[(cut + 1)..].TrimStart();
        }
        if (rest.Length > 0) pieces.Add(rest);
        return pieces;
    }

    /// <summary>Joins WAV clips with the same format into one WAV file.</summary>
    public static void JoinWav(IEnumerable<Stream> clips, string destination)
    {
        WaveFileWriter? writer = null;
        try
        {
            var buffer = new byte[65536];
            foreach (var clip in clips)
            {
                using var reader = new WaveFileReader(clip);
                writer ??= new WaveFileWriter(destination, reader.WaveFormat);
                if (!reader.WaveFormat.Equals(writer.WaveFormat))
                    throw new InvalidDataException("Speech parts came back in different audio formats.");
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) writer.Write(buffer, 0, read);
            }
            if (writer is null) throw new InvalidDataException("No audio was generated.");
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>Encodes a WAV file to MP3 with the Windows MP3 encoder.</summary>
    public static void WavToMp3(string wav, string mp3, int bitRate = 128000)
    {
        using var reader = new WaveFileReader(wav);
        try { MediaFoundationEncoder.EncodeToMp3(reader, mp3, bitRate); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            throw new IOException("Windows could not create an MP3 file. Save as WAV instead.", error);
        }
    }

    public static TimeSpan Duration(string wav)
    {
        using var reader = new WaveFileReader(wav);
        return reader.TotalTime;
    }
}
