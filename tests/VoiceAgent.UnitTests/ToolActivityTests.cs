using System.Text.Json;
using VoiceAgent.Codex;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class ToolActivityTests
{
    [Fact]
    public void StoresOnlyBoundedNamesAndStatusesAndUpdatesExistingItems()
    {
        var tools = new TurnToolActivities();
        Observe("""{"item":{"id":"1","type":"mcpToolCall","tool":"read_document","arguments":{"token":"secret"},"status":"inProgress"}}""", false);
        Observe("""{"item":{"id":"1","type":"mcpToolCall","tool":"read_document","result":"secret","status":"completed"}}""", true);
        Observe("""{"item":{"id":"2","type":"commandExecution","command":"secret","aggregatedOutput":"secret"}}""", false);
        Observe("""{"item":{"id":"3","type":"dynamicToolCall","tool":"Bearer secret","status":"failed"}}""", true);
        Observe("""{"item":{"id":"4","type":"userMessage"}}""", true);

        Assert.Equal(new[]
        {
            new ToolActivity("MCP · read_document", "Completed"),
            new ToolActivity("Command execution", "Unknown"),
            new ToolActivity("Dynamic tool", "Failed"),
        }, tools.Snapshot());

        void Observe(string json, bool completed)
        {
            using var document = JsonDocument.Parse(json);
            tools.Observe(document.RootElement, completed);
        }
    }
}
