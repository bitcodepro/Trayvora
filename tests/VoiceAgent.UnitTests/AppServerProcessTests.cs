using System.Diagnostics;
using System.Text;
using VoiceAgent.Codex;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class AppServerProcessTests
{
    private static ProcessStartInfo StartFixture(string scenario)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add(typeof(AppServerFixture.Program).Assembly.Location);
        start.ArgumentList.Add(scenario);
        return start;
    }

    [Fact]
    public async Task DeniesRepeatedApprovalRequestsWithoutLeakingTheirPayload()
    {
        var start = StartFixture("approvals");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = AppServerConnection.Start(start);
        var result = await connection.RequestAsync("test/request", new { }, deadline.Token);
        Assert.True(result.GetProperty("ok").GetBoolean());
        await using var events = connection.ReadEventsAsync(deadline.Token).GetAsyncEnumerator();
        foreach (var category in new[] { "Command execution", "File change", "Permission expansion", "MCP confirmation" })
        {
            Assert.True(await events.MoveNextAsync());
            Assert.Equal(category, events.Current.GetProperty("voiceAgentBlocked").GetString());
            Assert.DoesNotContain("test-secret-9876", events.Current.GetRawText());
        }

        var error = await Assert.ThrowsAsync<IOException>(() => events.MoveNextAsync().AsTask());
        Assert.DoesNotContain("test-secret-9876", error.Message);
    }

    [Fact]
    public async Task EmergencyStopTerminatesAnOwnedHungChild()
    {
        var start = StartFixture("hang");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = AppServerConnection.Start(start);
        var pending = connection.InitializeAsync(deadline.Token);
        connection.Abort();
        await Assert.ThrowsAnyAsync<IOException>(() => pending);
    }

    [Fact]
    public async Task RunsHandshakeRoutesNotificationAndObservesChildExit()
    {
        var start = StartFixture("handshake");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = AppServerConnection.Start(start);
        await connection.InitializeAsync(deadline.Token);
        var result = await connection.RequestAsync("test/request", new { }, deadline.Token);
        Assert.True(result.GetProperty("ok").GetBoolean());
        await using var events = connection.ReadEventsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("test/event", events.Current.GetProperty("method").GetString());
        await Assert.ThrowsAsync<IOException>(() => events.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task MalformedJsonIsReportedAsInvalidDataToTheWaitingRequest()
    {
        var start = StartFixture("malformed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = AppServerConnection.Start(start);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            connection.RequestAsync("config/read", new { }, deadline.Token));
        Assert.Equal("Invalid JSON from Codex App Server.", error.Message);
    }

    [Fact]
    public async Task InstalledServerRejectsUnsignedProfileWhenSmokeTestIsEnabled()
    {
        var directory = Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_UNSIGNED_SMOKE_DIR");
        if (directory is null)
        {
            return;
        }

        var output = new Progress<string>(_ => throw new InvalidOperationException("Unexpected model output."));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CodexSession(directory).RunAsync("This prompt must never be sent.", output, deadline.Token));
        Assert.Contains("Sign in with ChatGPT", error.Message);
    }

    [Fact]
    public async Task InstalledServerReportsOnlyTheAllowedRevqoraTools()
    {
        var home = Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_UNSIGNED_SMOKE_DIR");
        var revqora = Environment.GetEnvironmentVariable("VOICEAGENT_REVQORA_EXE");
        if (home is null || revqora is null) return;
        var workspace = Path.Combine(home, "workspace");
        Directory.CreateDirectory(workspace);
        var executable = Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE")!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var session = new CodexSession(home);
        Assert.False(await session.VerifySessionMcpAsync(false, timeout.Token));
        Assert.True(await session.VerifySessionMcpAsync(true, timeout.Token));
        var invalidHome = Path.Combine(home, "unexpected-mcp");
        Directory.CreateDirectory(invalidHome);
        await File.WriteAllTextAsync(Path.Combine(invalidHome, "config.toml"),
            "[mcp_servers.other]\ncommand = \"should-not-run.exe\"\n", timeout.Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CodexSession(invalidHome).VerifySessionMcpAsync(false, timeout.Token));
        Assert.Contains("Unexpected MCP configuration", error.Message);
        await using var connection = AppServerConnection.Start(executable, workspace, home, CodexSession.DisabledFeatures, revqora);
        await connection.InitializeAsync(timeout.Token);
        await CodexSession.VerifyRevqoraCatalogAsync(connection, null, timeout.Token);
    }
}
