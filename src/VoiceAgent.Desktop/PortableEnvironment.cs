using Microsoft.Win32;

namespace VoiceAgent.Desktop;

/// <summary>
/// Prepares the portable folder layout that the old PowerShell launcher used to set up, so Trayvora.exe can be
/// started directly: data folders beside the executable, a private Codex profile, codex.exe discovery and the
/// Codex Desktop profile. Variables already set (for example by a development shell) are left unchanged.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class PortableEnvironment
{
    public static void Apply(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // A development shell sets these explicitly; keep its choices.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VOICEAGENT_SETTINGS_DIR"))) return;

        var settings = Path.Combine(root, "settings");
        var userCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        var codex = FindCodex(Path.Combine(settings, "codex-exe.txt"));
        if (codex is not null) Environment.SetEnvironmentVariable("VOICEAGENT_CODEX_EXE", codex);

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VOICEAGENT_DESKTOP_CODEX_HOME"))
            && DesktopCodexHome(userCodexHome) is { } desktopHome)
            Environment.SetEnvironmentVariable("VOICEAGENT_DESKTOP_CODEX_HOME", desktopHome);

        var codexData = Path.Combine(root, "codex-data");
        Set("VOICEAGENT_MODEL_DIR", Path.Combine(root, "models"));
        Set("VOICEAGENT_SETTINGS_DIR", settings);
        Set("VOICEAGENT_HISTORY_DIR", Path.Combine(root, "history"));
        Set("VOICEAGENT_CODEX_DIR", codexData);
        Set("CODEX_HOME", codexData);
        Set("CODEX_SQLITE_HOME", codexData);
        Set("TEMP", Path.Combine(root, "tmp"));
        Set("TMP", Path.Combine(root, "tmp"));
        Set("APPDATA", Path.Combine(root, "appdata"));
        Set("LOCALAPPDATA", Path.Combine(root, "localappdata"));
        foreach (var folder in new[] { "tmp", "codex-data", "appdata", "localappdata" })
        {
            try { Directory.CreateDirectory(Path.Combine(root, folder)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VOICEAGENT_REVQORA_EXE"))
            && Directory.GetParent(root)?.Parent is { } workspace)
        {
            var revqora = Path.Combine(workspace.FullName, "ChessDatabase", "Revqora-0.1.25-win-x64", "revqora.exe");
            if (File.Exists(revqora)) Environment.SetEnvironmentVariable("VOICEAGENT_REVQORA_EXE", revqora);
        }
    }

    private static void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value);

    /// <summary>Finds codex.exe in the launcher's order: override, PATH, remembered path, Codex app, VS Code extension.</summary>
    internal static string? FindCodex(string cache)
    {
        static bool Usable(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);

        IEnumerable<Func<IEnumerable<string?>>> sources =
        [
            () => [Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE")],
            () => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(folder => folder.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
                .Select(folder => Path.Combine(folder, "codex.exe")),
            () => [ReadCache(cache)],
            CodexAppCandidates,
            VsCodeCandidates,
        ];
        foreach (var source in sources)
        {
            string? found = null;
            try { found = source().FirstOrDefault(Usable); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            if (found is null) continue;
            try
            {
                if (ReadCache(cache) != found)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                    File.WriteAllText(cache, found);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return found;
        }
        return null;
    }

    private static string? ReadCache(string cache)
    {
        try { return File.Exists(cache) ? File.ReadLines(cache).FirstOrDefault()?.Trim() : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Installed Codex app packages, newest first, read from the per-user package repository.</summary>
    private static IEnumerable<string?> CodexAppCandidates()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (packages is null) return [];
        return packages.GetSubKeyNames()
            .Where(name => name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
            .Select(name => (Name: name, Version: Version.TryParse(name.Split('_').ElementAtOrDefault(1), out var version) ? version : new Version()))
            .OrderByDescending(item => item.Version)
            .Select(item =>
            {
                using var package = packages.OpenSubKey(item.Name);
                return package?.GetValue("PackageRootFolder") is string folder ? Path.Combine(folder, "app", "resources", "codex.exe") : null;
            })
            .ToArray();
    }

    private static IEnumerable<string?> VsCodeCandidates()
    {
        var extensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        if (!Directory.Exists(extensions)) return [];
        return Directory.EnumerateDirectories(extensions, "openai.chatgpt-*")
            .Select(folder => (Folder: folder, Version: Version.TryParse(Path.GetFileName(folder).Split('-').ElementAtOrDefault(1), out var version) ? version : new Version()))
            .OrderByDescending(item => item.Version)
            .Select(item => Path.Combine(item.Folder, "bin", "windows-x86_64", "codex.exe"))
            .ToArray();
    }

    /// <summary>The user's own Codex Desktop profile, when it has saved sessions.</summary>
    internal static string? DesktopCodexHome(string? userCodexHome)
    {
        var home = string.IsNullOrWhiteSpace(userCodexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : userCodexHome;
        try
        {
            var info = new DirectoryInfo(home);
            if (!info.Exists) return null;
            var target = info.LinkTarget is null ? info : info.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo ?? info;
            var path = target.FullName;
            return Directory.Exists(Path.Combine(path, "sessions")) ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
