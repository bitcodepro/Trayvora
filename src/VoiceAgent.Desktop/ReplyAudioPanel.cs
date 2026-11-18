using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NAudio.Wave;
using VoiceAgent.Speech;

namespace VoiceAgent.Desktop;

internal enum PlaybackKeySource { Recognition, Separate }

internal sealed record PlaybackOptions(bool Cloud = false, string? WindowsVoice = null, int WindowsRate = 2,
    string OpenAiVoice = "marin", double OpenAiSpeed = 1.15, string? ProtectedKey = null, PlaybackKeySource? KeySource = null, int Volume = 100)
{
    public PlaybackKeySource SelectKeySource(string? recognitionKey) =>
        KeySource is PlaybackKeySource.Recognition or PlaybackKeySource.Separate ? KeySource.Value
        : ProtectedKey is null && recognitionKey is not null ? PlaybackKeySource.Recognition : PlaybackKeySource.Separate;

    public string? ResolveKey(string? recognitionKey) => SelectKeySource(recognitionKey) == PlaybackKeySource.Recognition
        ? recognitionKey : ProtectedKey;
}

internal sealed class ReplyAudioPanel : StackPanel, IDisposable
{
    private static readonly string[] CloudVoices = ["marin", "cedar", "coral", "alloy", "nova", "sage", "shimmer"];
    private const string Automatic = "Automatic by language";
    private const string Sample = "Проверка звука. Trayvora готова озвучивать ответы.";
    private readonly WindowsSpeechOutput _windows = new();
    private readonly Func<string?> _text;
    private readonly Func<string?> _recognitionKey;
    private readonly Action _refreshKeyChoices;
    private readonly string _file;
    private readonly Button _play = new() { Content = "Read text" };
    private readonly Button _sample = new() { Content = "Test sound" };
    private readonly Button _stop = new() { Content = "Stop audio", IsEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = TrayvoraTheme.Muted };
    private PlaybackOptions _options;
    private CancellationTokenSource? _playback;
    private WasapiPlayer? _cloudOutput;
    private bool _idle = true;

