using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

[Collection("KeyPhrases")]
public sealed class RelayPhraseTests
{
    [Theory]
    [InlineData("Проверь сборку. Перешли Клоду", "Claude", "Проверь сборку.")]
    [InlineData("проверь пожалуйста сборку перешли клоду", "Claude", "проверь пожалуйста сборку")]
    [InlineData("Сделай план, потом отправь ответ Кодексу.", "Codex", "Сделай план")]
    [InlineData("Сделай план и передай результат Claude", "Claude", "Сделай план")]
    [InlineData("Скажи, что думаешь, покажи Кодексу", "Codex", "Скажи, что думаешь")]
    [InlineData("Оцени код, а потом перешли свой ответ Клоду!", "Claude", "Оцени код")]
    [InlineData("Підготуй план, перешли відповідь Клоду", "Claude", "Підготуй план")]
    [InlineData("Перевір код і надішли Кодексу", "Codex", "Перевір код")]
    [InlineData("review this, then send it to Codex", "Codex", "review this")]
    [InlineData("Check the plan and forward to Claude!", "Claude", "Check the plan")]
    [InlineData("What do you think? Pass your answer on to Codex.", "Codex", "What do you think?")]
    [InlineData("Draft the release notes, tell Claude", "Claude", "Draft the release notes")]
    [InlineData("Draft the release notes → Claude", "Claude", "Draft the release notes")]
    [InlineData("Draft the release notes -> Codex", "Codex", "Draft the release notes")]
    [InlineData("Draft the release notes #codex", "Codex", "Draft the release notes")]
    [InlineData("Перешли Клоду, что ты думаешь о плане", "Claude", "что ты думаешь о плане")]
    [InlineData("Проверь сборку и передай ответ Кодексу, потом опиши риски", "Codex", "Проверь сборку потом опиши риски")]
    [InlineData("Оцени код → Claude и коротко", "Claude", "Оцени код и коротко")]
    [InlineData("Review this and forward to Claude, keep it short", "Claude", "Review this keep it short")]
    public void PhrasesAnywhereNameTheAgentToPassTheReplyTo(string text, string target, string rest)
    {
        var match = RelayPhrases.Detect(text);
        Assert.NotNull(match);
        Assert.Equal(target, match.Target.ToString());
        Assert.Equal(rest, match.Text);
    }

    [Theory]
    [InlineData("Перешли Клоду")]                                  // nothing to send
    [InlineData("Claude уже знает про это")]
    [InlineData("Я отправил Клоду письмо вчера, что думаешь?")]    // past tense is a story, not a request
    [InlineData("Перешли Клодовскому боту")]                        // a longer word, not the agent's name
    [InlineData("Расскажи про Codex")]
    [InlineData("Send me the logs")]
    [InlineData("Compare Claude and Codex")]
    [InlineData("")]
    public void OrdinaryTextIsNotARelayRequest(string text) => Assert.Null(RelayPhrases.Detect(text));

    [Fact]
    public void EveryShownExampleIsRecognized()
    {
        foreach (var example in RelayPhrases.Examples)
            Assert.NotNull(RelayPhrases.Detect("Сделай краткий обзор. " + example));
    }

    [Theory]
    [InlineData("Найди класс Client, посмотри поля и обсуди с Кодексом, как лучше решить эту задачу", "Codex")]
    [InlineData("Найди класс Client и обсуди с Клодом решение", "Claude")]
    [InlineData("обсуди с кодексом решение", "Codex")]
    [InlineData("Предложи план и обсудите его с Claude", "Claude")]
    [InlineData("Посоветуйся с Клодом, прежде чем менять схему", "Claude")]
    [InlineData("Согласуй с Кодексом подход к миграции", "Codex")]
    [InlineData("Знайди клас і обговори з Клодом рішення", "Claude")]
    [InlineData("Look at the Client class and discuss with Codex how to fix it", "Codex")]
    [InlineData("Draft a plan, then talk it over with Claude", "Claude")]
    [InlineData("Please consult Codex before changing the schema", "Codex")]
    [InlineData("Work it out with Claude and tell me the result", "Claude")]
    public void DiscussPhrasesNameThePartner(string text, string partner) =>
        Assert.Equal(partner, RelayPhrases.DetectDiscussion(text)?.ToString());

    [Theory]
    [InlineData("Перешли Клоду этот план")]
    [InlineData("Что думает Claude о Codex?")]
    [InlineData("Мы обсудили с командой новый релиз")]
    [InlineData("Codex, discuss the plan")]
    [InlineData("Отправь Кодексу")]
    public void OtherTextIsNotADiscussion(string text) => Assert.Null(RelayPhrases.DetectDiscussion(text));

    [Fact]
    public void EveryShownDiscussExampleIsRecognized()
    {
        foreach (var example in RelayPhrases.DiscussExamples)
            Assert.NotNull(RelayPhrases.DetectDiscussion("Посмотри класс Client и " + example));
    }
}
