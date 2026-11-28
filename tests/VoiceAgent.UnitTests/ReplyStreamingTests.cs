using System.Text.Json;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class ReplyStreamingTests
{
    private static string Line(object value) => JsonSerializer.Serialize(value);

    private static object Delta(string text, string? parent = null) => new
    {
        type = "stream_event", parent_tool_use_id = parent,
        @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } },
    };

    [Fact]
    public void NewClaudeChatsStartUnderTheirOwnIdAndSessionSettingsSurviveReload()
    {
        var profile = Directory.CreateTempSubdirectory("trayvora-claude-profile-");
        try
        {
            var id = Guid.NewGuid();
            Assert.False(ClaudeChats.Exists(profile.FullName, id));
            var project = Directory.CreateDirectory(Path.Combine(profile.FullName, "projects", "E--work"));
            File.WriteAllText(Path.Combine(project.FullName, $"{id}.jsonl"), "");
            Assert.True(ClaudeChats.Exists(profile.FullName, id));

            var store = new DiscussionStore(Path.Combine(profile.FullName, "rooms"));
            var room = store.Create(null);
            room.ClaudePermissions = ClaudePermissions.AllowEdits;
            room.NewCodexFolder = profile.FullName;
            store.Save(room);
            var loaded = Assert.Single(store.Load());
            Assert.Equal(ClaudePermissions.AllowEdits, loaded.ClaudePermissions);
            Assert.Equal(profile.FullName, loaded.NewCodexFolder);
        }
        finally { profile.Delete(recursive: true); }
    }

    [Fact]
    public void ShortcutsAcceptWinDigitsAndFunctionKeys()
    {
        Assert.True(new HotkeyShortcut(HotkeyModifiers.Win | HotkeyModifiers.Shift, 'K').IsValid);
        Assert.True(new HotkeyShortcut(0, 0x71).IsValid); // F2 alone
        Assert.True(new HotkeyShortcut(HotkeyModifiers.Ctrl, '7').IsValid);
        Assert.False(new HotkeyShortcut(HotkeyModifiers.Shift, 'K').IsValid);
        Assert.False(new HotkeyShortcut(0, 'K').IsValid);
        Assert.Equal("Win+Ctrl+F12", new HotkeyShortcut(HotkeyModifiers.Win | HotkeyModifiers.Ctrl, 0x7B).ToString());
        Assert.Equal(0x7Bu, HotkeyShortcut.VirtualKey(Avalonia.Input.Key.F12));
        Assert.Equal((uint)'5', HotkeyShortcut.VirtualKey(Avalonia.Input.Key.D5));
        Assert.Null(HotkeyShortcut.VirtualKey(Avalonia.Input.Key.LeftCtrl));
        Assert.Equal("default", ClaudeChats.Mode(ClaudePermissions.Ask));
        Assert.Equal("acceptEdits", ClaudeChats.Mode(ClaudePermissions.AllowEdits));
        Assert.Equal("bypassPermissions", ClaudeChats.Mode(ClaudePermissions.AllowAll));
    }

    [Fact]
    public void RenamedChatsUseTheirLatestNameAndSearchMatchesWords()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("trayvora-title-").FullName, "chat.jsonl");
        try
        {
            File.WriteAllLines(file,
            [
                "{\"type\":\"user\",\"message\":{\"content\":\"hello\"}}",
                "{\"type\":\"custom-title\",\"customTitle\":\"Old name\",\"sessionId\":\"x\"}",
                new string('x', 1_200_000).Insert(0, "{\"type\":\"pad\",\"v\":\"") + "\"}",
                "{\"type\":\"custom-title\",\"customTitle\":\"Тестовый чат\",\"sessionId\":\"x\"}",
            ]);
            Assert.Equal("Тестовый чат", ClaudeChats.CustomTitle(file));
            File.WriteAllLines(file, ["{\"type\":\"custom-title\",\"customTitle\":\"Only\"}", new string('y', 1_200_000)]);
            Assert.Equal("Only", ClaudeChats.CustomTitle(file));
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }

        Assert.True(DiscussionWindow.Matches("Тестовый чат для Claude", "чат тест"));
        Assert.True(DiscussionWindow.Matches("anything", "  "));
        Assert.False(DiscussionWindow.Matches("Тестовый чат", "релиз"));
    }

    [Fact]
    public void ClaudeTextArrivesAsItIsWrittenAndTheResultIsTheAnswer()
    {
        var stream = new ReplyStream();
        var parser = new ClaudeStreamParser(stream);
        parser.Accept(Line(new { type = "system", subtype = "init" }));
        parser.Accept("""{"type":"assistant","error":"authentication_failed","message":{"content":[{"type":"text","text":"Sign in again."}]}}""");
        Assert.False(stream.Receipt.IsCompleted);
        parser.Accept(Line(new { type = "stream_event", @event = new { type = "message_start" } }));
        Assert.True(stream.Receipt.IsCompletedSuccessfully);
        parser.Accept(Line(Delta("**OK")));
        Assert.Equal("**OK", stream.Text);
        parser.Accept(Line(Delta("** done")));
        parser.Accept(Line(Delta("ignored subagent text", parent: "toolu_1")));
        parser.Accept("not json");
        Assert.Null(parser.Result);
        parser.Accept(Line(new { type = "result", subtype = "success", is_error = false, result = "**OK** done", permission_denials = Array.Empty<object>() }));

        Assert.Equal("**OK** done", stream.Text);
        Assert.Equal("**OK** done", parser.Result);
        Assert.False(parser.Failed);
        Assert.Null(stream.Notes);
        Assert.Contains("not json", parser.Other);
    }

    [Fact]
    public void ToolUseIsShownAndANewMessageReplacesTheNarration()
    {
        var stream = new ReplyStream();
        var parser = new ClaudeStreamParser(stream);
        parser.Accept(Line(Delta("Let me look at the file.")));
        parser.Accept(Line(new { type = "assistant", message = new { id = "m1", content = new object[]
        {
            new { type = "tool_use", name = "Read", input = new { file_path = @"E:\repo\src\Client.cs" } },
        } } }));
        Assert.Equal("Read · Client.cs", stream.Snapshot().Activity);
        parser.Accept(Line(new { type = "stream_event", @event = new { type = "message_start" } }));
        Assert.Equal("", stream.Text);
        Assert.Null(stream.Snapshot().Activity);
        parser.Accept(Line(Delta("Client has three fields.")));
        Assert.Equal("Client has three fields.", stream.Text);
    }

    [Fact]
    public void ClaudeWorkLogTracksToolsWithoutBufferingCommandsOrThinkingContent()
    {
        var stream = new ReplyStream();
        var parser = new ClaudeStreamParser(stream);
        parser.Accept("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"thinking","thinking":"private thinking"}}}""");
        Assert.Equal("Thinking", stream.Snapshot().Activity);
        Assert.Null(stream.WorkLog);

        parser.Accept("""{"type":"assistant","parent_tool_use_id":"outer","message":{"content":[{"type":"text","text":"subagent answer"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"some-command with-secret","description":"another-secret"}}]}}""");
        parser.Accept("""{"type":"tool_progress","tool_use_id":"toolu_1","tool_name":"Bash","elapsed_time_seconds":12}""");
        Assert.Equal("Running · Bash · 12 s", stream.Snapshot().Activity);
        parser.Accept("""{"type":"tool_progress","tool_use_id":"toolu_1","tool_name":"Bash","elapsed_time_seconds":15}""");
        Assert.Contains("Bash · running", stream.WorkLog);
        Assert.Single(stream.WorkLog!.Split('\n'));
        parser.Accept("""{"type":"user","parent_tool_use_id":"outer","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","is_error":true,"content":"secret result"}]}}""");
        Assert.Equal("Waiting for Claude", stream.Snapshot().Activity);
        parser.Accept("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Read","input":{"file_path":"E:/working/Example.cs"}}]}}""");
        parser.Accept("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2","content":"file contents"}]}}""");
        Assert.Contains("Bash · failed", stream.WorkLog);
        Assert.Contains("Read · Example.cs · completed", stream.WorkLog);
        Assert.DoesNotContain("secret", stream.WorkLog);
        Assert.DoesNotContain("thinking", stream.WorkLog);
        Assert.Equal("", stream.Text);
    }

    [Fact]
    public void ClaudeWithoutPartialEventsStillStreamsWholeBlocks()
    {
        var stream = new ReplyStream();
        var parser = new ClaudeStreamParser(stream);
        parser.Accept(Line(new { type = "assistant", message = new { id = "m1", content = new object[] { new { type = "text", text = "First block." } } } }));
        Assert.Equal("First block.", stream.Text);
        parser.Accept(Line(new { type = "assistant", message = new { id = "m2", content = new object[] { new { type = "text", text = "Answer." } } } }));
        Assert.Equal("Answer.", stream.Text);
    }

    [Fact]
    public void DeniedToolsAndErrorsAreReported()
    {
        var stream = new ReplyStream();
        var parser = new ClaudeStreamParser(stream);
        parser.Accept(Line(new
        {
            type = "result", is_error = true, result = "Not logged in",
            permission_denials = new object[] { new { tool_name = "Bash" }, new { tool_name = "Bash" }, new { tool_name = "Edit" }, new { tool_name = "bad name!" } },
        }));
        Assert.True(parser.Failed);
        Assert.Equal("Not logged in", parser.Result);
        Assert.Equal("Claude Code was not allowed to use: Bash, Edit. Allow it in Claude Code for that chat if the task needs it.", stream.Notes);
    }

    [Theory]
    [InlineData("**Claude:** review the plan", "Claude", "review the plan")]
    [InlineData("**Claude**: review the plan", "Claude", "review the plan")]
    [InlineData("@codex: please answer", "Codex", "please answer")]
    [InlineData("  Boss: done", "Boss", "done")]
    [InlineData("**Claude:**", null, "**Claude:**")]
    [InlineData("Claudette: hi", null, "Claudette: hi")]
    public void HandOffAcceptsMarkdownAndMentions(string text, string? expected, string body)
    {
        Assert.Equal(expected, DiscussionWindow.Recipient(text)?.ToString());
        Assert.Equal(body, DiscussionWindow.StripAddress(text));
    }

    [Fact]
    public void LongMessagesReachTheOtherAgentWholeAsAFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "trayvora-attach-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("short", DiscussionWindow.ForAgent("short", folder, "Claude-to-Codex"));
            Assert.False(Directory.Exists(folder));
            var text = "Начало. " + new string('x', DiscussionWindow.MaxLength * 3) + " конец.";
            var sent = DiscussionWindow.ForAgent(text, folder, "Claude-to-Codex");
            var file = Assert.Single(Directory.GetFiles(folder, "*.txt"));
            Assert.Equal(text, File.ReadAllText(file));
            Assert.Contains(file, sent);
            Assert.StartsWith("[Trayvora: this message is long", sent);
            Assert.Contains("Начало.", sent);
            // Room for Trayvora's routing instructions under Codex's 16,000-character turn limit.
            Assert.True(sent.Length < DiscussionWindow.PreviewLength + 700);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ClaudeChatTitlesSkipCommandsAndReminders()
    {
        static string? Title(string json)
        {
            using var document = JsonDocument.Parse(json);
            return ClaudeChats.UserText(document.RootElement);
        }

        Assert.Equal("Fix the build", Title("\"  Fix the build \""));
        Assert.Null(Title("\"<command-name>/model</command-name>\""));
        Assert.Null(Title("\"Caveat: the messages below\""));
        Assert.Equal("Second", Title("[{\"type\":\"text\",\"text\":\"<system-reminder>x</system-reminder>\"},{\"type\":\"text\",\"text\":\"Second\"}]"));
        Assert.Null(Title("[{\"type\":\"tool_result\",\"content\":\"x\"}]"));
    }

    [Fact]
    public void ChatsAreListedNewestFirst()
    {
        var profile = Directory.CreateTempSubdirectory("trayvora-claude-");
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(profile.FullName, "projects", "p"));
            var workspace = profile.FullName.Replace("\\", "\\\\");
            var older = Path.Combine(project.FullName, $"{Guid.NewGuid()}.jsonl");
            var newer = Path.Combine(project.FullName, $"{Guid.NewGuid()}.jsonl");
            File.WriteAllText(older, $"{{\"type\":\"user\",\"cwd\":\"{workspace}\",\"message\":{{\"content\":\"Older chat\"}}}}\n");
            File.WriteAllText(newer, $"{{\"type\":\"user\",\"cwd\":\"{workspace}\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"Newer chat\"}}]}}}}\n");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
            Assert.Equal(["Newer chat", "Older chat"], ClaudeChats.Find(profile.FullName).Select(chat => chat.Title));
        }
        finally
        {
            profile.Delete(recursive: true);
        }
    }
}