    public ReplyAudioPanel(string directory, Func<string?> text, Control auto, Func<string?> recognitionKey)
    {
        _text = text;
        _recognitionKey = recognitionKey;
        _file = Path.Combine(directory, "playback.json");
        try { _options = SettingsFile.Read<PlaybackOptions>(_file) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _options = new();
        }

        var provider = new ComboBox { ItemsSource = new[] { "Windows voice", "OpenAI Audio API" }, SelectedIndex = _options.Cloud ? 1 : 0 };
        var localVoice = new ComboBox();
        var localRate = new NumericUpDown { Minimum = -10, Maximum = 10, Value = Math.Clamp(_options.WindowsRate, -10, 10), Increment = 1 };
        var cloudVoice = new ComboBox { ItemsSource = CloudVoices, SelectedItem = CloudVoices.Contains(_options.OpenAiVoice) ? _options.OpenAiVoice : CloudVoices[0] };
        var speed = new NumericUpDown
        {
            Minimum = 0.25m, Maximum = 4m, Value = (decimal)Math.Clamp(_options.OpenAiSpeed, .25, 4), Increment = .05m, FormatString = "F2",
        };
        var keySource = new ComboBox();
        var key = new TextBox { PasswordChar = '●', MaxLength = 512 };
        var forget = new Button { Content = "Forget separate playback key" };
        var note = new TextBlock
        {
            Text = "AI-generated voice. OpenAI receives the text being read and bills Audio API usage separately.",
            TextWrapping = TextWrapping.Wrap, Foreground = TrayvoraTheme.Muted,
        };
        TrayvoraTheme.StyleControls(provider, localVoice, cloudVoice, keySource, key, forget, _play, _sample, _stop);
        static StackPanel Field(string label, Control input) =>
            new() { Spacing = 4, Children = { new Label { Content = label, Target = input }, input } };
        var local = new StackPanel { Spacing = 5, Children = { Field("Installed voice", localVoice), Field("Windows rate", localRate) } };
        var cloud = new StackPanel
        {
            Spacing = 5, Children = { Field("OpenAI voice", cloudVoice), Field("Speed multiplier", speed), Field("API key source", keySource), key, note },
        };
        var settings = new Expander
        {
            Header = "Playback settings",
            Content = new StackPanel { Spacing = 8, Children = { provider, local, cloud, forget } },
        };

        void UpdateFields()
        {
            cloud.IsVisible = provider.SelectedIndex == 1;
            local.IsVisible = provider.SelectedIndex == 0;
            var separate = keySource.SelectedIndex == (int)PlaybackKeySource.Separate;
            key.IsVisible = separate;
            forget.IsVisible = cloud.IsVisible && separate && _options.ProtectedKey is not null;
            key.PlaceholderText = _options.ProtectedKey is null ? "OpenAI API key for playback" : "API key saved · type to replace";
        }

        var updatingKeySource = false;
        var loadingVoices = false;
        var saving = false;
        var keySourceEdited = false;
        _refreshKeyChoices = () =>
        {
            var saved = _recognitionKey();
            var selection = keySourceEdited && keySource.SelectedIndex >= 0
                ? (PlaybackKeySource)keySource.SelectedIndex : _options.SelectKeySource(saved);
            updatingKeySource = true;
            keySource.ItemsSource = new[]
            {
                saved is null ? "Recognition key unavailable" : "Use saved recognition key",
                "Separate playback key",
            };
            keySource.SelectedIndex = (int)selection;
            updatingKeySource = false;
            UpdateFields();
        };
        _refreshKeyChoices();

        settings.PropertyChanged += (_, args) =>
        {
            if (args.Property != Expander.IsExpandedProperty || !settings.IsExpanded) return;
            IReadOnlyList<string> names = [];
            try { names = _windows.Voices(); }
            catch (Exception) { _status.Text = "Could not list Windows voices."; }
            loadingVoices = true;
            localVoice.ItemsSource = new[] { Automatic }.Concat(names).ToArray();
            localVoice.SelectedItem = _options.WindowsVoice ?? Automatic;
            if (localVoice.SelectedIndex < 0) localVoice.SelectedIndex = 0;
            loadingVoices = false;
        };
        void SavePlayback()
        {
            if (saving || loadingVoices || updatingKeySource || !OperatingSystem.IsWindows()) return;
            saving = true;
            try
            {
                var useCloud = provider.SelectedIndex == 1;
                var source = useCloud ? (PlaybackKeySource?)(keySource.SelectedIndex == 0 ? PlaybackKeySource.Recognition : PlaybackKeySource.Separate) : _options.KeySource;
                var protectedKey = _options.ProtectedKey;
                if (useCloud && source == PlaybackKeySource.Separate && !string.IsNullOrWhiteSpace(key.Text))
                    protectedKey = SpeechSettings.ProtectKey(key.Text);

                var selectedVoice = localVoice.SelectedIndex < 0 ? _options.WindowsVoice : localVoice.SelectedItem as string;
                Save(new PlaybackOptions(useCloud, selectedVoice == Automatic ? null : selectedVoice,
                    (int)(localRate.Value ?? 2), (string?)cloudVoice.SelectedItem ?? CloudVoices[0],
                    (double)(speed.Value ?? 1.15m), protectedKey, source, _options.Volume));
                key.Clear();
                keySourceEdited = false;
                _refreshKeyChoices();
                if (useCloud && _options.ResolveKey(_recognitionKey()) is null) _status.Text = MissingKey(source);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                _status.Text = "Could not save playback settings.";
            }
            finally { saving = false; }
        }

        provider.SelectionChanged += (_, _) => { UpdateFields(); SavePlayback(); };
        keySource.SelectionChanged += (_, _) =>
        {
            if (updatingKeySource) return;
            keySourceEdited = true;
            UpdateFields();
            SavePlayback();
        };
        localVoice.SelectionChanged += (_, _) => SavePlayback();
        cloudVoice.SelectionChanged += (_, _) => SavePlayback();
        localRate.ValueChanged += (_, _) => SavePlayback();
        speed.ValueChanged += (_, _) => SavePlayback();
        key.LostFocus += (_, _) => { if (!string.IsNullOrWhiteSpace(key.Text)) SavePlayback(); };
        key.KeyDown += (_, args) =>
        {
            if (args.Key != Avalonia.Input.Key.Enter || string.IsNullOrWhiteSpace(key.Text)) return;
            SavePlayback();
            args.Handled = true;
        };
        forget.Click += (_, _) =>
        {
            try
            {
                var wasUsingSeparateKey = _options.SelectKeySource(_recognitionKey()) == PlaybackKeySource.Separate;
                Save(_options with { Cloud = _options.Cloud && !wasUsingSeparateKey, ProtectedKey = null });
                key.Clear();
                provider.SelectedIndex = _options.Cloud ? 1 : 0;
                keySourceEdited = false;
                _refreshKeyChoices();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _status.Text = "Could not remove the playback key.";
            }
        };

        Spacing = 8;
        Children.AddRange([auto, TrayvoraTheme.Row(_play, _sample, _stop), settings, _status]);
        _play.Click += async (_, _) => await ReadAsync(_text());
        _sample.Click += async (_, _) => await ReadAsync(string.IsNullOrWhiteSpace(_text()) ? Sample : _text());
        _stop.Click += (_, _) => Stop();
        UpdateFields();
        Refresh(true);
    }

