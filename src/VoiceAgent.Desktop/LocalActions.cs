using System.Diagnostics;

namespace VoiceAgent.Desktop;

internal static class LocalActions
{
    public static string Workspace(string directory)
    {
        // Local folders on any drive; network shares and device paths are not allowed.
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new IOException("Choose a local project directory.");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        for (var entry = new DirectoryInfo(full); entry is not null; entry = entry.Parent)
        {
            if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Choose an existing folder without symbolic links or junctions.");
            }
        }

        return full;
    }

    public static void OpenWorkspace(string directory)
    {
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Workspace(directory));
        using var process = Process.Start(start) ?? throw new IOException("Could not open the project in File Explorer.");
    }

    public static async Task<string> ReadGitHeadAsync(string directory, CancellationToken cancellationToken = default)
    {
        directory = Workspace(directory);
        var executable = GitExecutable();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(8));
        var root = await RunGitAsync(executable, directory, limit.Token, "rev-parse", "--show-toplevel");
        if (root is null || !string.Equals(Workspace(root), directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Select the repository root.");
        }

        var branch = await RunGitAsync(executable, directory, limit.Token, "symbolic-ref", "--quiet", "--short", "HEAD");
        var head = await RunGitAsync(executable, directory, limit.Token, "rev-parse", "--verify", "HEAD");
        return $"Branch: {branch ?? "(detached HEAD)"}{Environment.NewLine}Commit: {head ?? "(no commit yet)"}";
    }

    internal static async Task<string> FileChangesAsync(string path, CancellationToken ct)
    {
        var directory = Workspace(Path.GetDirectoryName(path)!);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(8));
        var result = await RunGitWithLimitAsync(GitExecutable(), directory, limit.Token, 256_000, "--literal-pathspecs",
            "diff", "--no-ext-diff", "--no-textconv", "--no-color", "HEAD", "--", Path.GetFileName(path));
        if (result is null) throw new IOException("No Git HEAD is available for this file.");
        if (result.Length > 0) return result;
        var tracked = await RunGitAsync(GitExecutable(), directory, limit.Token, "--literal-pathspecs", "ls-files", "--error-unmatch", "--", Path.GetFileName(path));
        return tracked is null ? "This file is not tracked in HEAD." : "No changes compared with HEAD.";
    }

    public static async Task InitializeEmptyRepositoryAsync(string directory)
    {
        directory = Workspace(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException("Choose an empty directory for the new repository.");
        }

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        if (await RunGitAsync(GitExecutable(), directory, limit.Token, "init", "--template=", "--") is null)
        {
            throw new IOException("Git could not initialize the empty directory.");
        }
    }

    private static string GitExecutable()
    {
        var executable = Environment.GetEnvironmentVariable("VOICEAGENT_GIT_EXE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe");
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new IOException("Set VOICEAGENT_GIT_EXE to the full path of your trusted Git executable.");
        }
        return executable;
    }

    private static Task<string?> RunGitAsync(string executable, string directory, CancellationToken ct, params string[] args) =>
        RunGitWithLimitAsync(executable, directory, ct, 2048, args);

    private static async Task<string?> RunGitWithLimitAsync(string executable, string directory, CancellationToken ct, int capacity, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        foreach (var argument in new[] { "--no-pager", "-c", "core.fsmonitor=false" }.Concat(args))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        try
        {
            var output = new char[capacity];
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            var length = await process.StandardOutput.ReadBlockAsync(output.AsMemory(), ct);
            if (length == output.Length) throw new IOException("The Git result exceeds the preview limit.");
            await process.WaitForExitAsync(ct);
            await stderr;
            return process.ExitCode == 0 ? new string(output, 0, length).Trim() : null;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
    }
}
