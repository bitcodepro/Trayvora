using System.Text.Json;
using VoiceAgent.Codex;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class CodexTurnEventsTests
{
    [Fact]
    public void SeparatesPublicActivityFromFinalAnswersAndKeepsToolIdentity()
    {
        var replies = new List<string>();
        var steps = new List<CodexActivity>();
        var reducer = new CodexTurnEvents(null, new InlineProgress<string>(replies.Add), new InlineProgress<CodexActivity>(steps.Add));

        Observe("item/agentMessage/delta", new { itemId = "notes", delta = "I will check." });
        Observe("item/started", new { item = new { id = "tool", type = "commandExecution", command = "secret command" } });
        Observe("item/completed", new { item = new { id = "notes", type = "agentMessage", phase = "commentary", text = "I will check." } });
        Observe("item/reasoning/textDelta", new { itemId = "summary", delta = "private reasoning" });
        Observe("item/reasoning/summaryTextDelta", new { itemId = "summary", summaryIndex = 0, delta = "Compared the outputs." });
        Observe("item/completed", new { item = new { id = "tool", type = "commandExecution", status = "failed", aggregatedOutput = "secret output" } });
        Observe("item/started", new { item = new { id = "answer", type = "agentMessage", phase = "final_answer" } });
        Observe("item/agentMessage/delta", new { itemId = "answer", delta = "Final" });
        Observe("item/completed", new { item = new { id = "answer", type = "agentMessage", phase = "final_answer", text = "Final answer." } });

        Assert.Equal("Final answer.", reducer.Answer);
        Assert.All(replies, reply => Assert.DoesNotContain("check", reply));
        Assert.Contains(steps, step => step.Label == "Commentary · I will check.");
        Assert.Contains(steps, step => step.Label == "Summary · Compared the outputs.");
        Assert.Equal(["running", "failed"], steps.Where(step => step.Id == "tool:tool").Select(step => step.Status));
        Assert.DoesNotContain(steps, step => step.Label.Contains("secret") || step.Label.Contains("private"));

        void Observe(string method, object data) => Assert.True(reducer.Observe(method, JsonSerializer.SerializeToElement(data)));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
