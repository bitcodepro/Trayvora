using System.Text.Json;

namespace VoiceAgent.Desktop;

internal static class CodexCommands
{
    internal const string RefreshMcp = "This MCP result belongs to a different or unknown Codex profile. Run /mcp again to manage its servers.";

    internal static readonly string[] LoginFileOptions = ["-c", "cli_auth_credentials_store=\"file\"", "-c", "forced_login_method=\"chatgpt\""];

    internal static readonly CommandChoice[] Menu =
    [
        new("/mcp", "MCP configuration for the selected Codex route"),
        new("/status", "Codex account status"),
        new("/version", "Installed Codex CLI version"),
        new("/help", "Commands available in Trayvora"),
        new("/cli-help", "Full help from the installed CLI")
    ];

    internal static string Profile(bool desktop, string? desktopProfile)
    {
        var path = desktop ? desktopProfile : Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "codex-data");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("Choose an absolute Codex Desktop profile folder.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static IReadOnlyDictionary<string, string?> EnvironmentFor(string profile, bool desktop) =>
        new Dictionary<string, string?>
        {
            ["CODEX_HOME"] = profile,
            ["CODEX_SQLITE_HOME"] = desktop ? null : profile,
            ["OPENAI_API_KEY"] = null,
            ["CODEX_API_KEY"] = null,
            ["ACCESS_TOKEN"] = null,
            ["OPENAI_BASE_URL"] = null,
        };

    internal static string[] Arguments(string command, bool desktop) => command switch
    {
        "/mcp" => ["mcp", "list", "--json"],
        "/status" => desktop ? ["login", "status"] : [.. LoginFileOptions, "login", "status"],
        "/version" => ["--version"],
        "/cli-help" => ["--help"],
        _ => throw new InvalidOperationException("Unknown command.")
    };

    internal static string Output(string command, int code, string output)
    {
        if (command == "/status")
            return code == 0 ? "Signed in to Codex."
                : output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)
                    ? "Not signed in to Codex." : $"Codex could not check sign-in (exit {code}).";

        if (command != "/mcp")
            return string.IsNullOrWhiteSpace(output) ? $"Codex CLI exited with code {code}." : output;

        if (code == 0)
        {
            try
            {
                using var json = JsonDocument.Parse(output);
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                {
                    if (json.RootElement.GetArrayLength() == 0) return "No MCP servers configured.";
                    var statuses = new List<string>();
                    foreach (var server in json.RootElement.EnumerateArray())
                    {
                        if (server.ValueKind != JsonValueKind.Object
                            || !server.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                            || !server.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            continue;
                        statuses.Add($"{name.GetString()} · {(enabled.GetBoolean() ? "Enabled" : "Disabled")}");
                    }
                    if (statuses.Count > 0) return string.Join("\n", statuses);
                }
            }
            catch (JsonException) { }
        }

        return $"Codex CLI returned no MCP server statuses (exit {code}).";
    }
}
