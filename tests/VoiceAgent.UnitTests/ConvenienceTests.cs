using VoiceAgent.Application;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class ConvenienceTests
{
    [Fact]
    public void RecordingLastsUpToTwoMinutes()
    {
        Assert.Equal(120, VoiceCapture.MaxSeconds);
        Assert.Equal(16000 * 2 * 120, VoiceCapture.Capacity);
    }

    [Fact]
    public void TemplatesAreSavedOnceNewestFirstAndCanBeRemoved()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-templates-");
        try
        {
            var templates = new PromptTemplates(Path.Combine(folder.FullName, "templates.json"));
            Assert.Empty(templates.Load());
            Assert.True(templates.Add("Review the diff"));
            Assert.True(templates.Add("Discuss with Claude"));
            Assert.True(templates.Add("  Review the diff "));
            Assert.False(templates.Add("   "));
            Assert.Equal(["Review the diff", "Discuss with Claude"], templates.Load());
            templates.Remove("Review the diff");
            Assert.Equal(["Discuss with Claude"], new PromptTemplates(Path.Combine(folder.FullName, "templates.json")).Load());
            File.WriteAllText(Path.Combine(folder.FullName, "templates.json"), "{ broken");
            Assert.Empty(templates.Load());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void SessionSearchLooksAtTitlesAndMessages()
    {
        var room = new DiscussionRoom { Title = "Release plan" };
        room.Messages.Add(new DiscussionMessage(DiscussionMember.Codex, DiscussionMember.Boss, "The migration script is ready", DateTimeOffset.Now));
        Assert.True(DiscussionWindow.RoomMatches(room, ""));
        Assert.True(DiscussionWindow.RoomMatches(room, "release"));
        Assert.True(DiscussionWindow.RoomMatches(room, "MIGRATION"));
        Assert.False(DiscussionWindow.RoomMatches(room, "invoice"));
    }

    [Fact]
    public void SessionsExportAsMarkdown()
    {
        var room = new DiscussionRoom { Title = "Release plan", CodexChatTitle = "Chat 2" };
        room.Messages.Add(new DiscussionMessage(DiscussionMember.Boss, DiscussionMember.Codex, "Check the build", DateTimeOffset.Now));
        room.Messages.Add(new DiscussionMessage(DiscussionMember.Codex, DiscussionMember.Boss, "**Green**", DateTimeOffset.Now));
        room.Messages.Add(new DiscussionMessage(DiscussionMember.Claude, null, "signed out", DateTimeOffset.Now, Failed: true));
        var markdown = DiscussionWindow.ToMarkdown(room);
        Assert.StartsWith("# Release plan", markdown);
        Assert.Contains("- Codex chat: Chat 2", markdown);
        Assert.Contains("## Boss → Codex", markdown);
        Assert.Contains("**Green**", markdown);
        Assert.Contains("Claude (did not answer)", markdown);
    }
}

public sealed class UiLanguageTests
{
    [Fact]
    public void UkrainianDictionaryIsEmbeddedAndCoversMainLabels()
    {
        var map = UiLanguage.LoadMap("uk");
        Assert.True(map.Count > 300);
        Assert.True(map.ContainsKey("Record with preview"));
        Assert.True(map.ContainsKey("Get started"));
        Assert.Empty(UiLanguage.LoadMap("xx"));
    }
}

public sealed class ChatFolderTests
{
    [Fact]
    public void NewChatsGoWhereCodexDesktopPutsItsChats()
    {
        var root = Directory.CreateTempSubdirectory("trayvora-chatfolders-");
        try
        {
            var sessions = Directory.CreateDirectory(Path.Combine(root.FullName, "sessions", "2026", "11")).FullName;
            File.WriteAllText(Path.Combine(sessions, "a.jsonl"), "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"D:\\\\CodexData\\\\workspaces\\\\2026-11-14\\\\new-chat-5\"}}\n");
            Assert.Equal(@"D:\CodexData\workspaces", ChatFolders.FromSessions(Path.Combine(root.FullName, "sessions")));
            Assert.Null(ChatFolders.FromSessions(Path.Combine(root.FullName, "missing")));

            var workspaces = Path.Combine(root.FullName, "workspaces");
            var day = new DateTime(2026, 11, 16);
            Assert.Equal(Path.Combine(workspaces, "2026-11-16", "new-chat"), ChatFolders.CreateNew(workspaces, day));
            Assert.Equal(Path.Combine(workspaces, "2026-11-16", "new-chat-2"), ChatFolders.CreateNew(workspaces, day));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}

public sealed class DesktopRegistrationTests
{
    [Fact]
    public void CodexChatIsAddedToProjectlessListKeepingOtherState()
    {
        var root = Directory.CreateTempSubdirectory("trayvora-codexstate-");
        try
        {
            var file = Path.Combine(root.FullName, ".codex-global-state.json");
            File.WriteAllText(file, "{\"projectless-thread-ids\":[\"a\"],\"pinned-thread-ids\":[\"p\"],\"x\":{\"y\":1}}");
            Assert.True(CodexDesktopChats.AddToState(file, ["b", "a"]));
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            Assert.Equal(["a", "b"], json["projectless-thread-ids"]!.AsArray().Select(item => item!.GetValue<string>()));
            Assert.Equal(1, json["x"]!["y"]!.GetValue<int>());
            Assert.True(File.Exists(file + ".trayvora-backup"));
            Assert.False(CodexDesktopChats.AddToState(Path.Combine(root.FullName, "missing.json"), ["c"]));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ClaudeDesktopRecordIsCopiedFromAnExistingSession()
    {
        var root = Directory.CreateTempSubdirectory("trayvora-claudedesktop-");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "Claude-Data", "claude_x", "claude-code-sessions", "acc", "org")).FullName;
            File.WriteAllText(Path.Combine(folder, "local_old.json"),
                "{\"sessionId\":\"local_old\",\"cliSessionId\":\"" + Guid.NewGuid() + "\",\"cwd\":\"C:\\\\old\",\"model\":\"m\",\"postTurnSummary\":{},\"title\":\"Old\"}");
            var chat = new ClaudeChat(Guid.NewGuid(), @"D:\CodexData\workspaces\2026-11-16\new-chat", "New Claude chat · new-chat");
            Assert.True(ClaudeDesktopSessions.Register(chat, root.FullName));
            Assert.True(ClaudeDesktopSessions.Register(chat, root.FullName));
            var files = Directory.GetFiles(folder, "local_*.json");
            Assert.Equal(2, files.Length);
            var created = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(files.Single(file => !file.EndsWith("local_old.json"))))!;
            Assert.Equal(chat.Id.ToString(), created["cliSessionId"]!.GetValue<string>());
            Assert.Equal(chat.Workspace, created["cwd"]!.GetValue<string>());
            Assert.Equal("m", created["model"]!.GetValue<string>());
            Assert.Null(created["postTurnSummary"]);
            Assert.Equal("Trayvora · new-chat", created["title"]!.GetValue<string>());
            Assert.EndsWith(created["sessionId"]!.GetValue<string>() + ".json", files.Single(file => !file.EndsWith("local_old.json")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}

public sealed class ReplyQuoteTests
{
    [Fact]
    public void RepliesQuoteEveryLineWithTheAuthor()
    {
        var message = new DiscussionMessage(DiscussionMember.Codex, DiscussionMember.Boss, "Plan:\r\n1. Build\n", DateTimeOffset.Now);
        Assert.Equal("> Codex wrote:\n> Plan:\n> 1. Build\n\n", DiscussionWindow.Quote(message));
    }
}
