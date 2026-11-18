using System.Text.Json;
using VoiceAgent.Codex;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class LiveStepsTests
{
    private static string? Describe(string json) => TurnToolActivities.Describe(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void CodexStepsSayWhatIsRunningOrEdited()
    {
        Assert.Equal("Running · git status --short",
            Describe("""{"item":{"type":"commandExecution","id":"1","command":"\"C:\\\\WINDOWS\\\\powershell.exe\" -Command 'git status --short'"}}"""));
        Assert.Equal("Running · dotnet test", Describe("""{"item":{"type":"commandExecution","id":"1","command":["dotnet","test"]}}"""));
        Assert.Equal("Editing · App.cs, View.cs", Describe("""{"item":{"type":"fileChange","id":"2","changes":[{"path":"C:\\src\\App.cs"},{"path":"src/View.cs"}]}}"""));
        Assert.Equal("Searching the web · avalonia styles", Describe("""{"item":{"type":"webSearch","id":"3","query":"avalonia styles"}}"""));
        Assert.Equal("Thinking", Describe("""{"item":{"type":"reasoning","id":"4"}}"""));
        Assert.Null(Describe("""{"item":{"type":"agentMessage","id":"5"}}"""));
    }

    [Fact]
    public void LongCommandsAreShortenedToOneLine()
    {
        var json = JsonSerializer.Serialize(new { item = new { type = "commandExecution", id = "1", command = "echo " + new string('a', 200) + "\nnext" } });
        var step = Describe(json)!;
        Assert.DoesNotContain('\n', step);
        Assert.EndsWith("…", step);
    }

    [Fact]
    public void StepsKeepTheLastOnesAndMergeDetailIntoTheBareToolName()
    {
        var stream = new ReplyStream();
        stream.Activity("Read");
        stream.Activity("Read · App.cs");
        stream.Activity(null);
        stream.Activity("Thinking");
        for (var i = 0; i < 10; i++) stream.Activity($"Bash · step {i}");
        var (recent, total) = stream.Steps;
        Assert.Equal(12, total);
        Assert.Equal(ReplyStream.StepsShown, recent.Count);
        Assert.Equal("Bash · step 9", recent[^1]);

        var text = ReplyStream.FormatSteps(recent, total, running: true);
        Assert.StartsWith("… 4 earlier steps", text);
        Assert.EndsWith("▸ Bash · step 9", text);
        Assert.DoesNotContain("▸", ReplyStream.FormatSteps(recent, total, running: false));
        Assert.Equal("", ReplyStream.FormatSteps([], 0, running: true));
    }
}

public sealed class ThemeTests
{
    [Fact]
    public void BadSizesAndColoursFallBackToDefaults()
    {
        var clean = Appearance.Clean(new AppearanceOptions(
            new TextLook("Consolas", 16.3, "#a9c8ff"),
            new TextLook("  ", 200, "red"),
            new TextLook(null, 9, "#12345")));
        Assert.Equal(new TextLook("Consolas", 16.5, "#A9C8FF"), clean.Chat);
        Assert.Equal(new TextLook(), clean.Input);
        Assert.Equal(new TextLook(), clean.Interface);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Chat\":{\"Size\":\"big\"}}")]
    [InlineData("[]")]
    public void ADamagedThemeFileGivesTheDefaultLook(string content)
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-theme-");
        try
        {
            var file = Path.Combine(folder.FullName, "appearance.json");
            File.WriteAllText(file, content);
            Assert.Equal(new AppearanceOptions(), Appearance.Read(file));
            Assert.Equal(new AppearanceOptions(), Appearance.Read(Path.Combine(folder.FullName, "missing.json")));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void ThemeRoundTripsThroughJson()
    {
        var options = new AppearanceOptions(new TextLook("Georgia", 18, "#FFE08A"), null, new TextLook(Size: 15));
        var back = JsonSerializer.Deserialize<AppearanceOptions>(JsonSerializer.Serialize(options))!;
        Assert.Equal(options.ChatLook, back.ChatLook);
        Assert.Equal(options.InterfaceLook, back.InterfaceLook);
        Assert.Equal(new TextLook(), back.InputLook);
    }
}
