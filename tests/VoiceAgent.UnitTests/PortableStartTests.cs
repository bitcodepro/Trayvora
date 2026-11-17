using Microsoft.Win32;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PortableStartTests
{
    [Fact]
    public void StartWithWindowsUsesTheExecutableAndUpgradesTheOldLauncherEntry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var testKey = $@"Software\TrayvoraTests\{Guid.NewGuid():N}\Run";
        var folder = @"E:\Apps\Trayvora-Ready";
        var startup = new StartupRegistration(Path.Combine(folder, "Trayvora.exe"), testKey);
        try
        {
            Assert.Equal(StartupRegistration.State.Disabled, startup.Current);

            // Old entry written by Trayvora-Startup.ps1 for this folder.
            using (var key = Registry.CurrentUser.CreateSubKey(testKey))
                key.SetValue(StartupRegistration.ValueName, $"\"C:\\pwsh.exe\" -NoLogo -File \"{folder}\\Trayvora-Launch.ps1\"");
            Assert.Equal(StartupRegistration.State.Enabled, startup.Current);
            Assert.True(startup.MigrateLauncherEntry());
            Assert.Equal("\"E:\\Apps\\Trayvora-Ready\\Trayvora.exe\"", Value());
            Assert.False(startup.MigrateLauncherEntry());

            Assert.Null(startup.Set(false));
            Assert.Equal(StartupRegistration.State.Disabled, startup.Current);
            Assert.Null(Value());
            Assert.Null(startup.Set(true));
            Assert.Equal(startup.Command, Value());

            // Another Trayvora folder owns the entry: refuse to take it over or delete it.
            var other = new StartupRegistration(@"E:\Other\Trayvora.exe", testKey);
            Assert.Equal(StartupRegistration.State.OtherFolder, other.Current);
            Assert.NotNull(other.Set(true));
            Assert.Null(other.Set(false));
            Assert.Equal(startup.Command, Value());
            Assert.False(other.MigrateLauncherEntry());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\TrayvoraTests", throwOnMissingSubKey: false);
        }

        string? Value()
        {
            using var key = Registry.CurrentUser.OpenSubKey(testKey);
            return key?.GetValue(StartupRegistration.ValueName) as string;
        }
    }

    [Fact]
    public void ExecutableSetsUpThePortableFoldersLikeTheOldLauncher()
    {
        if (!OperatingSystem.IsWindows()) return;
        string[] names = ["VOICEAGENT_SETTINGS_DIR", "VOICEAGENT_MODEL_DIR", "VOICEAGENT_HISTORY_DIR", "VOICEAGENT_CODEX_DIR",
            "CODEX_HOME", "CODEX_SQLITE_HOME", "TEMP", "TMP", "APPDATA", "LOCALAPPDATA", "VOICEAGENT_CODEX_EXE", "VOICEAGENT_DESKTOP_CODEX_HOME"];
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var root = Directory.CreateTempSubdirectory("trayvora-portable-");
        try
        {
            Environment.SetEnvironmentVariable("VOICEAGENT_SETTINGS_DIR", null);
            PortableEnvironment.Apply(root.FullName);
            string Var(string name) => Environment.GetEnvironmentVariable(name)!;
            Assert.Equal(Path.Combine(root.FullName, "settings"), Var("VOICEAGENT_SETTINGS_DIR"));
            Assert.Equal(Path.Combine(root.FullName, "models"), Var("VOICEAGENT_MODEL_DIR"));
            Assert.Equal(Path.Combine(root.FullName, "history"), Var("VOICEAGENT_HISTORY_DIR"));
            Assert.Equal(Path.Combine(root.FullName, "codex-data"), Var("CODEX_HOME"));
            Assert.Equal(Var("CODEX_HOME"), Var("VOICEAGENT_CODEX_DIR"));
            Assert.Equal(Path.Combine(root.FullName, "tmp"), Var("TEMP"));
            Assert.Equal(Path.Combine(root.FullName, "localappdata"), Var("LOCALAPPDATA"));
            Assert.True(Directory.Exists(Path.Combine(root.FullName, "codex-data")));

            // A development shell that already chose folders is left alone.
            Environment.SetEnvironmentVariable("VOICEAGENT_SETTINGS_DIR", @"E:\dev\settings");
            PortableEnvironment.Apply(Path.Combine(root.FullName, "other"));
            Assert.Equal(@"E:\dev\settings", Var("VOICEAGENT_SETTINGS_DIR"));
            Assert.Equal(Path.Combine(root.FullName, "models"), Var("VOICEAGENT_MODEL_DIR"));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CodexIsFoundFromTheOverrideAndRemembered()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Directory.CreateTempSubdirectory("trayvora-codex-");
        var previous = Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE");
        try
        {
            var codex = Path.Combine(folder.FullName, "codex.exe");
            File.WriteAllBytes(codex, []);
            var cache = Path.Combine(folder.FullName, "settings", "codex-exe.txt");
            Environment.SetEnvironmentVariable("VOICEAGENT_CODEX_EXE", codex);
            Assert.Equal(codex, PortableEnvironment.FindCodex(cache));
            Assert.Equal(codex, File.ReadAllText(cache));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOICEAGENT_CODEX_EXE", previous);
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void CodexDesktopProfileMustHaveSessions()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Directory.CreateTempSubdirectory("trayvora-codex-home-");
        try
        {
            Assert.Null(PortableEnvironment.DesktopCodexHome(folder.FullName));
            Directory.CreateDirectory(Path.Combine(folder.FullName, "sessions"));
            Assert.Equal(folder.FullName, PortableEnvironment.DesktopCodexHome(folder.FullName));
            Assert.Null(PortableEnvironment.DesktopCodexHome(Path.Combine(folder.FullName, "missing")));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
