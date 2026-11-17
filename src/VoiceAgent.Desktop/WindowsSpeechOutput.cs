using System.Runtime.InteropServices;

namespace VoiceAgent.Desktop;

internal sealed class WindowsSpeechOutput : IDisposable
{
    private object? _voice;

    public void Speak(string text, int rate, string? voiceName, int volume)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        dynamic voice = GetVoice();
        voice.Rate = rate;
        voice.Volume = volume;
        SelectVoice(voice, text, voiceName);

        const int async = 1;
        const int purgeBeforeSpeak = 2;
        const int plainText = 16;
        voice.Speak(text, async | purgeBeforeSpeak | plainText);
    }

    /// <summary>Renders text with a Windows voice into a WAV file (22 kHz, 16-bit, mono). Blocks until done.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void SaveWave(string text, int rate, string? voiceName, string path, CancellationToken cancellationToken)
    {
        dynamic voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice", throwOnError: true)!)
            ?? throw new InvalidOperationException("Windows speech synthesis is unavailable.");
        dynamic stream = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream", throwOnError: true)!)
            ?? throw new InvalidOperationException("Windows speech synthesis is unavailable.");
        try
        {
            voice.Rate = rate;
            SelectVoice(voice, text, voiceName);
            const int saft22kHz16BitMono = 22;
            const int createForWrite = 3;
            stream.Format.Type = saft22kHz16BitMono;
            stream.Open(path, createForWrite, false);
            voice.AudioOutputStream = stream;
            const int async = 1;
            const int plainText = 16;
            voice.Speak(text, async | plainText);
            while (!(bool)voice.WaitUntilDone(200))
            {
                if (!cancellationToken.IsCancellationRequested) continue;
                voice.Speak("", async | 2);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            try { stream.Close(); } catch (COMException) { }
            Marshal.ReleaseComObject((object)stream);
            Marshal.ReleaseComObject((object)voice);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SelectVoice(dynamic voice, string text, string? voiceName)
    {
        var cyrillic = text.Any(character => character is >= '\u0400' and <= '\u04ff');
        var ukrainian = text.IndexOfAny(['і', 'І', 'ї', 'Ї', 'є', 'Є', 'ґ', 'Ґ']) >= 0;
        var locale = cyrillic ? ukrainian ? "422" : "419" : "409";
        var filter = string.IsNullOrEmpty(voiceName) ? $"Language={locale}" : "";
        dynamic voices = voice.GetVoices(filter, "");
        try
        {
            var matched = false;
            for (var i = 0; i < (int)voices.Count; i++)
            {
                dynamic selected = voices.Item(i);
                try
                {
                    if (!string.IsNullOrEmpty(voiceName) && (string)selected.GetDescription(0) != voiceName) continue;
                    voice.Voice = selected;
                    matched = true;
                    break;
                }
                finally { Marshal.ReleaseComObject((object)selected); }
            }
            if (!matched && (cyrillic || !string.IsNullOrEmpty(voiceName)))
                throw new InvalidOperationException("Choose an installed Windows voice that supports the text language.");
        }
        finally { Marshal.ReleaseComObject((object)voices); }
    }

    public IReadOnlyList<string> Voices()
    {
        if (!OperatingSystem.IsWindows()) return [];
        dynamic voices = ((dynamic)GetVoice()).GetVoices("", "");
        var names = new List<string>();
        try
        {
            for (var i = 0; i < (int)voices.Count; i++)
            {
                dynamic item = voices.Item(i);
                try { names.Add((string)item.GetDescription(0)); }
                finally { Marshal.ReleaseComObject((object)item); }
            }
        }
        finally { Marshal.ReleaseComObject((object)voices); }
        return names;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private object GetVoice() => _voice ??= Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice", throwOnError: true)!)
        ?? throw new InvalidOperationException("Windows speech synthesis is unavailable.");

    public void SetVolume(int volume)
    {
        if (_voice is not null) ((dynamic)_voice).Volume = volume;
    }

    public void Stop()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_voice is not null) ((dynamic)_voice).Speak("", 1 | 2);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() || _voice is null) return;
        try { Stop(); }
        finally
        {
            Marshal.ReleaseComObject(_voice);
            _voice = null;
        }
    }
}
