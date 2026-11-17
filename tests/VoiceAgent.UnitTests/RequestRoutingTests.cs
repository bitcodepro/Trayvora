using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class RequestRoutingTests
{
    [Theory]
    [InlineData("Какая текущая ветка?", (int)RequestRoute.AppMcp)]
    [InlineData("Яка поточна гілка?", (int)RequestRoute.AppMcp)]
    [InlineData("Current branch", (int)RequestRoute.AppMcp)]
    [InlineData("Какая текущая ветка через мой MCP?", (int)RequestRoute.AppMcp)]
    [InlineData("Как сделать git push?", (int)RequestRoute.AppMcp)]
    [InlineData("Прочитай репозитории через MCP", (int)RequestRoute.AppMcp)]
    public void SelectsOneRoute(string text, int expected) =>
        Assert.Equal((RequestRoute)expected, RequestRouting.Select(text, RequestRoute.Auto));

    [Fact]
    public void ExplicitMcpNeverFallsBackToLocal()
    {
        Assert.Equal(RequestRoute.AppMcp, RequestRouting.Select("Current branch", RequestRoute.AppMcp));
        Assert.Equal(RequestRoute.AppMcp, RequestRouting.Select("Use MCP", RequestRoute.LocalGit));
    }
}
