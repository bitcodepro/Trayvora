using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NAudio.Wave;
using VoiceAgent.Speech;

namespace VoiceAgent.Desktop;

/// <summary>Turns text into speech, plays it in Trayvora and saves it as WAV or MP3.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class TextToAudioView : ToolPage, IDisposable
{
    private static readonly string[] CloudVoices = ["marin", "cedar", "coral", "alloy", "nova", "sage", "shimmer"];
    private const string Automatic = "Automatic by language";
    private const int MaxLength = 100_000;
    private readonly string _settingsFile;
    private readonly Func<string?> _recognitionKey;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 220, MaxLength = MaxLength, PlaceholderText = "Type or paste the text to speak" };
    private readonly TextBlock _counter = new() { FontSize = 11.5, Foreground = TrayvoraTheme.Faint, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ComboBox _engine = new() { ItemsSource = new[] { "Windows voice", "OpenAI voice" }, SelectedIndex = 0, MinWidth = 180 };
    private readonly ComboBox _voice = new() { MinWidth = 260 };
    private readonly NumericUpDown _speed = new() { MinWidth = 130 };
    private readonly TextBlock _engineNote = new() { FontSize = 12, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly Button _create = new() { Content = "Create audio" };
    private readonly Button _cancel = new() { Content = "Cancel", IsVisible = false };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    private readonly Button _play = new() { Content = "▶  Play" };
    private readonly Button _stop = new() { Content = "■  Stop" };
    private readonly Button _save = new() { Content = "Save as…" };
    private readonly TextBlock _audioInfo = new() { FontSize = 12.5, Foreground = TrayvoraTheme.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = TrayvoraTheme.Muted, TextWrapping = TextWrapping.Wrap };
    private PlaybackOptions _options = new();
    private IReadOnlyList<string> _windowsVoices = [];
    private bool _loading;
    private string? _audio;
    private string? _audioText;
    private CancellationTokenSource? _run;
    private CancellationTokenSource? _playback;
    private WasapiPlayer? _player;

    public TextToAudioView(string settingsDirectory, Func<string?> recognitionKey)
    {
        _settingsFile = Path.Combine(settingsDirectory, "playback.json");
        _recognitionKey = recognitionKey;
        TrayvoraTheme.Primary(_create);
        TrayvoraTheme.StyleControls(_text, _engine, _voice, _speed, _cancel, _play, _stop, _save);
        TrayvoraTheme.Ghost(_cancel);
        _text.FontSize = 14;
        _text.TextChanged += (_, _) => Refresh();
        _engine.SelectionChanged += (_, _) => { if (!_loading) FillVoices(); };
        _create.Click += async (_, _) => await CreateAsync();
        _cancel.Click += (_, _) => _run?.Cancel();
        _play.Click += async (_, _) => await PlayAsync();
        _stop.Click += (_, _) => StopPlayback();
        _save.Click += async (_, _) => await SaveAsync();

        var textCard = TrayvoraTheme.Card(TrayvoraTheme.Caption("Text"), _text, _counter);
        var voiceCard = TrayvoraTheme.Card(
            TrayvoraTheme.Caption("Voice"),
            TrayvoraTheme.Wrap(TrayvoraTheme.Field("Engine", _engine), TrayvoraTheme.Field("Voice", _voice), TrayvoraTheme.Field("Speed", _speed)),
            _engineNote,
            TrayvoraTheme.Row(_create, _cancel),
            _progress,
            _status);
        var audioCard = TrayvoraTheme.Card(
            TrayvoraTheme.Caption("Audio"),
            new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14,
                Children = { TrayvoraTheme.Row(_play, _stop, _save), WithColumn(_audioInfo, 1) },
            });
        Content = Frame("Text to audio", "Turn text into speech, listen to it here and save it as WAV or MP3.",
            new StackPanel { Spacing = 14, Children = { textCard, voiceCard, audioCard } });
        AttachedToVisualTree += (_, _) => LoadOptions();
        DetachedFromVisualTree += (_, _) => StopPlayback();
    }

    private static Control WithColumn(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private bool Cloud => _engine.SelectedIndex == 1;

    /// <summary>Starts from the voice, speed and key chosen in Playback settings.</summary>
    private void LoadOptions()
    {
        try { _options = SettingsFile.Read<PlaybackOptions>(_settingsFile) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { _options = new(); }
        if (_windowsVoices.Count == 0)
        {
            try { using var windows = new WindowsSpeechOutput(); _windowsVoices = windows.Voices(); }
            catch (Exception) { _windowsVoices = []; }
        }
        _loading = true;
        _engine.SelectedIndex = _options.Cloud ? 1 : 0;
        _loading = false;
        FillVoices();
        Refresh();
    }

    private void FillVoices()
    {
        if (Cloud)
        {
            _voice.ItemsSource = CloudVoices;
            _voice.SelectedItem = CloudVoices.Contains(_options.OpenAiVoice) ? _options.OpenAiVoice : CloudVoices[0];
            _speed.Minimum = 0.25m; _speed.Maximum = 4m; _speed.Increment = 0.05m; _speed.FormatString = "F2";
            _speed.Value = (decimal)Math.Clamp(_options.OpenAiSpeed, 0.25, 4);
            _engineNote.Text = _options.ResolveKey(_recognitionKey()) is null
                ? "OpenAI voice needs an API key. Save one in Speech recognition or in Playback settings."
                : "AI-generated voice. OpenAI receives the text and bills Audio API usage separately.";
        }
        else
        {
            var names = new[] { Automatic }.Concat(_windowsVoices).ToArray();
            _voice.ItemsSource = names;
            _voice.SelectedItem = _options.WindowsVoice is { } saved && names.Contains(saved) ? saved : Automatic;
            _speed.Minimum = -10; _speed.Maximum = 10; _speed.Increment = 1; _speed.FormatString = "F0";
            _speed.Value = Math.Clamp(_options.WindowsRate, -10, 10);
            _engineNote.Text = "Installed Windows voice, processed on this computer. Speed −10 … 10.";
        }
        Refresh();
    }

    private void Refresh()
    {
        var length = _text.Text?.Length ?? 0;
        _counter.Text = $"{length:N0} / {MaxLength:N0}";
        var idle = _run is null;
        _create.IsEnabled = idle && !string.IsNullOrWhiteSpace(_text.Text);
        _cancel.IsVisible = _progress.IsVisible = !idle;
        _engine.IsEnabled = _voice.IsEnabled = _speed.IsEnabled = _text.IsEnabled = idle;
        var hasAudio = _audio is not null && File.Exists(_audio);
        _play.IsEnabled = idle && hasAudio && _playback is null;
        _stop.IsEnabled = _playback is not null;
        _save.IsEnabled = idle && hasAudio;
        if (!hasAudio) _audioInfo.Text = "Create audio to listen to it and save it.";
    }

    protected override bool OnEscape()
    {
        if (_run is not null) { _run.Cancel(); return true; }
        if (_playback is not null) { StopPlayback(); return true; }
        return false;
    }

    private async Task CreateAsync()
    {
        var text = _text.Text?.Trim();
        if (_run is not null || string.IsNullOrWhiteSpace(text)) return;
        StopPlayback();
        using var run = new CancellationTokenSource();
        _run = run;
        _progress.Value = 0;
        var cloud = Cloud;
        var voice = _voice.SelectedItem as string;
        var speed = (double)(_speed.Value ?? 0);
        var target = Path.Combine(Path.GetTempPath(), $"trayvora-speech-{Guid.NewGuid():N}.wav");
        Refresh();
        try
        {
            if (cloud)
            {
                var key = _options.ResolveKey(_recognitionKey())
                    ?? throw new InvalidOperationException("Save an OpenAI key in Speech recognition or Playback settings first.");
                var speech = new OpenAiSpeechOutput(SpeechSettings.UnprotectKey(key));
                var pieces = SpeechFiles.Split(text, OpenAiSpeechOutput.MaxInputLength - 96);
                var clips = new List<Stream>();
                try
                {
                    for (var i = 0; i < pieces.Count; i++)
                    {
                        _status.Text = pieces.Count == 1 ? "Generating speech with OpenAI…" : $"Generating part {i + 1} of {pieces.Count} with OpenAI…";
                        clips.Add(await speech.GenerateAsync(pieces[i], voice ?? CloudVoices[0], speed, run.Token));
                        _progress.Value = (i + 1.0) / pieces.Count;
                    }
                    await Task.Run(() => SpeechFiles.JoinWav(clips, target), run.Token);
                }
                finally
                {
                    foreach (var clip in clips) await clip.DisposeAsync();
                }
            }
            else
            {
                _status.Text = "Creating speech with the Windows voice…";
                _progress.IsIndeterminate = true;
                var name = voice == Automatic ? null : voice;
                await Task.Run(() => WindowsSpeechOutput.SaveWave(text, (int)speed, name, target, run.Token), run.Token);
            }

            ReplaceAudio(target, text);
            _status.Text = "Audio is ready. Play it or save it as WAV or MP3.";
            _audioInfo.Text = $"Length {Clock(SpeechFiles.Duration(target))} · {new FileInfo(target).Length / 1024.0 / 1024.0:N1} MB WAV";
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            TryDelete(target);
            _status.Text = "Cancelled.";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDelete(target);
            _status.Text = error.Message;
        }
        catch (Exception error)
        {
            TryDelete(target);
            ErrorLog.Write(error);
            _status.Text = Cloud ? "Could not create OpenAI speech." : "Windows could not create speech. Choose another installed voice.";
        }
        finally
        {
            _run = null;
            _progress.IsIndeterminate = false;
            Refresh();
        }
    }

    private void ReplaceAudio(string path, string text)
    {
        if (_audio is not null && _audio != path) TryDelete(_audio);
        _audio = path;
        _audioText = text;
    }

    private async Task PlayAsync()
    {
        if (_audio is not { } path || _playback is not null) return;
        using var playback = new CancellationTokenSource();
        _playback = playback;
        Refresh();
        try
        {
            using var wave = new WaveFileReader(path);
            await using var output = new WasapiPlayerBuilder().Build();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is null) completed.TrySetResult();
                else completed.TrySetException(args.Exception);
            };
            output.Init(wave);
            output.Volume = Math.Clamp(_options.Volume, 0, 100) / 100f;
            _player = output;
            _status.Text = "Playing…";
            try
            {
                output.Play();
                await completed.Task.WaitAsync(playback.Token);
                _status.Text = "Playback finished.";
            }
            finally
            {
                _player = null;
                output.Stop();
            }
        }
        catch (OperationCanceledException) when (playback.IsCancellationRequested) { _status.Text = "Playback stopped."; }
        catch (Exception error)
        {
            ErrorLog.Write(error);
            _status.Text = "Could not play the audio. Check the Windows playback device.";
        }
        finally
        {
            _playback = null;
            Refresh();
        }
    }

    private void StopPlayback()
    {
        _playback?.Cancel();
        try { _player?.Stop(); } catch (Exception) { }
    }

    private async Task SaveAsync()
    {
        if (_audio is not { } source || Storage is not { } storage) return;
        var suggested = string.Concat((_audioText ?? "speech").Split(Path.GetInvalidFileNameChars())).Trim();
        if (suggested.Length > 40) suggested = suggested[..40].Trim();
        if (suggested.Length == 0) suggested = "speech";
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save audio",
            SuggestedFileName = suggested + ".mp3",
            DefaultExtension = "mp3",
            FileTypeChoices =
            [
                new FilePickerFileType("MP3 audio") { Patterns = ["*.mp3"] },
                new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        _status.Text = "Saving…";
        try
        {
            if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => File.Copy(source, path, overwrite: true));
            else
            {
                if (!string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)) path += ".mp3";
                await Task.Run(() => SpeechFiles.WavToMp3(source, path));
            }
            _status.Text = $"Saved to {path}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = error is IOException ? error.Message : "Could not save the file. Choose another folder.";
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _run?.Cancel();
        StopPlayback();
        if (_audio is not null) TryDelete(_audio);
    }
}
