using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VoiceAgent.Application;
using VoiceAgent.Speech;

namespace VoiceAgent.Desktop;

/// <summary>A full-window page with a Back button, used by the audio tools.</summary>
internal abstract class ToolPage : UserControl
{
    public event Action? BackRequested;

    protected Control Frame(string title, string subtitle, Control body)
    {
        var back = new Button { Content = "←  Back" };
        TrayvoraTheme.StyleControls(back);
        TrayvoraTheme.Ghost(back);
        back.Click += (_, _) => BackRequested?.Invoke();
        Avalonia.Automation.AutomationProperties.SetName(back, "Back to Trayvora");
        KeyDown += (_, args) =>
        {
            if (args.Key != Avalonia.Input.Key.Escape || args.Handled) return;
            if (OnEscape()) { args.Handled = true; return; }
            BackRequested?.Invoke();
            args.Handled = true;
        };
        var header = new Border
        {
            Background = TrayvoraTheme.Sidebar, BorderBrush = TrayvoraTheme.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12, 24, 14),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    back,
                    new StackPanel
                    {
                        Margin = new Thickness(8, 0, 0, 0), Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = subtitle, FontSize = 12, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                },
            },
        };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(26, 18, 26, 22), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Stretch, Child = body },
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(scroll, 1);
        grid.Children.AddRange([header, scroll]);
        return grid;
    }

    /// <summary>Handles Escape for a running operation; return true when handled.</summary>
    protected virtual bool OnEscape() => false;

    protected static string Clock(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    protected IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;
}

/// <summary>The local Whisper models the page can use and download.</summary>
internal sealed record WhisperChoices(
    IReadOnlyList<string> Names,
    Func<int> DefaultIndex,
    Func<int, bool> Exists,
    Func<int, IProgress<double>, CancellationToken, Task> Download);

