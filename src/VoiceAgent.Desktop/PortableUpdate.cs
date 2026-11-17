using System.Diagnostics;
using System.IO.Compression;

namespace VoiceAgent.Desktop;

/// <summary>
/// Updates a portable Trayvora folder from a newer Trayvora zip without scripts:
/// the zip is unpacked into settings\update-staging, the staged Trayvora.exe waits for this process to exit,
/// copies the program files over the application folder (data folders are never touched) and starts it again.
/// </summary>
internal static class PortableUpdate
{
    public const string ApplyArgument = "--apply-update";
    private const string ExeName = "Trayvora.exe";

    /// <summary>Folders with the user's data, kept as they are.</summary>
    internal static readonly string[] DataFolders = ["settings", "history", "codex-data", "Memory", "temp", "appdata", "localappdata", "logs"];

    public static string StagingRoot(string appDirectory) => Path.Combine(appDirectory, "settings", "update-staging");

    /// <summary>Unpacks <paramref name="zip"/> and returns the folder that holds the new Trayvora.exe.</summary>
    public static string Stage(string zip, string appDirectory)
    {
        var root = StagingRoot(appDirectory);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var target = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var full = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!path.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The zip contains an unsafe path.");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
            }
        }
        var program = ProgramFolder(target) ?? throw new InvalidDataException("This zip does not contain Trayvora.exe.");
        return program;
    }

    /// <summary>The folder with Trayvora.exe: the zip root or its only top folder.</summary>
    internal static string? ProgramFolder(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, ExeName))) return extracted;
        var folders = Directory.GetDirectories(extracted);
        return folders.Length == 1 && File.Exists(Path.Combine(folders[0], ExeName)) ? folders[0] : null;
    }

    /// <summary>Starts the staged Trayvora.exe, which applies the update after this process exits.</summary>
    public static void Launch(string staged, string appDirectory)
    {
        var info = new ProcessStartInfo(Path.Combine(staged, ExeName)) { UseShellExecute = false, WorkingDirectory = staged };
        info.ArgumentList.Add(ApplyArgument);
        info.ArgumentList.Add(Path.TrimEndingDirectorySeparator(appDirectory));
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process.Start(info)?.Dispose();
    }

    /// <summary>Runs in the staged copy: waits for the old process, copies program files, starts the updated app.</summary>
    public static void Apply(string appDirectory, int oldProcess)
    {
        try
        {
            using var old = Process.GetProcessById(oldProcess);
            old.WaitForExit(60_000);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                CopyProgram(source, appDirectory);
                last = null;
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                last = error;
                Thread.Sleep(1000);
            }
        }
        if (last is not null)
        {
            try { File.WriteAllText(Path.Combine(appDirectory, "settings", "update-failed.txt"), last.ToString()); } catch (IOException) { }
        }
        Process.Start(new ProcessStartInfo(Path.Combine(appDirectory, ExeName)) { UseShellExecute = false, WorkingDirectory = appDirectory })?.Dispose();
    }

    /// <summary>Copies every program file from <paramref name="source"/>; data folders and an existing model are kept.</summary>
    internal static void CopyProgram(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var top = relative.Split(Path.DirectorySeparatorChar, 2)[0];
            if (relative.Contains(Path.DirectorySeparatorChar) && DataFolders.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(target, relative);
            if (top.Equals("models", StringComparison.OrdinalIgnoreCase) && File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>Removes a finished staging folder (called by the updated app).</summary>
    public static void CleanUp(string appDirectory)
    {
        var root = StagingRoot(appDirectory);
        if (!Directory.Exists(root) || AppContext.BaseDirectory.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return;
        try { Directory.Delete(root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
