using VoiceAgent.Codex;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class LiveTrayvoraAcceptanceTests
{
    [LiveProfileFact]
    public async Task SignedInProfileStreamsAReplyFromCodex()
    {
        var home = Environment.GetEnvironmentVariable("TRAYVORA_LIVE_TEST_PROFILE")!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var output = new Reply();
        var executable = Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE")!;
        var sandbox = Directory.CreateTempSubdirectory("trayvora-live-");
        await using var connection = AppServerConnection.Start(executable, sandbox.FullName, home, CodexSession.DisabledFeatures);
        var status = await CodexSession.RunTurnAsync(connection, sandbox.FullName, Path.Combine(sandbox.FullName, "thread.txt"),
            "Reply with only OK. This is a read-only application smoke test.", output, deadline.Token);
        Assert.Equal(CodexTurnStatus.Completed, status);
        Assert.Contains("OK", output.Text.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [LiveProfileFact]
    public async Task SignedInProfileReadsRepositoryCatalogThroughRevqora()
    {
        var home = Environment.GetEnvironmentVariable("TRAYVORA_LIVE_TEST_PROFILE")!;
        var revqora = Environment.GetEnvironmentVariable("VOICEAGENT_REVQORA_EXE")!;
        var executable = Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE")!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var workspace = Directory.CreateTempSubdirectory("trayvora-mcp-live-");
        await using var connection = AppServerConnection.Start(executable, workspace.FullName, home,
            CodexSession.DisabledFeatures, revqora);
        IReadOnlyList<ToolActivity> tools = [];
        var status = await CodexSession.RunTurnAsync(connection, workspace.FullName, Path.Combine(workspace.FullName, "thread.txt"),
            "Use the Revqora list_repositories tool. If there is an active repository, also call list_pull_requests for it. Do not make any changes. Then reply with only OK.",
            new Reply(), deadline.Token, result => tools = result, revqora);
        Assert.Equal(CodexTurnStatus.Completed, status);
        Assert.Contains(tools, tool => tool == new ToolActivity("MCP · list_repositories", "Completed"));
        var pr = tools.FirstOrDefault(tool => tool.Name == "MCP · list_pull_requests");
        Console.WriteLine($"Revqora PR call: {pr?.Status ?? "not observed"}");
    }

    [LiveProfileFact]
    public async Task ReadsSandboxReadinessWithoutChangingWindowsSettings()
    {
        var home = Environment.GetEnvironmentVariable("TRAYVORA_LIVE_TEST_PROFILE")!;
        var executable = Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE")!;
        var workspace = Directory.CreateTempSubdirectory("trayvora-readiness-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = AppServerConnection.Start(executable, workspace.FullName, home, CodexSession.DisabledFeatures);
        await connection.InitializeAsync(deadline.Token);
        var response = await connection.RequestAsync("windowsSandbox/readiness", null!, deadline.Token);
        var status = response.GetProperty("status").GetString();
        Assert.Contains(status, new[] { "ready", "updateRequired", "notConfigured" });
        Console.WriteLine($"Windows sandbox readiness: {status}");
    }

    private sealed class Reply : IProgress<string>
    {
        public System.Text.StringBuilder Text { get; } = new();
        public void Report(string value) => Text.Append(value);
    }
}

internal sealed class LiveProfileFactAttribute : FactAttribute
{
    public LiveProfileFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRAYVORA_LIVE_TEST_PROFILE")))
        {
            Skip = "An authorized E: test profile is required.";
        }
    }
}
