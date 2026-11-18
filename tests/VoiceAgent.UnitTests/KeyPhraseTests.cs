using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

// Changes the app-wide phrase lists, so it must not run alongside the other phrase tests.
[Collection("KeyPhrases")]
public sealed class KeyPhraseTests : IDisposable
{
    public void Dispose() => KeyPhrases.Use(new KeyPhraseOptions());

    [Fact]
    public void OwnPhrasesAreFoundAnywhereAndRemovedFromTheText()
    {
        KeyPhrases.Use(new KeyPhraseOptions(["закинь   Клоду"], ["прокачай идею с кодексом"]));
        var match = RelayPhrases.Detect("Проверь сборку, ЗАКИНЬ клоду, и опиши риски")!;
        Assert.Equal(DiscussionMember.Claude, match.Target);
        Assert.Equal("Проверь сборку и опиши риски", match.Text);
        Assert.Equal(DiscussionMember.Codex, RelayPhrases.DetectDiscussion("Посмотри класс и прокачай идею  с Кодексом"));
        // Only whole words: "Клоду" inside a longer word does not count.
        Assert.Null(KeyPhrases.Find(KeyPhrases.ForwardPatterns, "закинь клодуx"));
    }

    [Fact]
    public void BuiltInVariationsCanBeTurnedOff()
    {
        KeyPhrases.Use(new KeyPhraseOptions(["закинь Клоду"], [], BuiltIn: false));
        Assert.Null(RelayPhrases.Detect("Сделай обзор, потом перешли Кодексу"));
        Assert.Null(RelayPhrases.DetectDiscussion("обсуди с Клодом план"));
        KeyPhrases.Use(new KeyPhraseOptions(["закинь Клоду"], []));
        Assert.Equal(DiscussionMember.Codex, RelayPhrases.Detect("Сделай обзор, потом перешли Кодексу")!.Target);
    }

    [Fact]
    public void EachLineIsOnePhraseAndMustNameOneAgent()
    {
        Assert.Equal(["перешли Клоду", "send to Codex"], KeyPhrases.Lines("перешли Клоду\r\n\n  send to Codex  \nПЕРЕШЛИ КЛОДУ\n"));
        Assert.Equal(DiscussionMember.Claude, KeyPhrases.TargetOf("обговори з Клодом"));
        Assert.Equal(DiscussionMember.Codex, KeyPhrases.TargetOf("ask CODEX"));
        Assert.Null(KeyPhrases.TargetOf("перешли дальше"));
        Assert.Null(KeyPhrases.TargetOf("Claude и Codex вместе"));
    }

    [Fact]
    public void FullListsSurviveARestartWithTheBuiltInSwitch()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-phrases-");
        try
        {
            KeyPhrases.Load(folder.FullName);
            string Line(int i, string agent) => $"{i:D3} " + new string('ж', KeyPhrases.MaxLength - 14) + " " + agent;
            var forward = Enumerable.Range(0, KeyPhrases.MaxLines).Select(i => Line(i, "Клоду")).ToArray();
            var discuss = Enumerable.Range(0, KeyPhrases.MaxLines).Select(i => Line(i, "Кодексом")).ToArray();
            KeyPhrases.Save(new KeyPhraseOptions(forward, discuss, BuiltIn: false));
            Assert.True(new FileInfo(Path.Combine(folder.FullName, "key-phrases.json")).Length > SettingsFile.DefaultMaxBytes);

            KeyPhrases.Use(new KeyPhraseOptions());
            KeyPhrases.Load(folder.FullName);
            Assert.False(KeyPhrases.Current.BuiltIn);
            Assert.Equal(forward, KeyPhrases.Current.ForwardLines);
            Assert.Equal(discuss, KeyPhrases.Current.DiscussLines);
        }
        finally
        {
            KeyPhrases.Load(null);
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void SettingsThatCouldNotBeReadBackAreNotWritten()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-settings-");
        try
        {
            var file = Path.Combine(folder.FullName, "big.json");
            Assert.Throws<IOException>(() => SettingsFile.Write(file, new string('x', SettingsFile.DefaultMaxBytes)));
            Assert.False(File.Exists(file));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void DefaultListsShowTheBuiltInExamplesAndADamagedFileFallsBack()
    {
        Assert.Equal(RelayPhrases.Examples, new KeyPhraseOptions().ForwardLines);
        Assert.Equal(RelayPhrases.DiscussExamples, new KeyPhraseOptions().DiscussLines);
        var folder = Directory.CreateTempSubdirectory("trayvora-phrases-");
        try
        {
            var file = Path.Combine(folder.FullName, "key-phrases.json");
            File.WriteAllText(file, "{ broken");
            Assert.Equal(new KeyPhraseOptions().ForwardLines, KeyPhrases.Read(file).ForwardLines);
        }
        finally { folder.Delete(recursive: true); }
    }
}
