using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceAgent.Desktop;

/// <summary>The phrase lists the user sees and edits in Settings; one phrase per line.</summary>
internal sealed record KeyPhraseOptions(string[]? Forward = null, string[]? Discuss = null, bool BuiltIn = true)
{
    public string[] ForwardLines => Forward ?? RelayPhrases.Examples;
    public string[] DiscussLines => Discuss ?? RelayPhrases.DiscussExamples;
}

/// <summary>
/// Phrases that send a message on to Claude or Codex ("перешли Клоду") or start a discussion ("обсуди с Кодексом").
/// Every line is a phrase that names the agent; it is found anywhere in a message, ignoring case and extra spaces.
/// Stored in settings\key-phrases.json.
/// </summary>
internal static partial class KeyPhrases
{
    public const int MaxLines = 200, MaxLength = 120;

    /// <summary>
    /// Room for both full lists even when every character is escaped in JSON (\uXXXX, 6 bytes): 2 × 200 × 120 × 6 ≈ 288 KB.
    /// The general 16 KB settings limit would drop a long list on the next start and reset the built-in switch with it.
    /// </summary>
    internal const int MaxFileBytes = 1024 * 1024;
    private static string? _file;

    public static KeyPhraseOptions Current { get; private set; } = new();

    /// <summary>Compiled user phrases with the agent each one names.</summary>
    internal static IReadOnlyList<(Regex Pattern, DiscussionMember Target, string Phrase)> ForwardPatterns { get; private set; } = Compile(new KeyPhraseOptions().ForwardLines);
    internal static IReadOnlyList<(Regex Pattern, DiscussionMember Target, string Phrase)> DiscussPatterns { get; private set; } = Compile(new KeyPhraseOptions().DiscussLines);

    public static void Load(string? settingsDirectory)
    {
        _file = settingsDirectory is null ? null : Path.Combine(settingsDirectory, "key-phrases.json");
        Use(Read(_file));
    }

