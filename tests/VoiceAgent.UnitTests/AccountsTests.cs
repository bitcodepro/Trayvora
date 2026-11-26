using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class AccountsTests
{
    [Fact]
    public void ClaudeStatusShowsEmailPlanAndExpiry()
    {
        var until = DateTimeOffset.Now.AddDays(20);
        var status = AccountProviders.ParseClaudeStatus(0,
            $$"""{"loggedIn":true,"authMethod":"claude.ai","email":"me@example.com","subscriptionType":"max","loginExpiresAt":"{{until:O}}"}""");
        Assert.True(status.SignedIn);
        Assert.Equal($"me@example.com · Max · until {until.ToLocalTime():dd.MM.yyyy}", status.Summary);
    }

    [Fact]
    public void ClaudeStatusRecognizesSignedOutExpiredAndUnreadableOutput()
    {
        Assert.True(AccountProviders.ParseClaudeStatus(0, """{"loggedIn":true,"loginExpiresAt":null}""").SignedIn);
        Assert.True(AccountProviders.ParseClaudeStatus(1, """{"loggedIn":false}""").NeedsLogin);
        Assert.False(AccountProviders.ParseClaudeStatus(1, "CLI unavailable").NeedsLogin);
        var expired = AccountProviders.ParseClaudeStatus(0,
            $$"""{"loggedIn":true,"email":"me@example.com","loginExpiresAt":"{{DateTimeOffset.Now.AddDays(-1):O}}"}""");
        Assert.False(expired.SignedIn);
        Assert.Contains("sign-in expired", expired.Summary);
        Assert.False(AccountProviders.ParseClaudeStatus(1, "Error: something went wrong").SignedIn);
        Assert.False(AccountProviders.ParseClaudeStatus(0, "{ broken").SignedIn);
    }
}
