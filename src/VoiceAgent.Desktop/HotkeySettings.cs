using System.Text.Json;
using VoiceAgent.Application;

namespace VoiceAgent.Desktop;

[Flags]
internal enum HotkeyModifiers : uint
{
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8, // MOD_WIN
}

internal sealed record HotkeyShortcut(HotkeyModifiers Modifiers, uint Key)
{
    public static HotkeyShortcut Default { get; } = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 'Z');

    private const HotkeyModifiers All = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win;

    /// <summary>Space, letters, digits and F1–F24 (Windows virtual-key codes).</summary>
    public static bool IsSupportedKey(uint key) => key == 0x20 || key is >= 'A' and <= 'Z' or >= '0' and <= '9' or >= 0x70 and <= 0x87;

    /// <summary>A global shortcut needs Ctrl, Alt or Win (Shift alone would block typing); F-keys may also be used alone.</summary>
    public bool IsValid =>
        (Modifiers & ~All) == 0 && IsSupportedKey(Key) &&
        ((Modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0 || Key >= 0x70);

    /// <summary>Windows virtual-key code for a pressed key, or null for modifiers and unsupported keys.</summary>
    public static uint? VirtualKey(Avalonia.Input.Key key) => key switch
    {
        Avalonia.Input.Key.Space => 0x20,
        >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z => (uint)('A' + (key - Avalonia.Input.Key.A)),
        >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => (uint)('0' + (key - Avalonia.Input.Key.D0)),
        >= Avalonia.Input.Key.NumPad0 and <= Avalonia.Input.Key.NumPad9 => (uint)('0' + (key - Avalonia.Input.Key.NumPad0)),
        >= Avalonia.Input.Key.F1 and <= Avalonia.Input.Key.F24 => (uint)(0x70 + (key - Avalonia.Input.Key.F1)),
        _ => null,
    };

    public static string KeyName(uint key) => key switch
    {
        0x20 => "Space",
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        _ => ((char)key).ToString(),
    };

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }
}

internal sealed class HotkeySettings(string directory)
{
    private const int Version = 1;
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "hotkey.json");
    private sealed record StoredShortcut(int Version, HotkeyShortcut Shortcut);

    public (HotkeyShortcut Shortcut, string? Warning) Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var legacy = Environment.GetEnvironmentVariable("VOICEAGENT_HOTKEY")?.ToUpperInvariant();
                return legacy switch
                {
                    "SPACE" => (new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x20), null),
                    { Length: 1 } when legacy[0] is >= 'A' and <= 'Z' =>
                        (new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, legacy[0]), null),
                    _ => (HotkeyShortcut.Default, null),
                };
            }

            using var input = File.OpenRead(_path);
            if (input.Length <= 4096 &&
                JsonSerializer.Deserialize<StoredShortcut>(input) is { Version: Version, Shortcut.IsValid: true } settings)
            {
                return (settings.Shortcut, null);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (HotkeyShortcut.Default, "Saved shortcut could not be read. Using the default.");
        }

        return (HotkeyShortcut.Default, "Saved shortcut is invalid. Using the default.");
    }

    public void Save(HotkeyShortcut shortcut)
    {
        var directory = Path.GetDirectoryName(_path)!;

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, new StoredShortcut(Version, shortcut));
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

internal sealed class HotkeyController(
    Func<HotkeyShortcut, IGlobalHotkeyService> create,
    Action<HotkeyShortcut> save) : IDisposable
{
    private IGlobalHotkeyService? _active;

    public event EventHandler? Pressed;
    public HotkeyShortcut? ActiveShortcut { get; private set; }

    public HotkeyRegistration Apply(HotkeyShortcut shortcut, bool persist)
    {
        if (!shortcut.IsValid)
        {
            throw new ArgumentException("Choose a supported shortcut.", nameof(shortcut));
        }

        if (shortcut == ActiveShortcut)
        {
            if (persist) save(shortcut);
            return new(true, 0);
        }

        var candidate = create(shortcut);
        var installed = false;
        try
        {
            var result = candidate.Start();
            if (!result.Registered)
            {
                return result;
            }

            if (persist) save(shortcut);
            var previous = _active;
            _active = candidate;
            ActiveShortcut = shortcut;
            candidate.Pressed += OnPressed;
            installed = true;
            if (previous is not null)
            {
                previous.Pressed -= OnPressed;
                previous.Dispose();
            }

            return result;
        }
        finally
        {
            if (!installed) candidate.Dispose();
        }
    }

    public void Dispose()
    {
        if (_active is not null)
        {
            _active.Pressed -= OnPressed;
            _active.Dispose();
            _active = null;
            ActiveShortcut = null;
        }
    }

    private void OnPressed(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, _active)) Pressed?.Invoke(sender, args);
    }
}