    internal static KeyPhraseOptions Read(string? file)
    {
        try { return (file is null ? null : SettingsFile.Read<KeyPhraseOptions>(file, MaxFileBytes)) is { } saved ? Clean(saved) : new(); }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return new(); }
    }

    public static void Save(KeyPhraseOptions options)
    {
        var clean = Clean(options);
        if (_file is not null) SettingsFile.Write(_file, clean, MaxFileBytes);
        Use(clean);
    }

    internal static void Use(KeyPhraseOptions options)
    {
        Current = options;
        ForwardPatterns = Compile(options.ForwardLines);
        DiscussPatterns = Compile(options.DiscussLines);
    }

    /// <summary>One phrase per line: blank lines and duplicates are dropped, lines are trimmed and limited in length.</summary>
    internal static string[] Lines(string? text) =>
        (text ?? "").ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line.Length <= MaxLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxLines)
            .ToArray();

    internal static KeyPhraseOptions Clean(KeyPhraseOptions options) => new(
        options.Forward is null ? null : Lines(string.Join("\n", options.Forward)),
        options.Discuss is null ? null : Lines(string.Join("\n", options.Discuss)),
        options.BuiltIn);

    /// <summary>The agent a phrase names, or null when it names neither (such a line is ignored).</summary>
    internal static DiscussionMember? TargetOf(string phrase)
    {
        var claude = AgentName().Matches(phrase).Select(match => match.Value.ToLowerInvariant()).ToArray();
        var isClaude = claude.Any(name => name.StartsWith("claude", StringComparison.Ordinal) || name.StartsWith("клод", StringComparison.Ordinal));
        var isCodex = claude.Any(name => name.StartsWith("codex", StringComparison.Ordinal) || name.StartsWith("кодекс", StringComparison.Ordinal));
        return isClaude == isCodex ? null : isClaude ? DiscussionMember.Claude : DiscussionMember.Codex;
    }

    private static IReadOnlyList<(Regex, DiscussionMember, string)> Compile(IEnumerable<string> lines)
    {
        var list = new List<(Regex, DiscussionMember, string)>();
        foreach (var line in lines)
        {
            if (TargetOf(line) is not { } target) continue;
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
            // A joining word before the phrase ("и", "потом", "and then") goes with it, as in the built-in phrases.
            var pattern = @"(?<![\p{L}\p{N}])(?:(?:а\s+)?(?:потом|затем|после\s+этого|и|і|й|потім|після\s+цього|та|then|and\s+then|and|afterwards)\s+)?"
                + string.Join(@"\s+", words) + @"(?![\p{L}\p{N}])[.!?…]*";
            list.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)), target, line));
        }
        return list;
    }

    /// <summary>The earliest user phrase found in <paramref name="text"/>.</summary>
    internal static (System.Text.RegularExpressions.Match Found, DiscussionMember Target)? Find(IReadOnlyList<(Regex Pattern, DiscussionMember Target, string Phrase)> patterns, string text)
    {
        (System.Text.RegularExpressions.Match, DiscussionMember)? best = null;
        foreach (var (pattern, target, _) in patterns)
        {
            System.Text.RegularExpressions.Match found;
            try { found = pattern.Match(text); }
            catch (RegexMatchTimeoutException) { continue; }
            if (found.Success && (best is null || found.Index < best.Value.Item1.Index)) best = (found, target);
        }
        return best;
    }

    [GeneratedRegex(@"(?<![\p{L}])(?:claude|клод\p{L}*|codex|кодекс\p{L}*)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgentName();

    /// <summary>The Key phrases card of the Settings window.</summary>
    public static Control Card()
    {
        var feedback = TrayvoraTheme.Hint("");
        TextBox Box(string[] lines) => new()
        {
            Text = string.Join(Environment.NewLine, lines), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            MinHeight = 150, MaxHeight = 260, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };
        var forward = Box(Current.ForwardLines);
        var discuss = Box(Current.DiscussLines);
        var builtIn = new CheckBox { Content = "Also catch built-in variations (other word forms, \"потом/затем/then …\", → Claude, #codex)", IsChecked = Current.BuiltIn };
        var save = new Button { Content = "Save phrases" };
        var reset = new Button { Content = "Restore defaults" };
        TrayvoraTheme.StyleControls(forward, discuss, save, reset);
        TrayvoraTheme.Primary(save);
        TrayvoraTheme.Ghost(reset);
        Avalonia.Automation.AutomationProperties.SetName(forward, "Forward phrases");
        Avalonia.Automation.AutomationProperties.SetName(discuss, "Discussion phrases");

        string Ignored(string? text)
        {
            var bad = Lines(text).Where(line => TargetOf(line) is null).ToArray();
            return bad.Length == 0 ? "" : $" Ignored (no single agent name): {string.Join("; ", bad.Take(3))}{(bad.Length > 3 ? "…" : "")}.";
        }
        save.Click += (_, _) =>
        {
            try
            {
                Save(new KeyPhraseOptions(Lines(forward.Text), Lines(discuss.Text), builtIn.IsChecked == true));
                forward.Text = string.Join(Environment.NewLine, Current.ForwardLines);
                discuss.Text = string.Join(Environment.NewLine, Current.DiscussLines);
                feedback.Text = $"Saved: {ForwardPatterns.Count} forward and {DiscussPatterns.Count} discussion phrases." + Ignored(forward.Text) + Ignored(discuss.Text);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { feedback.Text = "Could not save the phrases."; }
        };
        reset.Click += (_, _) =>
        {
            forward.Text = string.Join(Environment.NewLine, RelayPhrases.Examples);
            discuss.Text = string.Join(Environment.NewLine, RelayPhrases.DiscussExamples);
            builtIn.IsChecked = true;
            feedback.Text = "Defaults restored in the boxes. Press Save phrases to keep them.";
        };
        return TrayvoraTheme.Card(
            new TextBlock { Text = "Key phrases", FontSize = 22, FontWeight = FontWeight.SemiBold },
            TrayvoraTheme.Hint("Phrases Trayvora looks for anywhere in a message (typed or dictated) in Sessions. One phrase per line; each must name Claude/Клод… or Codex/Кодекс…. Case and extra spaces do not matter."),
            TrayvoraTheme.Heading("Forward the reply to the other agent"),
            TrayvoraTheme.Hint("The agent answers, then Trayvora passes the answer to the agent named in the phrase. Example: перешли Клоду"),
            forward,
            TrayvoraTheme.Heading("Discuss with the other agent"),
            TrayvoraTheme.Hint("The agents discuss the task, then the lead one reports to you. Example: обсуди с Кодексом"),
            discuss,
            builtIn,
            TrayvoraTheme.Row(save, reset),
            feedback);
    }
}
