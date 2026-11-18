using System.Text.RegularExpressions;

namespace VoiceAgent.Desktop;

/// <summary>
/// Key phrases anywhere in a message that ask Trayvora to pass the agent's reply on to the other agent,
/// for example "… перешли Клоду" or "… forward it to Codex". Trayvora detects them itself, so it works the
/// same for typed and dictated text and does not depend on the agent noticing the request.
/// </summary>
internal static partial class RelayPhrases
{
    /// <summary>Examples shown to the user (the detector accepts many more variations).</summary>
    public static readonly string[] Examples =
    [
        "перешли Клоду", "передай ответ Кодексу", "потом отправь Claude",
        "перешли відповідь Клоду",
        "forward to Claude", "then send it to Codex",
        "→ Claude", "#codex",
    ];

    public sealed record Match(DiscussionMember Target, string Text, string Phrase);

    /// <summary>
    /// The agent named in a relay phrase anywhere in <paramref name="text"/> ("перешли Клоду, что думаешь о плане",
    /// "проверь сборку и передай ответ Кодексу, потом опиши риски"), and the text without the phrase.
    /// </summary>
    public static Match? Detect(string text)
    {
        var trimmed = text.Trim();
        System.Text.RegularExpressions.Match found;
        DiscussionMember target;
        if (KeyPhrases.Find(KeyPhrases.ForwardPatterns, trimmed) is { } own)
        {
            (found, target) = own;
        }
        else
        {
            if (!KeyPhrases.Current.BuiltIn) return null;
            found = Spoken().Match(trimmed);
            if (!found.Success) found = Symbol().Match(trimmed);
            if (!found.Success) return null;
            var name = found.Groups["target"].Value.ToLowerInvariant();
            target = name.StartsWith("claude", StringComparison.Ordinal) || name.StartsWith("клод", StringComparison.Ordinal)
                ? DiscussionMember.Claude : DiscussionMember.Codex;
        }
        var before = trimmed[..found.Index].TrimEnd(' ', ',', ';', ':', '—', '-', '–');
        var after = trimmed[(found.Index + found.Length)..].TrimStart(' ', ',', ';', ':', '—', '-', '–', '.', '!', '?', '…');
        var rest = before.Length == 0 ? after : after.Length == 0 ? before : before + " " + after;
        return string.IsNullOrWhiteSpace(rest) ? null : new Match(target, rest, found.Value.Trim());
    }

    /// <summary>Examples of phrases that start a discussion between the agents.</summary>
    public static readonly string[] DiscussExamples =
    [
        "обсуди с Клодом", "обсуди с Кодексом решение", "посоветуйся с Клодом", "согласуй с Кодексом",
        "обговори з Клодом", "discuss with Claude", "consult Codex",
    ];

    /// <summary>
    /// The agent named in a "discuss with …" phrase anywhere in <paramref name="text"/>, for example
    /// "найди класс Client, посмотри поля и обсуди с Кодексом, как лучше решить задачу".
    /// </summary>
    public static DiscussionMember? DetectDiscussion(string text)
    {
        if (KeyPhrases.Find(KeyPhrases.DiscussPatterns, text) is { } own) return own.Target;
        if (!KeyPhrases.Current.BuiltIn) return null;
        var found = Discuss().Match(text);
        if (!found.Success) return null;
        var name = found.Groups["target"].Value.ToLowerInvariant();
        return name.StartsWith("claude", StringComparison.Ordinal) || name.StartsWith("клод", StringComparison.Ordinal)
            ? DiscussionMember.Claude : DiscussionMember.Codex;
    }

    // verb [object] with NAME — anywhere in the message.
    [GeneratedRegex(
        @"(?<![\p{L}])" +
        @"(?:(?:обсуди(?:те)?|обсудить|обговори(?:те)?|обговорити|согласуй(?:те)?|согласовать|узгодь(?:те)?|" +
        @"посоветуйся|посоветуйтесь|посовещайся|порадься|порадьтеся|подумай(?:те)?|поразмышляй|" +
        @"discuss|talk|work|figure|agree|brainstorm|check)" +
        @"(?:\s+(?:это|его|решение|план|задачу|вариант(?:ы)?|подход|це|рішення|it|this|that|the\s+plan|the\s+solution|out|over|it\s+out|it\s+over|this\s+out|things\s+out))?" +
        @"\s+(?:с|со|з|із|with|together\s+with)" +
        @"|consult|ask\s+for\s+(?:a\s+)?(?:second\s+opinion|review)\s+from)" +
        @"\s+" + Names + @"(?![\p{L}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Discuss();

    private const string Names = @"(?<target>claude|клод(?:у|а|ом)?|codex|кодекс(?:у|а|ом)?)";

    // [connector] verb [object] [to] NAME — anywhere in the message.
    [GeneratedRegex(
        @"(?:^|[\s,.;:!?…—–-])" +
        @"(?:(?:а\s+)?(?:потом|затем|после\s+этого|и|і|й|потім|після\s+цього|та|then|and\s+then|and|afterwards)\s+)?" +
        @"(?:перешли(?:те)?|переслать|передай(?:те)?|передать|отправь(?:те)?|отправить|скинь(?:те)?|покажи(?:те)?|надішли(?:ть)?|переправ|" +
        @"forward|send|pass|show|give|relay|tell)" +
        @"(?:\s+(?:(?:свой|свою|твой|этот|это|его|её|ее|свій|свою|it|this|that|the|your)\s+)?(?:ответ|результат|это|его|відповідь|результат|answer|reply|result|response|it|this|that))?" +
        @"(?:\s+on)?(?:\s+(?:to|для|на))?\s+" + Names + @"(?![\p{L}\p{N}])[.!?…]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Spoken();

    // Short written forms: "→ Claude", "-> Codex", ">> Claude", "#claude".
    [GeneratedRegex(@"(?:→|->|>>|#)\s*" + Names + @"(?![\p{L}\p{N}])[.!?…]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Symbol();
}
