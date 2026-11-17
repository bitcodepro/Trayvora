using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class DiscussionStoreTests
{
    [Fact]
    public void RoomsKeepMessagesAndBindingsAcrossReloadsUntilDeleted()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-discussions-");
        try
        {
            var store = new DiscussionStore(folder.FullName);
            var claude = new ClaudeChat(Guid.NewGuid(), folder.FullName, "Planning session");
            var first = store.Create(claude);
            Assert.Equal(first.Id, first.CodexChatId);
            Assert.True(first.OwnCodexThread);

            var codexChat = Guid.NewGuid();
            first.Title = "Onboarding";
            first.CodexChatId = codexChat;
            first.CodexChatTitle = "Trayvora chat · Chat 2";
            first.Messages.Add(new DiscussionMessage(DiscussionMember.Boss, DiscussionMember.Codex, "Hello", DateTimeOffset.Now.AddMinutes(-2)));
            first.Messages.Add(new DiscussionMessage(DiscussionMember.Codex, null, "Hi", DateTimeOffset.Now));
            first.Updated = DateTimeOffset.Now;
            store.Save(first);
            var second = store.Create(null);
            second.Updated = DateTimeOffset.Now.AddDays(-1);
            store.Save(second);

            var reloaded = new DiscussionStore(folder.FullName).Load();
            Assert.Equal(2, reloaded.Count);
            var room = reloaded[0];
            Assert.Equal(first.Id, room.Id);
            Assert.Equal("Onboarding", room.Title);
            Assert.Equal(codexChat, room.CodexChatId);
            Assert.False(room.OwnCodexThread);
            Assert.Equal(claude, room.Claude);
            Assert.Equal(["Hello", "Hi"], room.Messages.Select(message => message.Text));
            Assert.Equal(DiscussionMember.Codex, room.Messages[0].To);
            Assert.Null(reloaded[1].Claude);

            store.Delete(first.Id);
            Assert.Equal([second.Id], store.Load().Select(item => item.Id));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void CorruptFilesAreSkipped()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-discussions-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, $"{Guid.NewGuid():N}.json"), "{ not json");
            File.WriteAllText(Path.Combine(folder.FullName, "notes.json"), "{}");
            var store = new DiscussionStore(folder.FullName);
            var room = store.Create(null);
            Assert.Equal([room.Id], store.Load().Select(item => item.Id));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void AgentsReceiveOnlyTheMessageAddressedToThem()
    {
        var room = new DiscussionRoom { Title = "Release" };
        room.Messages.Add(new DiscussionMessage(DiscussionMember.Boss, DiscussionMember.Claude, "private to Claude", DateTimeOffset.Now));

        // Boss's message goes exactly as typed: no session header, no routing rules, nothing from other messages.
        Assert.Equal("Check the build", DiscussionWindow.Envelope(room, DiscussionMember.Codex, DiscussionMember.Boss, "Check the build", null));
        Assert.Equal("2 + 5?", DiscussionWindow.Envelope(room, DiscussionMember.Claude, DiscussionMember.Boss, "2 + 5?", null, relay: true));

        // Text from the other agent only gets a short label.
        Assert.Equal("Codex wrote:\nPlan text", DiscussionWindow.Envelope(room, DiscussionMember.Claude, DiscussionMember.Boss, "Plan text", DiscussionMember.Codex));
        Assert.Equal("Codex wrote:\ncheck this", DiscussionWindow.Envelope(room, DiscussionMember.Claude, DiscussionMember.Codex, "check this", null));
    }

    [Fact]
    public void DiscussionsStillCarryTheirRules()
    {
        var room = new DiscussionRoom { Title = "Release" };
        var lead = DiscussionWindow.Envelope(room, DiscussionMember.Codex, DiscussionMember.Boss, "Pick a plan", null,
            discuss: new DiscussionPlan(DiscussionMember.Codex, DiscussionMember.Claude, 3));
        Assert.Contains("You lead the discussion", lead);
        Assert.Contains("Pick a plan", lead);
    }

    [Fact]
    public void MessagesForwardOnlyToAgentsThatDidNotReceiveThem()
    {
        DiscussionMember[] Targets(DiscussionMessage message) => DiscussionWindow.ForwardTargets(message).ToArray();
        var now = DateTimeOffset.Now;
        Assert.Equal([DiscussionMember.Claude], Targets(new(DiscussionMember.Codex, DiscussionMember.Boss, "x", now)));
        Assert.Equal([DiscussionMember.Codex], Targets(new(DiscussionMember.Claude, DiscussionMember.Boss, "x", now)));
        Assert.Equal([DiscussionMember.Claude], Targets(new(DiscussionMember.Boss, DiscussionMember.Codex, "x", now)));
        Assert.Empty(Targets(new(DiscussionMember.Boss, null, "to both", now)));
        Assert.Empty(Targets(new(DiscussionMember.Claude, null, "error", now, Failed: true)));
    }

    [Fact]
    public void CodexDesktopBindingAndForwardsSurviveReload()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-discussions-");
        try
        {
            var store = new DiscussionStore(folder.FullName);
            var room = store.Create(null);
            var desktopChat = Guid.NewGuid();
            room.CodexChatId = desktopChat;
            room.CodexDesktop = true;
            room.CodexChatTitle = "Codex Desktop · API work";
            room.Messages.Add(new DiscussionMessage(DiscussionMember.Boss, DiscussionMember.Claude, "plan", DateTimeOffset.Now, DiscussionMember.Codex));
            store.Save(room);

            var loaded = Assert.Single(store.Load());
            Assert.True(loaded.CodexDesktop);
            Assert.False(loaded.OwnCodexThread);
            Assert.Equal(desktopChat, loaded.CodexChatId);
            Assert.Equal(DiscussionMember.Codex, loaded.Messages[0].ForwardedFrom);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ClaudeCodeBundledWithClaudeDesktopIsFoundNewestFirst()
    {
        var root = Directory.CreateTempSubdirectory("trayvora-claude-data-");
        try
        {
            string Make(string version, string build)
            {
                var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "claude_pkg", "claude-code", version, build));
                var exe = Path.Combine(folder.FullName, "claude.exe");
                File.WriteAllBytes(exe, []);
                return exe;
            }
            var older = Make("2.1.308", "aaa");
            var newer = Make("2.1.325", "bbb");
            Directory.CreateDirectory(Path.Combine(root.FullName, "claude_pkg", "claude-code", "not-a-version", "x"));
            Assert.Equal([newer, older], ClaudeChats.DesktopBundled(root.FullName));
            Assert.Empty(ClaudeChats.DesktopBundled(Path.Combine(root.FullName, "missing")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("Short title", "Short title")]
    [InlineData("Line one\nline two", "Line one line two")]
    public void TitlesComeFromTheFirstMessage(string text, string expected) =>
        Assert.Equal(expected, DiscussionStore.TitleFrom(text));

    [Fact]
    public void LongTitlesAreShortened() =>
        Assert.EndsWith("…", DiscussionStore.TitleFrom(new string('a', 120)));

    [Fact]
    public void DiscussionRoundsAreSavedAndKeptInRange()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-rounds-");
        try
        {
            var settings = new DiscussionSettings(folder.FullName);
            Assert.Equal(5, settings.Rounds);
            settings.SaveRounds(3);
            Assert.Equal(3, new DiscussionSettings(folder.FullName).Rounds);
            settings.SaveRounds(50);
            Assert.Equal(10, settings.Rounds);
            settings.SaveRounds(0);
            Assert.Equal(1, settings.Rounds);
            File.WriteAllText(Path.Combine(folder.FullName, "discussion.json"), "{ broken");
            Assert.Equal(5, settings.Rounds);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