/// <summary>Turns audio files into text, one after another, with local Whisper (any downloaded model) or OpenAI.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AudioToTextView : ToolPage
{
    private static readonly string[] LanguageCodes = ["auto", "ru", "uk", "en"];
    private readonly Func<bool, int, ISpeechTranscriber> _transcriber;
    private readonly Func<bool, string> _engine;
    private readonly Func<bool, bool> _ready;
    private readonly WhisperChoices _models;
    private readonly ComboBox _model = new() { MinWidth = 300 };
    private readonly Button _download = new() { Content = "Download model" };
    private readonly ProgressBar _downloadProgress = new() { Minimum = 0, Maximum = 1, IsVisible = false, MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _modelRow = new() { Spacing = 6 };
    private CancellationTokenSource? _downloading;
    private bool _fillingModels;
    private readonly Func<bool> _defaultCloud;
    private readonly Button _useLocal = TrayvoraTheme.Chip("Local Whisper");
    private readonly Button _useCloud = TrayvoraTheme.Chip("OpenAI");
    private bool? _cloud;
    private readonly Button _choose = new() { Content = "Choose audio files…" };
    private readonly Border _dropZone = new();
    private readonly TextBlock _dropHint = new() { Text = "Drop audio files here", FontWeight = FontWeight.SemiBold };
    private readonly StackPanel _queueList = new() { Spacing = 6 };
    private readonly ComboBox _language = new() { ItemsSource = new[] { "Auto", "Русский", "Українська", "English" }, SelectedIndex = 0, MinWidth = 160 };
    private readonly CheckBox _times = new() { Content = "Add time marks between parts" };
    private readonly TextBlock _engineText = new() { FontSize = 12, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly Button _start = new() { Content = "Transcribe" };
    private readonly Button _cancel = new() { Content = "Cancel", IsVisible = false };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _resultName = new() { FontSize = 12, Foreground = TrayvoraTheme.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _result = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 260, PlaceholderText = "The transcript appears here, part by part." };
    private readonly Button _copy = new() { Content = "Copy" };
    private readonly Button _save = new() { Content = "Save as .txt…" };
    private readonly Button _saveSrt = new() { Content = "Save as .srt…" };
    private readonly Button _saveAll = new() { Content = "Save all…" };
    private readonly List<QueueItem> _queue = [];
    private QueueItem? _selected;
    private bool _showing;
    private CancellationTokenSource? _run;

    internal enum ItemState { Reading, Waiting, Transcribing, Done, Failed, Stopped }

    /// <summary>One file in the queue and what was recognized from it.</summary>
    internal sealed class QueueItem(string path)
    {
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
        public TimeSpan Duration { get; set; }
        public ItemState State { get; set; } = ItemState.Reading;
        public double Progress { get; set; }
        public string? Error { get; set; }
        public string Text { get; set; } = "";
        public List<SubtitleCue> Cues { get; } = [];
    }

    /// <param name="transcriber">Creates a transcriber for OpenAI (true) or local Whisper (false).</param>
    /// <param name="engine">Model name for OpenAI (true) or local Whisper (false).</param>
    /// <param name="ready">Whether that engine is set up.</param>
    /// <param name="defaultCloud">The engine chosen in Settings, used until the user switches here.</param>
    public AudioToTextView(Func<bool, int, ISpeechTranscriber> transcriber, Func<bool, string> engine, Func<bool, bool> ready, Func<bool> defaultCloud, WhisperChoices models)
    {
        _transcriber = transcriber;
        _engine = engine;
        _ready = ready;
        _models = models;
        TrayvoraTheme.StyleControls(_model, _download);
        _model.SelectionChanged += (_, _) => { if (!_fillingModels) Refresh(); };
        _download.Click += async (_, _) => await DownloadAsync();
        Avalonia.Automation.AutomationProperties.SetName(_model, "Whisper model");
        _modelRow.Children.AddRange([
            new Label { Content = "Whisper model", Target = _model },
            TrayvoraTheme.Row(_model, _download, _downloadProgress),
            TrayvoraTheme.Hint("Bigger models recognize speech much better but are slower on this computer. Large v3 Turbo gives the best accuracy; Small is a good balance."),
        ]);
        _defaultCloud = defaultCloud;
        _useLocal.Click += (_, _) => { _cloud = false; Refresh(); };
        _useCloud.Click += (_, _) => { _cloud = true; Refresh(); };
        Avalonia.Automation.AutomationProperties.SetName(_useLocal, "Use local Whisper");
        Avalonia.Automation.AutomationProperties.SetName(_useCloud, "Use OpenAI");
        TrayvoraTheme.Primary(_start);
        TrayvoraTheme.StyleControls(_choose, _language, _cancel, _result, _copy, _save, _saveSrt, _saveAll);
        TrayvoraTheme.Ghost(_cancel);
        _result.FontSize = 14;
        _choose.Click += async (_, _) => await ChooseAsync();
        _start.Click += async (_, _) => await TranscribeAsync();
        _cancel.Click += (_, _) => _run?.Cancel();
        _copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, _result.Text ?? "");
            _status.Text = "Transcript copied.";
        };
        _save.Click += async (_, _) => await SaveAsync();
        _saveSrt.Click += async (_, _) => await SaveSrtAsync();
        _saveAll.Click += async (_, _) => await SaveAllAsync();
        _result.TextChanged += (_, _) =>
        {
            // Edits to a finished transcript are kept for that file.
            if (!_showing && _selected is { State: ItemState.Done or ItemState.Stopped } item) item.Text = _result.Text ?? "";
            Refresh();
        };

        _dropZone.BorderBrush = TrayvoraTheme.Border;
        _dropZone.BorderThickness = new Thickness(1);
        _dropZone.CornerRadius = new CornerRadius(8);
        _dropZone.Padding = new Thickness(14, 12);
        _dropZone.Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12,
            Children =
            {
                new StackPanel
                {
                    Spacing = 2,
                    Children = { _dropHint, new TextBlock { Text = "or choose one or more files. They are transcribed one after another.", FontSize = 12, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap } },
                },
                Column(_choose, 1),
            },
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(false));
        AddHandler(DragDrop.DropEvent, OnDrop);

        var fileCard = TrayvoraTheme.Card(
            TrayvoraTheme.Caption("Audio files"),
            _dropZone,
            _queueList,
            TrayvoraTheme.Hint("MP3, WAV, M4A, AAC, WMA, FLAC, OGG, MP4 and other formats Windows can play. Long recordings are processed in parts of about two minutes."));
        var optionsCard = TrayvoraTheme.Card(
            TrayvoraTheme.Caption("Recognition"),
            TrayvoraTheme.Field("Engine", TrayvoraTheme.Row(_useLocal, _useCloud)),
            _modelRow,
            TrayvoraTheme.Wrap(TrayvoraTheme.Field("Language", _language), _times),
            _engineText,
            TrayvoraTheme.Row(_start, _cancel),
            _progress,
            _status);
        var resultCard = TrayvoraTheme.Card(
            new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel { Spacing = 2, Children = { TrayvoraTheme.Heading("Transcript"), _resultName } },
                    Column(TrayvoraTheme.Wrap(_copy, _save, _saveSrt, _saveAll), 1),
                },
            },
            _result);
        Content = Frame("Audio to text", "Choose or drop recordings and Trayvora turns them into editable text and subtitles.",
            new StackPanel { Spacing = 14, Children = { fileCard, optionsCard, resultCard } });
        AttachedToVisualTree += (_, _) => Refresh();
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    internal IReadOnlyList<QueueItem> Queue => _queue;

    private int? _modelChoice;

    /// <summary>The Whisper model used on this page: the user's choice here, otherwise the one from Settings.</summary>
    internal int SelectedModel => _model.SelectedIndex >= 0 ? _model.SelectedIndex : _modelChoice ?? Math.Clamp(_models.DefaultIndex(), 0, _models.Names.Count - 1);

    private static string ShortName(int index) => index switch { 0 => "Base", 1 => "Small", 2 => "Medium", _ => "Large v3 Turbo" } + index switch
    {
        0 => " (148 MB)", 1 => " (488 MB)", 2 => " (1.5 GB)", _ => " (1.6 GB)",
    };

    private void FillModels()
    {
        var selected = SelectedModel;
        _fillingModels = true;
        try
        {
            _model.ItemsSource = _models.Names.Select((name, index) => _models.Exists(index) ? $"{name}  ·  downloaded" : $"{name}  ·  not downloaded").ToArray();
            _model.SelectedIndex = selected;
        }
        finally { _fillingModels = false; }
        _modelChoice = selected;
    }

    private async Task DownloadAsync()
    {
        if (_downloading is not null) return;
        var index = SelectedModel;
        using var download = new CancellationTokenSource();
        _downloading = download;
        _downloadProgress.Value = 0;
        _status.Text = $"Downloading Whisper {_models.Names[index]}. The file is verified before use.";
        Refresh();
        try
        {
            await _models.Download(index, new Progress<double>(value => _downloadProgress.Value = value), download.Token);
            _status.Text = $"Whisper {_models.Names[index]} is ready.";
        }
        catch (OperationCanceledException) { _status.Text = "Download stopped."; }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            _status.Text = error is HttpRequestException ? "Download failed. Check your internet connection and try again." : error.Message;
        }
        finally
        {
            _downloading = null;
            Refresh();
        }
    }

    /// <summary>The engine used on this page: the user's choice here, otherwise the one from Settings.</summary>
    internal bool Cloud => _cloud ?? _defaultCloud();

    public void Refresh()
    {
        var idle = _run is null;
        var cloud = Cloud;
        FillModels();
        var model = SelectedModel;
        var modelReady = _models.Exists(model);
        var ready = cloud ? _ready(true) : modelReady;
        _modelRow.IsVisible = !cloud;
        var downloading = _downloading is not null;
        _download.IsVisible = !cloud && !modelReady;
        _download.IsEnabled = idle && !downloading;
        _download.Content = downloading ? "Downloading…" : $"Download {ShortName(model)}";
        _downloadProgress.IsVisible = downloading;
        _model.IsEnabled = idle && !downloading;
        TrayvoraTheme.SetSelected(_useLocal, !cloud);
        TrayvoraTheme.SetSelected(_useCloud, cloud);
        _useLocal.IsEnabled = _useCloud.IsEnabled = idle;
        _engineText.Text = (cloud, ready) switch
        {
            (false, true) => $"Runs on this computer with Whisper {_models.Names[model]}. Free; the audio stays here.",
            (true, true) => $"OpenAI {_engine(true)}. The audio is sent to OpenAI and billed separately.",
            (false, false) => downloading ? $"Downloading Whisper {_models.Names[model]}…" : $"Whisper {_models.Names[model]} is not downloaded yet. Download it here, choose another model, or switch to OpenAI.",
            (true, false) => "No OpenAI API key is saved. Add one in Settings → Speech recognition, or switch to Local Whisper.",
        };
        _engineText.Foreground = ready ? TrayvoraTheme.Muted : TrayvoraTheme.Warning;
        var waiting = _queue.Count(item => item.State == ItemState.Waiting);
        _start.IsEnabled = idle && ready && waiting > 0 && !downloading;
        _start.Content = waiting > 1 ? $"Transcribe {waiting} files" : "Transcribe";
        _language.IsEnabled = _times.IsEnabled = idle;
        _cancel.IsVisible = !idle;
        _progress.IsVisible = !idle;
        var hasText = !string.IsNullOrWhiteSpace(_result.Text);
        _copy.IsEnabled = _save.IsEnabled = hasText;
        _saveSrt.IsEnabled = idle && _selected is { Cues.Count: > 0 };
        _saveAll.IsEnabled = idle && _queue.Count(item => !string.IsNullOrWhiteSpace(item.Text)) > 1;
        _resultName.Text = _selected is { } shown ? shown.Name : "";
        RenderQueue();
    }

    private void RenderQueue()
    {
        var idle = _run is null;
        _queueList.Children.Clear();
        _queueList.IsVisible = _queue.Count > 0;
        foreach (var item in _queue)
        {
            var name = new Button { Content = item.Name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            TrayvoraTheme.StyleControls(name);
            TrayvoraTheme.Ghost(name);
            TrayvoraTheme.SetSelected(name, ReferenceEquals(item, _selected));
            name.IsEnabled = item.State is ItemState.Done or ItemState.Stopped or ItemState.Transcribing;
            Avalonia.Automation.AutomationProperties.SetName(name, $"Show transcript of {item.Name}");
            name.Click += (_, _) => Show(item);
            var state = new TextBlock
            {
                Text = Describe(item), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 360,
                Foreground = item.State switch { ItemState.Failed => TrayvoraTheme.Danger, ItemState.Done => TrayvoraTheme.Success, _ => TrayvoraTheme.Muted },
            };
            var remove = new Button { Content = "Remove", IsVisible = item.State == ItemState.Waiting || idle && item.State != ItemState.Reading };
            TrayvoraTheme.StyleControls(remove);
            TrayvoraTheme.Ghost(remove);
            Avalonia.Automation.AutomationProperties.SetName(remove, $"Remove {item.Name}");
            remove.Click += (_, _) => Remove(item);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10, Children = { name, Column(state, 1), Column(remove, 2) } };
            _queueList.Children.Add(row);
        }
    }

    internal static string Describe(QueueItem item) => item.State switch
    {
        ItemState.Reading => "Reading…",
        ItemState.Waiting => $"Waiting · {Clock(item.Duration)}",
        ItemState.Transcribing => $"Transcribing {item.Progress:P0}",
        ItemState.Done => string.IsNullOrWhiteSpace(item.Text) ? "Done · no speech found" : "Done",
        ItemState.Stopped => "Stopped",
        _ => $"Failed: {item.Error}",
    };

    private void Show(QueueItem item)
    {
        _selected = item;
        _showing = true;
        try { _result.Text = item.Text; }
        finally { _showing = false; }
        Refresh();
    }

    private void Remove(QueueItem item)
    {
        if (item.State == ItemState.Transcribing) return;
        _queue.Remove(item);
        if (ReferenceEquals(_selected, item))
        {
            _selected = null;
            if (_queue.LastOrDefault(other => other.State is ItemState.Done or ItemState.Stopped) is { } other) Show(other);
            else { _showing = true; _result.Clear(); _showing = false; }
        }
        Refresh();
    }

    protected override bool OnEscape()
    {
        if (_downloading is not null) { _downloading.Cancel(); return true; }
        if (_run is null) return false;
        _run.Cancel();
        return true;
    }

    private static bool IsAudio(string path) =>
        AudioFileChunks.Extensions.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DroppedPaths(DragEventArgs args) =>
        args.DataTransfer.TryGetFiles() is { } files
            ? files.Select(file => file.TryGetLocalPath()).OfType<string>().Where(IsAudio).ToArray()
            : [];

    private void OnDragOver(object? sender, DragEventArgs args)
    {
        var any = args.DataTransfer.Contains(DataFormat.File);
        args.DragEffects = any ? DragDropEffects.Copy : DragDropEffects.None;
        Highlight(any);
        args.Handled = true;
    }

    private void Highlight(bool on)
    {
        _dropZone.BorderBrush = on ? TrayvoraTheme.Accent : TrayvoraTheme.Border;
        _dropZone.Background = on ? TrayvoraTheme.AccentSoft : null;
    }

    private async void OnDrop(object? sender, DragEventArgs args)
    {
        Highlight(false);
        args.Handled = true;
        var paths = DroppedPaths(args);
        if (paths.Count == 0)
        {
            _status.Text = "None of the dropped files look like audio. Use MP3, WAV, M4A, OGG, FLAC, WMA, AAC or MP4.";
            return;
        }
        await AddAsync(paths);
    }

    private async Task ChooseAsync()
    {
        if (Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose audio files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio") { Patterns = AudioFileChunks.Extensions.Select(extension => "*." + extension).ToArray() },
                new FilePickerFileType("All files") { Patterns = ["*.*"] },
            ],
        });
        await AddAsync(files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray());
    }

    private async Task AddAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths) await LoadAsync(path);
    }

    /// <summary>Adds a file to the queue after checking that Windows can decode it.</summary>
    internal async Task LoadAsync(string path)
    {
        if (_queue.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase) && item.State is ItemState.Reading or ItemState.Waiting or ItemState.Transcribing)) return;
        var item = new QueueItem(path);
        _queue.Add(item);
        Refresh();
        try
        {
            item.Duration = await Task.Run(() =>
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                using var chunks = AudioFileChunks.Open(path);
                return chunks.Duration;
            });
            item.State = ItemState.Waiting;
            _status.Text = "";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            item.State = ItemState.Failed;
            item.Error = error.Message;
        }
        Refresh();
    }

    internal async Task TranscribeAsync()
    {
        if (_run is not null || !_queue.Any(item => item.State == ItemState.Waiting) || !OperatingSystem.IsWindows()) return;
        using var run = new CancellationTokenSource();
        _run = run;
        _progress.Value = 0;
        var language = LanguageCodes[Math.Max(0, _language.SelectedIndex)];
        var marks = _times.IsChecked == true;
        var cloud = Cloud;
        var model = SelectedModel;
        int done = 0, failed = 0;
        Refresh();
        try
        {
            while (!run.IsCancellationRequested && _queue.FirstOrDefault(item => item.State == ItemState.Waiting) is { } item)
            {
                if (await TranscribeOneAsync(item, cloud, model, language, marks, run)) done++;
                else failed++;
            }
            if (run.IsCancellationRequested)
                _status.Text = "Stopped. The text recognized so far is kept.";
            else if (_selected is { } shown && string.IsNullOrWhiteSpace(shown.Text) && done + failed == 1 && failed == 0)
                _status.Text = "No speech was found in this file.";
            else
                _status.Text = failed == 0 ? "Done. You can edit, copy or save the transcript." : $"Done: {done} file(s) transcribed, {failed} failed.";
            _progress.Value = 1;
        }
        finally
        {
            _run = null;
            Refresh();
        }
    }

    /// <summary>Transcribes one queued file; returns false when it failed.</summary>
    private async Task<bool> TranscribeOneAsync(QueueItem item, bool cloud, int model, string language, bool marks, CancellationTokenSource run)
    {
        item.State = ItemState.Transcribing;
        item.Progress = 0;
        item.Text = "";
        item.Cues.Clear();
        Show(item);
        _progress.Value = 0;
        var path = item.Path;
        try
        {
            var transcriber = _transcriber(cloud, model);
            await Task.Run(async () =>
            {
                Dispatcher.UIThread.Post(() => _status.Text = $"Preparing speech recognition for {item.Name}…");
                await using var batch = await transcriber.StartBatchAsync(language, run.Token);
                using var chunks = AudioFileChunks.Open(path);
                var total = chunks.Duration.TotalSeconds;
                var part = 0;
                while (true)
                {
                    var start = chunks.Position;
                    var pcm = chunks.Next(run.Token);
                    if (pcm is null) break;
                    part++;
                    var end = chunks.Position;
                    var label = $"{item.Name} · part {part} · {Clock(start)} – {Clock(end)} of {Clock(chunks.Duration)}";
                    Dispatcher.UIThread.Post(() => _status.Text = $"Transcribing {label}…");
                    string text;
                    try { text = await batch.TranscribeAsync(pcm, run.Token); }
                    finally { CryptographicOperations.ZeroMemory(pcm); }
                    var position = end.TotalSeconds;
                    Dispatcher.UIThread.Post(() =>
                    {
                        item.Progress = total > 0 ? Math.Min(1, position / total) : 0;
                        _progress.Value = item.Progress;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var trimmed = text.Trim();
                            item.Cues.AddRange(SubtitleFile.Spread(start, end, trimmed));
                            var mark = marks ? $"[{Clock(start)}] " : "";
                            item.Text = item.Text.Length == 0 ? mark + trimmed : item.Text + (marks ? "\n\n" : " ") + mark + trimmed;
                            if (ReferenceEquals(_selected, item)) Show(item);
                        }
                        RenderQueue();
                    });
                }
            }, run.Token);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            item.Progress = 1;
            item.State = ItemState.Done;
            return true;
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            item.State = ItemState.Stopped;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            item.State = ItemState.Failed;
            item.Error = error.Message;
            return false;
        }
        catch (Exception error)
        {
            ErrorLog.Write(error);
            item.State = ItemState.Failed;
            item.Error = "Could not transcribe this file. Check the speech recognition setting.";
            return false;
        }
        finally
        {
            if (ReferenceEquals(_selected, item)) Show(item);
            else Refresh();
        }
    }

    private string SuggestedName(string extension) => Path.GetFileNameWithoutExtension(_selected?.Path ?? "transcript") + extension;

    private async Task SaveAsync()
    {
        if (Storage is not { } storage || string.IsNullOrWhiteSpace(_result.Text)) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save transcript",
            SuggestedFileName = SuggestedName(".txt"),
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await WriteAsync(path, _result.Text);
    }

    private async Task SaveSrtAsync()
    {
        if (Storage is not { } storage || _selected is not { Cues.Count: > 0 } item) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save subtitles",
            SuggestedFileName = SuggestedName(".srt"),
            DefaultExtension = "srt",
            FileTypeChoices = [new FilePickerFileType("SubRip subtitles") { Patterns = ["*.srt"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await WriteAsync(path, SubtitleFile.ToSrt(item.Cues));
    }

    private async Task SaveAllAsync()
    {
        if (Storage is not { } storage) return;
        var items = _queue.Where(item => !string.IsNullOrWhiteSpace(item.Text)).ToArray();
        if (items.Length == 0) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder for the transcripts", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in items)
            {
                var stem = Path.GetFileNameWithoutExtension(item.Path);
                var name = stem + ".txt";
                for (var copy = 2; !used.Add(name); copy++) name = $"{stem} ({copy}).txt";
                await File.WriteAllTextAsync(Path.Combine(folder, name), item.Text);
            }
            _status.Text = $"Saved {items.Length} transcripts to {folder}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Could not save the files. Choose another folder.";
        }
    }

    private async Task WriteAsync(string path, string text)
    {
        try
        {
            await File.WriteAllTextAsync(path, text);
            _status.Text = $"Saved to {path}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Could not save the file. Choose another folder.";
        }
    }

    public void Stop()
    {
        _run?.Cancel();
        _downloading?.Cancel();
    }
}

/// <summary>A subtitle line with its time on the recording.</summary>
internal readonly record struct SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Builds SubRip (.srt) subtitles.</summary>
internal static class SubtitleFile
{
    /// <summary>Formats cues as SRT: numbered from 1, "hh:mm:ss,mmm --> hh:mm:ss,mmm", a blank line after each; empty cues are skipped.</summary>
    public static string ToSrt(IEnumerable<SubtitleCue> cues)
    {
        var builder = new System.Text.StringBuilder();
        var number = 0;
        foreach (var cue in cues)
        {
            var text = cue.Text.Trim();
            if (text.Length == 0) continue;
            var end = cue.End < cue.Start ? cue.Start : cue.End;
            builder.Append(++number).Append("\r\n")
                .Append(Stamp(cue.Start)).Append(" --> ").Append(Stamp(end)).Append("\r\n")
                .Append(text.ReplaceLineEndings("\r\n")).Append("\r\n\r\n");
        }
        return builder.ToString();
    }

    public static string ToSrt(IEnumerable<(TimeSpan Start, TimeSpan End, string Text)> cues) =>
        ToSrt(cues.Select(cue => new SubtitleCue(cue.Start, cue.End, cue.Text)));

    /// <summary>00:01:02,345</summary>
    public static string Stamp(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        var ms = (long)Math.Round(time.TotalMilliseconds);
        return $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}";
    }

    /// <summary>
    /// The engines return plain text per audio part, without word times, so a part is split into sentences
    /// (merged up to about <paramref name="maxChars"/> characters) and the part's time is shared by text length.
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Spread(TimeSpan start, TimeSpan end, string text, int maxChars = 84)
    {
        var sentences = System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?…])\s+")
            .Select(sentence => sentence.Trim()).Where(sentence => sentence.Length > 0).ToList();
        var lines = new List<string>();
        foreach (var sentence in sentences)
        {
            if (lines.Count > 0 && lines[^1].Length + 1 + sentence.Length <= maxChars) lines[^1] += " " + sentence;
            else lines.Add(sentence);
        }
        if (lines.Count == 0) return [];
        var total = lines.Sum(line => line.Length);
        var span = end - start;
        var cues = new List<SubtitleCue>(lines.Count);
        var used = 0;
        foreach (var line in lines)
        {
            var from = start + span * ((double)used / total);
            used += line.Length;
            var to = start + span * ((double)used / total);
            cues.Add(new SubtitleCue(from, to, line));
        }
        return cues;
    }
}
