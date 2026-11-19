using DiscussionTarget = VoiceAgent.Desktop.DiscussionWindow.DiscussionTarget;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace VoiceAgent.Desktop;

internal sealed class FilePreview : Border
{
    private static readonly Regex Location = new(@"^(?<path>.+?)(?:(?::|#L)(?<line>\d+)(?:-L?(?<end>\d+))?)?$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Extensions = new(
        ".cs .csproj .sln .slnx .fs .vb .cpp .cc .h .hpp .c .go .rs .py .js .mjs .tsx .jsx .ts .css .html .xml .xaml .axaml .md .txt .json .jsonl .yaml .yml .toml .ini .cfg .log .ps1 .sh .sql .java .kt .swift .rb .php .diff .patch .csv .props .targets".Split(' '),
        StringComparer.OrdinalIgnoreCase);
    public event Action? CloseRequested;
    internal static bool IsReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)) return false;
        var path = Parse(value).Path;
        return !path.Contains("://") && !path.StartsWith(@"\\") && !path.StartsWith("//") && Extensions.Contains(Path.GetExtension(path));
    }

    internal static (string Path, int Line, int End) Parse(string value)
    {
        var match = Location.Match(Uri.UnescapeDataString(value.Trim()));
        var path = match.Groups["path"].Value;
        if (path.Length > 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        var line = int.TryParse(match.Groups["line"].Value, out var l) ? Math.Max(1, l) : 1;
        return (path, line, int.TryParse(match.Groups["end"].Value, out var e) ? Math.Max(line, e) : line);
    }

    internal static string Resolve(string value, params string?[] roots)
    {
        if (!IsReference(value)) throw new IOException("Unsupported file reference.");
        var (raw, _, _) = Parse(value);
        foreach (var root in roots.OfType<string>().Where(Path.IsPathFullyQualified))
        {
            var full = Path.GetFullPath(raw, root);
            var relative = Path.GetRelativePath(root, full);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative) || relative.Contains(':')) continue;
            if (!File.Exists(full)) continue;
            for (FileSystemInfo? item = new FileInfo(full); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Choose the file manually to open a linked path.");
            return full;
        }
        throw new IOException("The linked file is missing or outside this chat’s workspace. Choose a local file to view.");
    }

    public FilePreview(string reference, string? workspace, string? attachments, Action<DiscussionTarget, string> reply)
    {
        Focusable = true;
        Padding = new Thickness(16); Background = TrayvoraTheme.Surface;
        var location = Parse(reference);
        var status = TrayvoraTheme.Hint(reference);
        var code = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        var close = new Button { Content = "Close" };
        var choose = new Button { Content = "Choose file…", IsVisible = false };
        var toCodex = new Button { Content = "Ask Codex", IsEnabled = false };
        var toClaude = new Button { Content = "Ask Claude", IsEnabled = false };
        var header = new StackPanel { Spacing = 6, Children = { TrayvoraTheme.Heading("File preview"), status, choose } };
        var diff = new Button { Content = "Changes", IsEnabled = false };
        var showingChanges = false;
        var actions = new WrapPanel { ItemSpacing = 6, Children = { diff, toCodex, toClaude, close } };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10 };
        Grid.SetRow(code, 1); Grid.SetRow(actions, 2);
        grid.Children.AddRange([header, code, actions]); Child = grid;
        TrayvoraTheme.StyleControls(diff, toCodex, toClaude, close, choose);
        string? file = null;
        string excerpt = "";
        void Quote(DiscussionTarget to)
        {
            if (file is null) return;
            var selected = string.IsNullOrEmpty(code.SelectedText) ? excerpt : code.SelectedText;
            reply(to, $"Review {file} ({(showingChanges ? "working tree vs HEAD" : "current file on disk")}):\n\n{selected}\n\n");
            CloseRequested?.Invoke();
        }
        toCodex.Click += (_, _) => Quote(DiscussionTarget.Codex);
        toClaude.Click += (_, _) => Quote(DiscussionTarget.Claude);
        close.Click += (_, _) => CloseRequested?.Invoke();
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; CloseRequested?.Invoke(); } };
        async Task Load(string? picked)
        {
            try
            {
                var path = picked ?? Resolve(reference, workspace, attachments);
                if (!Extensions.Contains(Path.GetExtension(path))) throw new IOException("Choose a text or source file.");
                var page = await Task.Run(() => ReadPage(path, location.Line, location.End));
                diff.IsEnabled = true;
                showingChanges = false; diff.Content = "Changes";
                file = path; code.Text = page.Text; excerpt = page.Text[page.Start..page.End];
                code.CaretIndex = page.End; code.SelectionStart = page.Start; code.SelectionEnd = page.End;
                status.Text = $"{path}  ·  line {location.Line}  ·  current file on disk";
                toCodex.IsEnabled = toClaude.IsEnabled = true;
                choose.IsVisible = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { status.Text = error.Message; choose.IsVisible = true; }
        }
        diff.Click += async (_, _) =>
        {
            if (file is null) return;
            if (showingChanges) { await Load(file); return; }
            diff.IsEnabled = false;
            try
            {
                excerpt = await LocalActions.FileChangesAsync(file, CancellationToken.None);
                code.Text = excerpt;
                code.SelectionStart = code.SelectionEnd = 0;
                status.Text = $"{file} · working tree vs HEAD";
                showingChanges = true;
                diff.Content = "File";
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { diff.IsEnabled = true; }
        };
        choose.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } picker) return;
            var result = await picker.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a source file" });
            if (result.FirstOrDefault()?.TryGetLocalPath() is { } path) await Load(path);
        };
        _ = Load(null);
    }

    internal static (string Text, int Start, int End) ReadPage(string path, int line, int endLine)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 32L * 1024 * 1024) throw new IOException("Preview supports text files up to 32 MiB.");
        using var reader = new StreamReader(file);
        var text = new StringBuilder();
        int start = 0, end = 0;
        var last = Math.Min((long)endLine, (long)line + 120) + 60;
        for (long n = 1; n <= last; n++)
        {
            if (file.Position > 32L * 1024 * 1024) throw new IOException("The requested line is beyond the preview limit.");
            var row = reader.ReadLine();
            if (row is null) { if (n <= line) throw new IOException("This line no longer exists in the file."); break; }
            if (n < line - 35) continue;
            if (row.Contains('\0')) throw new IOException("This is a binary file.");
            if (n == line) start = text.Length;
            text.Append(n.ToString().PadLeft(5)).Append("  ").Append(row.AsSpan(0, Math.Min(row.Length, 1600))).AppendLine();
            if (n >= line && n <= endLine) end = text.Length;
        }
        return (text.ToString(), start, end);
    }
}
