using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class DiscussionRoutingTests
{
    [Theory]
    [InlineData("Claude: review the plan", "Claude")]
    [InlineData("codex: please answer", "Codex")]
    [InlineData("Boss: choose an option", "Boss")]
    [InlineData("No recipient", null)]
    [InlineData("I asked Claude: review the plan", null)]
    [InlineData("Claude: ", null)]
    [InlineData("First line\nCodex: code sample", null)]
    public void OnlyAnAddressAtTheStartIsRoutable(string text, string? expected)
    {
        Assert.Equal(expected, DiscussionWindow.Recipient(text)?.ToString());
    }
}