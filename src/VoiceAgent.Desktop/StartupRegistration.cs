using Microsoft.Win32;

namespace VoiceAgent.Desktop;

/// <summary>"Start with Windows" for this Trayvora folder (HKCU Run), replacing the old Manage-Startup script.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class StartupRegistration(string executable, string runKey = StartupRegistration.RunKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Trayvora.VoiceAgent";

    private readonly string _folder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(executable))!);
    public string Command => $"\"{Path.GetFullPath(executable)}\"";

    public enum State { Disabled, Enabled, OtherFolder }

    public State Current
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey);
            return key?.GetValue(ValueName) is not string value ? State.Disabled
                : BelongsHere(value) ? State.Enabled : State.OtherFolder;
        }
    }

    /// <summary>The entry points at this folder: the executable itself or the old launcher script inside it.</summary>
    private bool BelongsHere(string command) =>
        command.Contains(_folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rewrites an old PowerShell-launcher entry for this folder so it starts the executable directly.</summary>
    public bool MigrateLauncherEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
        if (key?.GetValue(ValueName) is not string value || !BelongsHere(value) || value == Command) return false;
        key.SetValue(ValueName, Command, RegistryValueKind.String);
        return true;
    }

    /// <summary>Tray check item; also upgrades an old launcher entry for this folder to start the executable.</summary>
    public static Avalonia.Controls.NativeMenuItem CreateMenuItem(string executable, Action<string> refused)
    {
        var startup = new StartupRegistration(executable);
        static bool Expected(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException or IOException;
        try { startup.MigrateLauncherEntry(); }
        catch (Exception error) when (Expected(error)) { }
        var item = new Avalonia.Controls.NativeMenuItem("Start with Windows") { ToggleType = Avalonia.Controls.MenuItemToggleType.CheckBox };
        void Show()
        {
            try
            {
                var state = startup.Current;
                item.IsChecked = state == State.Enabled;
                item.Header = state == State.OtherFolder ? "Start with Windows (used by another Trayvora folder)" : "Start with Windows";
            }
            catch (Exception error) when (Expected(error)) { item.IsEnabled = false; }
        }
        item.Click += (_, _) =>
        {
            try
            {
                if (startup.Set(startup.Current != State.Enabled) is { } message) refused(message);
            }
            catch (Exception error) when (Expected(error)) { }
            Show();
        };
        Show();
        return item;
    }

    /// <returns>A message for the user when the change was refused, otherwise null.</returns>
    public string? Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runKey, writable: true);
        var value = key.GetValue(ValueName) as string;
        if (enabled)
        {
            if (value is not null && !BelongsHere(value))
                return "Another Trayvora folder starts with Windows. Turn it off there first.";
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else if (value is not null && BelongsHere(value))
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        return null;
    }
}
