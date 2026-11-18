using System.Text.Json;
using VoiceAgent.Application;

namespace VoiceAgent.Desktop;

internal enum VoiceAction { Preview, Background, Send, Dictate, SharedChat, LongQuestions, ShortQuestions, WindowsAudio, StopSpeech, Anywhere }

internal sealed record VoiceHotkeys(HotkeyShortcut Preview, HotkeyShortcut Background, HotkeyShortcut Send, HotkeyShortcut? Dictation = null, HotkeyShortcut? Shared = null, HotkeyShortcut? Long = null, HotkeyShortcut? Short = null, HotkeyShortcut? Loopback = null, HotkeyShortcut? StopSpeech = null, HotkeyShortcut? Anywhere = null)
{
    /// <summary>A readable name for the Activity page.</summary>
    public static string Describe(VoiceAction action) => action switch
    {
        VoiceAction.Preview => "Voice → preview",
        VoiceAction.Background => "Voice → background",
        VoiceAction.Send => "Send preview",
        VoiceAction.Dictate => "Dictation",
        VoiceAction.SharedChat => "Shared chat",
        VoiceAction.LongQuestions => "Deep question",
        VoiceAction.ShortQuestions => "Quick question",
        VoiceAction.WindowsAudio => "Computer sound",
        VoiceAction.StopSpeech => "Stop reading aloud",
        VoiceAction.Anywhere => "Dictate anywhere",
        _ => action.ToString(),
    };

    public static VoiceHotkeys Default(HotkeyShortcut preview)
    {
        var remaining = new[] { Pick('Z'), Pick('X'), Pick('V') }.Where(key => key != preview).ToArray();
        return new(preview, remaining[0], remaining[1]);
    }

    public HotkeyShortcut DictationShortcut => Dictation ??
        "DQWERTYUIOPASFGHJKLCBNMZXV".Select(Pick).First(key => key != Preview && key != Background && key != Send && key != Loopback);

    public HotkeyShortcut SharedShortcut => Shared ??
        "GQWERTYUIOPASDFHJKLCBNMZXV".Select(Pick)
            .First(key => key != Preview && key != Background && key != Send && key != Loopback && key != DictationShortcut);

    public HotkeyShortcut LongShortcut => Long ?? PickQuestion('L', Short);
    public HotkeyShortcut ShortShortcut => Short ?? PickQuestion('Q', LongShortcut);

    private HotkeyShortcut PickQuestion(char preferred, HotkeyShortcut? other) =>
        new[] { preferred }.Concat("QWERTYUIOPASDFGHJKLZXCVBNM").Select(Pick)
            .First(key => key != Preview && key != Background && key != Send && key != Loopback &&
                key != DictationShortcut && key != SharedShortcut && key != other);

    public HotkeyShortcut LoopbackShortcut => Loopback ??
        "WERTYUIOPASDFGHJKLZXCVBNMQ".Select(Pick)
            .First(key => key != Preview && key != Background && key != Send &&
                key != DictationShortcut && key != SharedShortcut && key != LongShortcut && key != ShortShortcut);

    /// <summary>Stops any reply being read aloud (Windows voice or OpenAI voice).</summary>
    public HotkeyShortcut StopSpeechShortcut => StopSpeech ??
        "SQWERTYUIOPADFGHJKLZXCVBNM".Select(Pick)
            .First(key => key != Preview && key != Background && key != Send && key != DictationShortcut &&
                key != SharedShortcut && key != LongShortcut && key != ShortShortcut && key != LoopbackShortcut);

    /// <summary>Dictate into any app: typed at the text cursor, otherwise copied to the clipboard.</summary>
    public HotkeyShortcut AnywhereShortcut => Anywhere ??
        "AQWERTYUIOPSDFGHJKLZXCVBNM".Select(Pick)
            .First(key => key != Preview && key != Background && key != Send && key != DictationShortcut &&
                key != SharedShortcut && key != LongShortcut && key != ShortShortcut && key != LoopbackShortcut && key != StopSpeechShortcut);

    private static HotkeyShortcut Pick(char key) =>
        new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, key);

    public IReadOnlyDictionary<VoiceAction, HotkeyShortcut> Entries => new Dictionary<VoiceAction, HotkeyShortcut>
    {
        [VoiceAction.Preview] = Preview,
        [VoiceAction.Background] = Background,
        [VoiceAction.Send] = Send,
        [VoiceAction.Dictate] = DictationShortcut,
        [VoiceAction.SharedChat] = SharedShortcut,
        [VoiceAction.LongQuestions] = LongShortcut,
        [VoiceAction.ShortQuestions] = ShortShortcut,
        [VoiceAction.WindowsAudio] = LoopbackShortcut,
        [VoiceAction.StopSpeech] = StopSpeechShortcut,
        [VoiceAction.Anywhere] = AnywhereShortcut,
    };
}

internal sealed class VoiceHotkeyBindings(Func<HotkeyShortcut, IGlobalHotkeyService> create, Action<VoiceHotkeys> save) : IDisposable
{
    private Dictionary<HotkeyShortcut, IGlobalHotkeyService> _services = [];
    public event Action<VoiceAction>? Pressed;

    public string? Apply(VoiceHotkeys settings, bool persist)
    {
        var entries = settings.Entries;
        if (entries.Values.Any(key => key is null || !key.IsValid) || entries.Values.Distinct().Count() != entries.Count)
            return "Choose different valid shortcuts.";

        var next = new Dictionary<HotkeyShortcut, IGlobalHotkeyService>();
        var created = new List<IGlobalHotkeyService>();
        var installed = false;
        string? warning = null;
        try
        {
            foreach (var (action, shortcut) in entries)
            {
                if (!_services.TryGetValue(shortcut, out var service))
                {
                    service = create(shortcut);
                    created.Add(service);
                    var result = service.Start();
                    if (!result.Registered)
                    {
                        warning = $"{shortcut} is unavailable for {action} (Windows error {result.ErrorCode}).";
                        if (persist) return warning;
                        continue;
                    }

                    service.Pressed += (_, _) => OnPressed(service);
                }

                next.Add(shortcut, service);
            }

            if (persist) save(settings);
            var previous = _services;
            _services = next;
            _actions = entries;
            installed = true;
            foreach (var service in previous.Values.Concat(created).Except(next.Values).Distinct()) service.Dispose();
            return warning;
        }
        finally
        {
            if (!installed)
                foreach (var service in created) service.Dispose();
        }
    }

    private IReadOnlyDictionary<VoiceAction, HotkeyShortcut> _actions = new Dictionary<VoiceAction, HotkeyShortcut>();

    private void OnPressed(IGlobalHotkeyService service)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var (action, shortcut) in _actions)
                if (_services.TryGetValue(shortcut, out var active) && ReferenceEquals(active, service))
                    Pressed?.Invoke(action);
        });
    }

    public void Dispose()
    {
        foreach (var service in _services.Values) service.Dispose();
        _services = [];
    }
}

internal static class SettingsFile
{
    public static T? Read<T>(string file) where T : class
    {
        if (!File.Exists(file)) return null;
        using var stream = File.OpenRead(file);
        return stream.Length <= 16384 ? JsonSerializer.Deserialize<T>(stream) : null;
    }

    public static void Write<T>(string file, T value)
    {
        var folder = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(folder);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value));
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