    public string? SavedSeparateKey => _options.ProtectedKey;

    public void RefreshKeyChoices() => _refreshKeyChoices();

    public void Refresh(bool idle)
    {
        _idle = idle;
        _play.IsEnabled = _idle && !string.IsNullOrWhiteSpace(_text());
        _sample.IsEnabled = _idle;
    }

    private void Save(PlaybackOptions options)
    {
        SettingsFile.Write(_file, options);
        Stop();
        _options = options;
        _status.Text = "Playback settings saved.";
    }

    public async Task ReadAsync(string? text)
    {
        if (!_idle || !OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(text)) return;
        Stop();
        var options = _options;
        if (!options.Cloud)
        {
            try
            {
                _windows.Speak(text, options.WindowsRate, options.WindowsVoice, Math.Clamp(options.Volume, 0, 100));
                _stop.IsEnabled = true;
                _status.Text = "Playing Windows voice.";
            }
            catch (InvalidOperationException error) { _status.Text = error.Message; }
            catch (Exception) { _status.Text = "Windows speech playback is unavailable."; }
            return;
        }

        using var playback = new CancellationTokenSource();
        _playback = playback;
        _stop.IsEnabled = true;
        var token = playback.Token;
        try
        {
            _status.Text = "Generating speech with OpenAI…";
            using var audio = await GenerateCloudAsync(options, text, token);
            token.ThrowIfCancellationRequested();
            using var wave = new WaveFileReader(audio);
            await using var output = new WasapiPlayerBuilder().Build();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is null) completed.TrySetResult();
                else completed.TrySetException(args.Exception);
            };
            output.Init(wave);
            output.Volume = Math.Clamp(_options.Volume, 0, 100) / 100f;
            _cloudOutput = output;
            _status.Text = "Playing AI-generated voice.";
            try
            {
                output.Play();
                await completed.Task.WaitAsync(token);
            }
            finally
            {
                if (ReferenceEquals(_cloudOutput, output)) _cloudOutput = null;
                output.Stop();
            }
            if (!token.IsCancellationRequested) _status.Text = "Playback complete.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        {
            if (!token.IsCancellationRequested) { ErrorLog.Write(error, "Reading aloud"); _status.Text = error.Message; }
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) { ErrorLog.Write(error, "Reading aloud (OpenAI voice)"); _status.Text = "Could not generate or play OpenAI speech."; }
        }
        finally
        {
            if (ReferenceEquals(_playback, playback))
            {
                _playback = null;
                _stop.IsEnabled = false;
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private Task<Stream> GenerateCloudAsync(PlaybackOptions options, string text, CancellationToken token)
    {
        var recognitionKey = _recognitionKey();
        var protectedKey = options.ResolveKey(recognitionKey)
            ?? throw new InvalidOperationException(MissingKey(options.SelectKeySource(recognitionKey)));
        return new OpenAiSpeechOutput(SpeechSettings.UnprotectKey(protectedKey))
            .GenerateAsync(text, options.OpenAiVoice, options.OpenAiSpeed, token);
    }

    private static string MissingKey(PlaybackKeySource? source) => source == PlaybackKeySource.Recognition
        ? "Save a key in Speech recognition or choose Separate playback key."
        : "Enter a separate playback key or choose Use saved recognition key.";

    public void AdjustVolume(int steps)
    {
        var volume = Math.Clamp(Math.Clamp(_options.Volume, 0, 100) + Math.Clamp(steps, -20, 20) * 5, 0, 100);
        _options = _options with { Volume = volume };
        try
        {
            _windows.SetVolume(volume);
            if (OperatingSystem.IsWindows() && _cloudOutput is not null) _cloudOutput.Volume = volume / 100f;
            _status.Text = $"Playback volume: {volume}%";
            SettingsFile.Write(_file, _options);
        }
        catch (Exception)
        {
            _status.Text = "Could not apply or save playback volume.";
        }
    }

    public void Stop()
    {
        _playback?.Cancel();
        _playback = null;
        try { _windows.Stop(); _status.Text = ""; }
        catch (Exception) { _status.Text = "Windows could not stop speech playback."; }
        _stop.IsEnabled = false;
    }

    public void Dispose()
    {
        Stop();
        _windows.Dispose();
    }
}
