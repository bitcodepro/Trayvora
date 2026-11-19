using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace VoiceAgent.Desktop;

/// <summary>
/// Files attached to the next Sessions message: dropped on the window, pasted (images and files from the
/// clipboard) or chosen with the paperclip. Each file is copied into the session's attachments folder, which both
/// agents can read, and the message lists the copies' paths.
/// </summary>
internal sealed class ComposerAttachments
{
    public const long MaxFileBytes = 200L * 1024 * 1024;
    private readonly List<string> _files = [];
    private readonly Func<string?> _folder;

    public ComposerAttachments(Func<string?> folder)
    {
        _folder = folder;
        Chips = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, IsVisible = false };
    }

    /// <summary>The attached files, shown above the text box with a × to remove each.</summary>
    public WrapPanel Chips { get; }

    public IReadOnlyList<string> Files => _files;

    public event Action<string>? Problem;
    public event Action? Changed;

    /// <summary>Copies files into the session folder and adds them; returns how many were added.</summary>
    public int AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path)) { Problem?.Invoke($"Folders cannot be attached: {Path.GetFileName(path)}."); continue; }
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                if (info.Length > MaxFileBytes) { Problem?.Invoke($"{info.Name} is larger than 200 MB."); continue; }
                var target = Target(info.Name);
                File.Copy(path, target);
                Add(target);
                added++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Problem?.Invoke($"Could not attach {Path.GetFileName(path)}: {error.Message}");
            }
        }
        return added;
    }

    private CancellationTokenSource? _copy;
    public bool Copying => _copy is not null;

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (_copy is not null) return;
        using var stop = new CancellationTokenSource();
        _copy = stop;
        var folder = _folder();
        Changed?.Invoke();
        try
        {
            foreach (var path in paths.ToArray())
            {
                stop.Token.ThrowIfCancellationRequested();
                if (folder is null || folder != _folder()) return;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxFileBytes) { Problem?.Invoke("Choose a file of 200 MiB or less."); continue; }
                var target = Target(info.Name);
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                var dest = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                try
                {
                    using (dest) await source.CopyToAsync(dest, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    Add(target);
                }
                catch { File.Delete(target); throw; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { Problem?.Invoke(error.Message); }
        finally { _copy = null; Changed?.Invoke(); }
    }

    public void CancelCopy() => _copy?.Cancel();

    /// <summary>Saves a pasted or dropped picture as PNG and attaches it.</summary>
    public bool AddImage(Bitmap image)
    {
        try
        {
            var target = Target($"image-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png");
#pragma warning disable CS0618 // PNG by default; the options overload adds nothing here
            image.Save(target);
#pragma warning restore CS0618
            Add(target);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Problem?.Invoke("Could not save the pasted image: " + error.Message);
            return false;
        }
    }

    /// <summary>The message text with the list of attached files the agents should open.</summary>
    private const string ListHeader = "[Attached files — open them before answering:]";

    public static string WithFiles(string text, IReadOnlyList<string> files) =>
        files.Count == 0 ? text
            : text + "\n\n" + ListHeader + "\n" + string.Join("\n", files.Select(file => "- " + file));

    /// <summary>
    /// Moves attachments that were copied into another session's folder (the user switched sessions after attaching)
    /// into <paramref name="folder"/>, so the agent of this session can read them; returns the files to send.
    /// </summary>
    public IReadOnlyList<string> MoveInto(string folder)
    {
        var root = Root(folder);
        for (var i = 0; i < _files.Count; i++)
        {
            if (Inside(_files[i], root)) continue;
            Directory.CreateDirectory(folder);
            var target = Free(folder, Path.GetFileName(_files[i]));
            File.Move(_files[i], target);
            _files[i] = target;
        }
        Redraw();
        return [.. _files];
    }

    /// <summary>
    /// Attached files named in a saved message (used when it is forwarded): only existing files whose full path lies
    /// inside <paramref name="folder"/>; "..", other drives and links that lead elsewhere are refused.
    /// </summary>
    public static IReadOnlyList<string> FilesIn(string text, string folder)
    {
        var root = Root(folder);
        var marker = text.LastIndexOf(ListHeader, StringComparison.Ordinal);
        if (marker < 0) return [];
        var found = new List<string>();
        foreach (var line in text[(marker + ListHeader.Length)..].Split('\n'))
        {
            if (!line.StartsWith("- ", StringComparison.Ordinal)) continue;
            string path;
            try { path = Path.GetFullPath(line[2..].Trim()); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (Inside(path, root) && File.Exists(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase)) found.Add(path);
        }
        return found;
    }

    private static string Root(string folder) => Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>True when <paramref name="file"/> resolves to a plain file directly inside the folder <paramref name="root"/>.</summary>
    internal static bool Inside(string file, string root)
    {
        try
        {
            var full = Path.GetFullPath(file);
            if (!string.Equals(Path.GetDirectoryName(full) + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(full);
            return info.Exists && info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Restore(IEnumerable<string> files)
    {
        _files.Clear();
        CancelCopy();
        _files.AddRange(files);
        Redraw();
    }

    public void Clear()
    {
        _files.Clear();
        Redraw();
    }

    /// <summary>A free file name in the session's attachments folder.</summary>
    private string Target(string name)
    {
        var folder = _folder() ?? throw new IOException("Open or start a session first.");
        Directory.CreateDirectory(folder);
        var safe = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safe.Length == 0) safe = "file";
        return Free(folder, safe);
    }

    private static string Free(string folder, string safe)
    {
        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);
        var target = Path.Combine(folder, safe);
        for (var i = 2; File.Exists(target); i++) target = Path.Combine(folder, $"{stem} ({i}){extension}");
        return target;
    }

    private void Add(string file)
    {
        _files.Add(file);
        Redraw();
    }

    private void Redraw()
    {
        Chips.Children.Clear();
        foreach (var file in _files)
        {
            var remove = new Button { Content = "×", Padding = new Thickness(6, 0), MinHeight = 0, FontSize = 13 };
            TrayvoraTheme.Ghost(remove);
            ToolTip.SetTip(remove, "Remove this attachment");
            Avalonia.Automation.AutomationProperties.SetName(remove, "Remove " + Path.GetFileName(file));
            var path = file;
            remove.Click += (_, _) =>
            {
                _files.Remove(path);
                try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                Redraw();
            };
            var icon = IsImage(file) ? "🖼" : "📄";
            Chips.Children.Add(new Border
            {
                Background = TrayvoraTheme.Elevated, BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 2, 2),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = $"{icon} {Path.GetFileName(file)}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis },
                        remove,
                    },
                },
            });
        }
        Chips.IsVisible = _files.Count > 0;
        Changed?.Invoke();
    }

    internal static bool IsImage(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    /// <summary>Pastes <paramref name="text"/> over the selection or at the cursor, as a normal paste would.</summary>
    internal static void InsertAtCaret(TextBox box, string text)
    {
        var current = box.Text ?? "";
        var start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        if (start == end) start = end = Math.Clamp(box.CaretIndex, 0, current.Length);
        box.Text = current[..start] + text + current[end..];
        box.SelectionStart = box.SelectionEnd = box.CaretIndex = start + text.Length;
    }

    /// <summary>Drop handling for a control: files and pictures dragged onto it are attached.</summary>
    public void AcceptDrops(Control target, Action<bool> highlight)
    {
        DragDrop.SetAllowDrop(target, true);
        void Over(object? sender, DragEventArgs args)
        {
            var any = args.DataTransfer.Contains(DataFormat.File) || args.DataTransfer.Contains(DataFormat.Bitmap);
            args.DragEffects = any ? DragDropEffects.Copy : DragDropEffects.None;
            highlight(any);
            args.Handled = true;
        }
        target.AddHandler(DragDrop.DragEnterEvent, Over);
        target.AddHandler(DragDrop.DragOverEvent, Over);
        target.AddHandler(DragDrop.DragLeaveEvent, (_, _) => highlight(false));
        target.AddHandler(DragDrop.DropEvent, async (_, args) =>
        {
            highlight(false);
            args.Handled = true;
            if (args.DataTransfer.TryGetFiles() is { } files)
                await AddFilesAsync(files.Select(file => file.TryGetLocalPath()).OfType<string>());
            else if (args.DataTransfer.TryGetBitmap() is { } image)
                AddImage(image);
        });
    }

    /// <summary>Ctrl+V in <paramref name="box"/>: a picture or files on the clipboard are attached, text is pasted as usual.</summary>
    public void HandlePaste(TextBox box)
    {
        box.AddHandler(InputElement.KeyDownEvent, async (_, args) =>
        {
            var paste = (args.Key == Key.V && args.KeyModifiers.HasFlag(KeyModifiers.Control)) ||
                        (args.Key == Key.Insert && args.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (!paste || TopLevel.GetTopLevel(box)?.Clipboard is not { } clipboard) return;
            args.Handled = true;
            try
            {
                if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
                {
                    await AddFilesAsync(files.Select(file => file.TryGetLocalPath()).OfType<string>());
                    return;
                }
                if (await clipboard.TryGetTextAsync() is { Length: > 0 } text)
                {
                    InsertAtCaret(box, text);
                    return;
                }
                if (await clipboard.TryGetBitmapAsync() is { } image) AddImage(image);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                Problem?.Invoke("Could not read the clipboard.");
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}
