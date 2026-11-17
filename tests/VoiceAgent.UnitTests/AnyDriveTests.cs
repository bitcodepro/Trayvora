using VoiceAgent.Desktop;
using VoiceAgent.Infrastructure;
using Xunit;

namespace VoiceAgent.UnitTests;

/// <summary>The portable build keeps all data next to Trayvora.exe, including on drive C: (for example in Downloads).</summary>
public sealed class AnyDriveTests
{
    [Fact]
    public void SettingsHistoryAndSessionsCanBeSavedOnDriveC()
    {
        if (!OperatingSystem.IsWindows()) return;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (!local.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)) return;
        var root = Path.Combine(local, "Temp", "trayvora-any-drive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = Path.Combine(root, "settings");
            SettingsFile.Write(Path.Combine(settings, "probe.json"), new DiscussionOptions(4));
            new DiscussionSettings(settings).SaveRounds(3);
            Assert.Equal(3, new DiscussionSettings(settings).Rounds);

            var store = new DiscussionStore(Path.Combine(settings, "discussions"));
            var room = store.Create(null);
            room.Messages.Add(new DiscussionMessage(DiscussionMember.Boss, DiscussionMember.Codex, "hello", DateTimeOffset.Now));
            store.Save(room);
            Assert.Equal("hello", Assert.Single(store.Load()).Messages[0].Text);

            var hotkeys = new HotkeySettings(settings);
            hotkeys.Save(HotkeyShortcut.Default);

            using (var history = new RequestHistory(Path.Combine(root, "history")))
            {
                var id = history.Start("question", Guid.Empty, localGit: false);
                history.Finish(id, RequestStatus.Completed, "answer", null, "");
            }
            Assert.True(File.Exists(Path.Combine(root, "history", "history.db")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
