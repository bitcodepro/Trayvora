using VoiceAgent.Application;
using VoiceAgent.Desktop;
using VoiceAgent.Speech;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class VoiceSettingsTests
{
    [Fact]
    public void ConflictingShortcutDoesNotSaveOrReleaseWorkingBindings()
    {
        var original = VoiceHotkeys.Default(HotkeyShortcut.Default);
        var unavailable = new HotkeyShortcut(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'K');
        var created = new Dictionary<HotkeyShortcut, TestHotkey>();
        var saved = false;
        using var bindings = new VoiceHotkeyBindings(key => created[key] = new TestHotkey(key != unavailable), _ => saved = true);
        Assert.Null(bindings.Apply(original, persist: false));
        Assert.NotNull(bindings.Apply(original with { Send = unavailable }, persist: true));
        Assert.NotNull(bindings.Apply(original with { Send = original.Preview }, persist: true));
        Assert.False(saved);
        Assert.False(created[original.Send].Disposed);
        Assert.True(created[unavailable].Disposed);
    }

    [Fact]
    public void ExistingShortcutsGainADistinctDictationBinding()
    {
        var existing = System.Text.Json.JsonSerializer.Deserialize<VoiceHotkeys>(
            """{"Preview":{"Modifiers":7,"Key":68},"Background":{"Modifiers":1,"Key":66},"Send":{"Modifiers":1,"Key":88}}""")!;
        Assert.Equal("Ctrl+Alt+Shift+D", existing.Preview.ToString());
        Assert.Equal(8, existing.Entries.Values.Distinct().Count());
        Assert.True(existing.Entries[VoiceAction.Dictate].IsValid);
    }

    [Fact]
    public void PlaybackCanFollowRecognitionKeyWithoutCopyingIt()
    {
        var existing = System.Text.Json.JsonSerializer.Deserialize<PlaybackOptions>(
            """{"Cloud":true,"ProtectedKey":"existing-playback-key"}""")!;
        Assert.Equal("existing-playback-key", existing.ResolveKey("recognition-key"));
        Assert.Equal("recognition-key", new PlaybackOptions().ResolveKey("recognition-key"));

        var shared = existing with { KeySource = PlaybackKeySource.Recognition };
        Assert.Equal("recognition-key", shared.ResolveKey("recognition-key"));
        Assert.Equal("replacement", shared.ResolveKey("replacement"));
        Assert.Null(shared.ResolveKey(null));
        Assert.Equal("existing-playback-key", (shared with { KeySource = PlaybackKeySource.Separate }).ResolveKey(null));
    }

    [Fact]
    public void CloudKeyIsEncryptedAndLocalBaseIsTheDefault()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("trayvora-speech-");
        var file = Path.Combine(directory.FullName, "speech.json");
        try
        {
            var speech = new SpeechSettings(directory.FullName, directory.FullName);
            Assert.False(speech.Current.Cloud);
            Assert.Equal("ggml-base.bin", Path.GetFileName(speech.Model.Path));
            Assert.Throws<InvalidOperationException>(() => { if (OperatingSystem.IsWindows()) speech.Save(true, 0, 0, ""); });
            speech.Save(true, 1, 0, "test-key-trayvora");
            Assert.DoesNotContain("test-key-trayvora", File.ReadAllText(file));
            var reopened = new SpeechSettings(directory.FullName, directory.FullName);
            Assert.True(reopened.Ready);
            Assert.IsType<VoiceAgent.Speech.OpenAiTranscriber>(reopened.CreateTranscriber());
            reopened.RemoveKey();
            Assert.False(reopened.Current.Cloud);
            Assert.Null(reopened.Current.ProtectedKey);
            Assert.Equal("ggml-small.bin", Path.GetFileName(reopened.Model.Path));
        }
        finally
        {
            File.Delete(file);
            directory.Delete();
        }
    }

    [Fact]
    public void WhisperHintsPersistAcrossProviderChangesAndCanBeCleared()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("trayvora-hints-");
        var file = Path.Combine(directory.FullName, "speech.json");
        try
        {
            File.WriteAllText(file, """{"Cloud":false,"Local":0,"Remote":0}""");
            var speech = new SpeechSettings(directory.FullName, directory.FullName);
            Assert.Contains("запушать", speech.Current.WhisperPrompt);
            Assert.Equal("ggml-base.bin", Path.GetFileName(speech.Model.Path));
            speech.Save(false, 0, 0, "", " Revqora\r\n\r\nrevqora\nсоздать бранчу\n repo-one ");
            speech.Save(true, 0, 0, "test-key-trayvora");
            var reopened = new SpeechSettings(directory.FullName, directory.FullName);
            Assert.IsType<VoiceAgent.Speech.OpenAiTranscriber>(reopened.CreateTranscriber());
            reopened.RemoveKey();
            Assert.Equal("Revqora\nсоздать бранчу\nrepo-one", reopened.Current.EffectiveWhisperHints);
            Assert.Equal("Revqora, создать бранчу, repo-one", reopened.Current.WhisperPrompt);
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (OperatingSystem.IsWindows()) reopened.Save(false, 0, 0, "", new string('x', 501));
            });
            reopened.Save(false, 0, 0, "", "");
            Assert.Empty(new SpeechSettings(directory.FullName, directory.FullName).Current.WhisperPrompt);
        }
        finally
        {
            File.Delete(file);
            directory.Delete();
        }
    }

    private sealed class TestHotkey(bool registered) : IGlobalHotkeyService
    {
        public event EventHandler? Pressed { add { } remove { } }
        public bool Disposed { get; private set; }
        public HotkeyRegistration Start() => new(registered, registered ? 0 : HotkeyRegistration.AlreadyRegistered);
        public void Dispose() => Disposed = true;
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void EachEngineReportsItsOwnReadiness()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Directory.CreateTempSubdirectory("trayvora-engines-");
        try
        {
            var settings = new SpeechSettings(folder.FullName, folder.FullName);
            Assert.False(settings.ReadyFor(cloud: true));
            Assert.False(settings.ReadyFor(cloud: false));
            Assert.IsType<WhisperTranscriber>(settings.CreateTranscriber(cloud: false));
            Assert.Throws<InvalidOperationException>(() => settings.CreateTranscriber(cloud: true));

            settings.Save(cloud: false, local: 0, remote: 2, key: "sk-test-not-real");
            Assert.True(settings.ReadyFor(cloud: true));
            Assert.IsType<OpenAiTranscriber>(settings.CreateTranscriber(cloud: true));
            Assert.Equal("gpt-4o-transcribe", settings.Describe(cloud: true));
            Assert.Equal("Base · 148 MB", settings.Describe(cloud: false));
            Assert.False(settings.Current.Cloud); // switching on the page does not change Settings
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
