using System.Security.Cryptography;
using System.Text;
using VoiceAgent.Application;
using VoiceAgent.Speech;

namespace VoiceAgent.Desktop;

/// <summary>Where a recording is made; each place can use its own recognizer.</summary>
internal enum RecognitionPlace { Main, Sessions, Questions, ComputerSound }

/// <summary>The recognizer choices shown in Settings, sessions and questions; index 0 follows the default.</summary>
internal static class Recognizers
{
    public static readonly string[] Choices = ["Automatic (default)", "Local Whisper", "OpenAI"];
    public static int Index(bool? cloud) => cloud switch { null => 0, false => 1, true => 2 };
    public static bool? Value(int index) => index switch { 1 => false, 2 => true, _ => null };
}

/// <param name="KeyHint">The last characters of the saved OpenAI key, shown so the user can see a key is saved.</param>
/// <param name="SessionCloud">Recognizer for session dictation: true OpenAI, false Whisper, null the main choice.</param>
/// <param name="QuestionCloud">Recognizer for Quick and Deep questions (same meaning).</param>
/// <param name="ComputerSoundCloud">Recognizer for the computer-sound shortcut (same meaning; null means OpenAI when a key is saved).</param>
internal sealed record SpeechOptions(bool Cloud = false, int Local = 0, int Remote = 0, string? ProtectedKey = null, string? WhisperHints = null,
    string? KeyHint = null, bool? SessionCloud = null, bool? QuestionCloud = null, bool? ComputerSoundCloud = null)
{
    public const string DefaultWhisperHints = "Revqora\nCodex\nMCP\nкоммитить\nпушать\nзапушать\nсоздать бранчу\nзапулить\nсмёржить\nсделать ребейз\nоткрыть пул-реквест";
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveWhisperHints => WhisperHints ?? DefaultWhisperHints;
    [System.Text.Json.Serialization.JsonIgnore]
    public string WhisperPrompt => string.Join(", ", EffectiveWhisperHints.Split('\n', StringSplitOptions.RemoveEmptyEntries));
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class SpeechSettings(string directory, string modelDirectory)
{
    public static readonly string[] CloudModels = ["gpt-transcribe", "gpt-4o-mini-transcribe", "gpt-4o-transcribe"];
    public static readonly string[] LocalModels = ["Base · 148 MB", "Small · 488 MB", "Medium · 1.53 GB", "Large v3 Turbo · 1.62 GB"];
    private string FilePath => Path.Combine(directory, "speech.json");
    public SpeechOptions Current { get; private set; } = Load(Path.Combine(directory, "speech.json"));

    public WhisperModel Model => ModelAt(Current.Local);
    public WhisperModel ModelAt(int index) => new(Path.Combine(modelDirectory, LocalFile(index)));
    public bool Ready => Current.Cloud ? Current.ProtectedKey is not null : Model.Exists;

    public void Save(bool cloud, int local, int remote, string key, string? whisperHints = null)
    {
        if (local < 0 || local >= LocalModels.Length || remote < 0 || remote >= CloudModels.Length) throw new InvalidOperationException("Select a speech model.");
        var hints = whisperHints is null ? Current.WhisperHints : NormalizeHints(whisperHints);
        var protectedKey = Current.ProtectedKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            protectedKey = ProtectKey(key);
        }
        if (cloud && protectedKey is null) throw new InvalidOperationException("Save an OpenAI API key above first; OpenAI recognition turns on when the key is saved.");
        var next = Current with { Cloud = cloud, Local = local, Remote = remote, ProtectedKey = protectedKey, WhisperHints = hints, KeyHint = string.IsNullOrWhiteSpace(key) ? Current.KeyHint : Hint(key.Trim()) };
        SettingsFile.Write(FilePath, next);
        Current = next;
    }

    /// <summary>Saves the one OpenAI API key that recognition, questions and voices share.</summary>
    /// <param name="cloud">The recognizer shown in Settings, applied together with the key (OpenAI cannot be saved before a key exists).</param>
    public void SaveKey(string key, bool? cloud = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Enter an OpenAI API key.");
        var trimmed = key.Trim();
        var next = Current with { ProtectedKey = ProtectKey(trimmed), KeyHint = Hint(trimmed), Cloud = cloud ?? Current.Cloud };
        SettingsFile.Write(FilePath, next);
        Current = next;
    }

    /// <summary>"sk-…AbCd": enough to recognise the saved key without revealing it.</summary>
    internal static string Hint(string key) => key.Length <= 8 ? "saved" : $"{key[..3]}…{key[^4..]}";

    /// <summary>The recognizer chosen for <paramref name="place"/>: null means "same as Speech recognition".</summary>
    public bool? ChoiceFor(RecognitionPlace place) => place switch
    {
        RecognitionPlace.Sessions => Current.SessionCloud,
        RecognitionPlace.Questions => Current.QuestionCloud,
        RecognitionPlace.ComputerSound => Current.ComputerSoundCloud,
        _ => null,
    };

    /// <summary>Whether <paramref name="place"/> records through OpenAI (true) or local Whisper (false).</summary>
    public bool CloudFor(RecognitionPlace place) => Resolve(Current, place);

    internal static bool Resolve(SpeechOptions options, RecognitionPlace place) => place switch
    {
        RecognitionPlace.Sessions => options.SessionCloud ?? options.Cloud,
        RecognitionPlace.Questions => options.QuestionCloud ?? options.Cloud,
        RecognitionPlace.ComputerSound => options.ComputerSoundCloud ?? (options.ProtectedKey is not null || options.Cloud),
        _ => options.Cloud,
    };

    /// <summary>Whether <paramref name="place"/> can record: OpenAI needs a key, Whisper needs the model.</summary>
    internal static bool ReadyFor(SpeechOptions options, RecognitionPlace place, bool modelReady) =>
        Resolve(options, place) ? options.ProtectedKey is not null : modelReady;

    public void SetChoice(RecognitionPlace place, bool? cloud)
    {
        var next = place switch
        {
            RecognitionPlace.Sessions => Current with { SessionCloud = cloud },
            RecognitionPlace.Questions => Current with { QuestionCloud = cloud },
            RecognitionPlace.ComputerSound => Current with { ComputerSoundCloud = cloud },
            _ => Current,
        };
        SettingsFile.Write(FilePath, next);
        Current = next;
    }

    public void RemoveKey()
    {
        var next = Current with { Cloud = false, ProtectedKey = null, KeyHint = null };
        SettingsFile.Write(FilePath, next);
        Current = next;
    }

    /// <summary>Whether local Whisper (model downloaded) or OpenAI (key saved) can be used, regardless of the current choice.</summary>
    public bool ReadyFor(bool cloud) => cloud ? Current.ProtectedKey is not null : Model.Exists;

    public string Describe(bool cloud) => cloud ? CloudModels[Current.Remote] : LocalModels[Current.Local];

    /// <summary>A transcriber for the given engine with the models and key from these settings.</summary>
    public ISpeechTranscriber CreateTranscriber(bool cloud, int? localModel = null) => cloud
        ? new OpenAiTranscriber(CloudModels[Current.Remote], UnprotectKey(Current.ProtectedKey ?? throw new InvalidOperationException("Save an OpenAI API key in Settings → OpenAI API key first.")))
        : new WhisperTranscriber(ModelAt(localModel ?? Current.Local), Current.WhisperPrompt);

    public ISpeechTranscriber CreateTranscriber()
    {
        if (!Current.Cloud) return new WhisperTranscriber(Model, Current.WhisperPrompt);
        return new OpenAiTranscriber(CloudModels[Current.Remote], UnprotectKey(Current.ProtectedKey!));
    }

    internal static string ProtectKey(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key.Trim());
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static string UnprotectKey(string key)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(key), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string NormalizeHints(string hints)
    {
        var terms = hints.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length > 32 || string.Join(", ", terms).Length > 500)
            throw new InvalidOperationException("Use at most 32 Whisper hints and keep the list under 500 characters.");
        return string.Join('\n', terms);
    }

    private static string LocalFile(int index) => index switch
    {
        1 => "ggml-small.bin",
        2 => "ggml-medium.bin",
        3 => "ggml-large-v3-turbo.bin",
        _ => "ggml-base.bin",
    };

    private static SpeechOptions Load(string file)
    {
        try
        {
            var value = SettingsFile.Read<SpeechOptions>(file);
            if (value is { Local: >= 0 and < 4, Remote: >= 0 and < 3 })
                return value.Cloud && value.ProtectedKey is null ? value with { Cloud = false } : value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return new();
    }
}
