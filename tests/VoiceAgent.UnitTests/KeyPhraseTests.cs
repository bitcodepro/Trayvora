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
