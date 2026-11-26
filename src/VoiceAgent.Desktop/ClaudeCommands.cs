namespace VoiceAgent.Desktop;

internal sealed record CommandChoice(string Command, string Description)
{
    public override string ToString() => Command.StartsWith("/resume ", StringComparison.Ordinal)
        ? $"{Description}   ·   {Command[8..]}" : $"{Command}   {UiLanguage.T(Description)}";
}

internal static class ClaudeCommands
{
    internal static readonly string[] Efforts = ["auto", "low", "medium", "high", "xhigh", "max"];
    internal static readonly string[] Models = ["auto", "haiku", "sonnet", "opus"];
    internal static readonly CommandChoice[] Menu =
    [
        new("/mcp", "MCP servers and connection status"),
        new("/resume", "Find and select a Claude chat"),
        new("/effort", "Choose reasoning effort for this session"),
        new("/model", "Choose the Claude model for this session"),
        new("/status", "Claude account status"),
        new("/plugins", "Installed Claude plugins"),
        new("/version", "Installed Claude Code version"),
        new("/help", "Commands available in Trayvora"),
        new("/cli-help", "Full help from the installed CLI")
    ];

    internal static string[]? Arguments(string command) => command switch
    {
        "/mcp" => ["mcp", "list"],
        "/status" => ["auth", "status", "--json"],
        "/plugins" => ["plugin", "list"],
        "/version" => ["--version"],
        "/cli-help" => ["--help"],
        _ => null
    };

    internal static void AddTurnOptions(ICollection<string> args, string? effort, string? model)
    {
        if (effort is not null && effort != "auto" && Efforts.Contains(effort))
        {
            args.Add("--effort");
            args.Add(effort);
        }
        if (model is not null && model != "auto" && Models.Contains(model))
        {
            args.Add("--model");
            args.Add(model);
        }
    }

    internal static string Output(string command, int code, string output)
    {
        if (command == "/status")
            return AccountProviders.ParseClaudeStatus(code, output).Summary;
        if (command == "/mcp")
        {
            // The CLI also prints each server's executable, arguments or URL. Store only its name and health.
            var servers = new List<string>();
            foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
            {
                var name = line.IndexOf(": ", StringComparison.Ordinal);
                var status = line.LastIndexOf(" - ", StringComparison.Ordinal);
                if (name > 0 && status > name)
                    servers.Add(line[..name] + " · " + line[(status + 3)..]);
            }
            if (servers.Count > 0) return string.Join("\n", servers);
            if (output.Contains("No MCP servers", StringComparison.OrdinalIgnoreCase)) return "No MCP servers configured.";
            return $"Claude CLI returned no server statuses (exit {code}).";
        }
        return string.IsNullOrWhiteSpace(output) ? $"Claude CLI exited with code {code}." : output;
    }
}
