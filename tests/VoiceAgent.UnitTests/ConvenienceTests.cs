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

public sealed class TargetChipTests
{
    [Fact]
    public void SwitchingRecipientClearsTheOtherAgentsRelayToggle()
    {
        Assert.Equal((true, false), DiscussionWindow.RelayAfterSwitch(DiscussionWindow.DiscussionTarget.Codex, true, true));
        Assert.Equal((false, true), DiscussionWindow.RelayAfterSwitch(DiscussionWindow.DiscussionTarget.Claude, true, true));
        Assert.Equal((false, false), DiscussionWindow.RelayAfterSwitch(DiscussionWindow.DiscussionTarget.Both, true, true));
        Assert.Equal((false, false), DiscussionWindow.RelayAfterSwitch(DiscussionWindow.DiscussionTarget.Codex, false, true));
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RecognitionChoiceTests
{
    [Fact]
    public void EachPlaceUsesItsOwnRecognizerOrTheDefault()
    {
        var whisperDefault = new SpeechOptions(Cloud: false, ProtectedKey: "k");
        Assert.False(SpeechSettings.Resolve(whisperDefault, RecognitionPlace.Main));
        Assert.False(SpeechSettings.Resolve(whisperDefault, RecognitionPlace.Sessions));
        Assert.True(SpeechSettings.Resolve(whisperDefault with { QuestionCloud = true }, RecognitionPlace.Questions));
        Assert.False(SpeechSettings.Resolve(whisperDefault with { Cloud = true, SessionCloud = false }, RecognitionPlace.Sessions));
        // Computer sound goes to OpenAI by default whenever a key is saved.
        Assert.True(SpeechSettings.Resolve(whisperDefault, RecognitionPlace.ComputerSound));
        Assert.False(SpeechSettings.Resolve(new SpeechOptions(), RecognitionPlace.ComputerSound));
        Assert.False(SpeechSettings.Resolve(whisperDefault with { ComputerSoundCloud = false }, RecognitionPlace.ComputerSound));
    }

    [Fact]
    public void SavedKeyIsShownOnlyAsAShortHint()
    {
        Assert.Equal("sk-…WXYZ", SpeechSettings.Hint("sk-proj-1234567890WXYZ"));
        Assert.Equal("saved", SpeechSettings.Hint("short"));
        Assert.Equal([null, false, true], new[] { 0, 1, 2 }.Select(Recognizers.Value));
        Assert.Equal(2, Recognizers.Index(true));
    }

    [Fact]
    public void ChosenOpenAiRecognizerIsReadyEvenWhenTheDefaultWhisperModelIsMissing()
    {
        var options = new SpeechOptions(Cloud: false, ProtectedKey: "k", QuestionCloud: true);
        Assert.True(SpeechSettings.ReadyFor(options, RecognitionPlace.Questions, modelReady: false));
        Assert.False(SpeechSettings.ReadyFor(options, RecognitionPlace.Main, modelReady: false));
        Assert.False(SpeechSettings.ReadyFor(options with { ProtectedKey = null }, RecognitionPlace.Questions, modelReady: true));
        Assert.True(SpeechSettings.ReadyFor(options with { QuestionCloud = false }, RecognitionPlace.Questions, modelReady: true));
    }

    [Fact]
    public void FreshInstallSelectingOpenAiThenSavingTheKeyTurnsOpenAiOn()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-speech-");
        try
        {
            var speech = new SpeechSettings(folder.FullName, folder.FullName);
            Assert.Throws<InvalidOperationException>(() => speech.Save(true, 0, 0, ""));
            Assert.False(speech.Current.Cloud);
            // The user also picked a non-default OpenAI model before the key existed.
            Assert.Throws<InvalidOperationException>(() => speech.Save(true, 0, 1, ""));
            speech.Save(true, 0, 1, "sk-test-0000000000ABCD", "");
            Assert.True(speech.Current.Cloud);
            Assert.Equal(1, speech.Current.Remote);
            Assert.Equal(1, new SpeechSettings(folder.FullName, folder.FullName).Current.Remote);
            Assert.True(speech.CloudFor(RecognitionPlace.Main));
            Assert.True(SpeechSettings.ReadyFor(speech.Current, RecognitionPlace.Main, modelReady: false));
            Assert.Equal("sk-…ABCD", speech.Current.KeyHint);
            Assert.True(new SpeechSettings(folder.FullName, folder.FullName).Current.Cloud);
            speech.SaveKey("sk-test-1111111111WXYZ");
            Assert.True(speech.Current.Cloud);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ComputerSoundQuestionsNeverUseLongMemory()
    {
        Assert.False(VoiceOverlay.UsesLongMemory(false, longModeSelected: true));
        Assert.True(VoiceOverlay.UsesLongMemory(null, longModeSelected: true));
        Assert.False(VoiceOverlay.UsesLongMemory(null, longModeSelected: false));
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ComputerSoundTests
{
    [Fact]
    public void TheOutputDeviceThatCarriedTheCallIsChosen()
    {
        static byte[] Pcm(params short[] samples) => samples.SelectMany(BitConverter.GetBytes).ToArray();
        var speakers = Pcm(0, 0, 0, 0);
        var headset = Pcm(1200, -900, 1500, -1100);
        Assert.Equal(1, VoiceAgent.Speech.WasapiRecorder.Loudest([speakers, headset], [8, 8]));
        Assert.Equal(0, VoiceAgent.Speech.WasapiRecorder.Loudest([headset, speakers], [8, 8]));
        // Only the recorded part counts.
        Assert.Equal(1, VoiceAgent.Speech.WasapiRecorder.Loudest([headset, Pcm(10, 10, 0, 0)], [0, 4]));
    }

    private static readonly short[] Speech = [1200, -900, 1500, -1100];

    private static Task<int> Fill(byte[] buffer, short[] samples)
    {
        var bytes = samples.SelectMany(BitConverter.GetBytes).ToArray();
        bytes.CopyTo(buffer, 0);
        return Task.FromResult(bytes.Length);
    }

    [Fact]
    public async Task AFailedDeviceNeverWinsOverASilentWorkingOne()
    {
        var pcm = new byte[64];
        var count = await VoiceAgent.Speech.WasapiRecorder.RecordLoudestAsync(2, (index, buffer) => index == 0
            ? Task.FromException<int>(new InvalidOperationException("device lost"))
            : Task.FromResult(0), pcm, CancellationToken.None);
        Assert.Equal(0, count);

        count = await VoiceAgent.Speech.WasapiRecorder.RecordLoudestAsync(3, (index, buffer) => index switch
        {
            0 => Task.FromException<int>(new InvalidOperationException("device lost")),
            1 => Fill(buffer, [0, 0, 0, 0]),
            _ => Fill(buffer, Speech),
        }, pcm, CancellationToken.None);
        Assert.Equal(8, count);
        Assert.Equal(1200, BitConverter.ToInt16(pcm, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(() => VoiceAgent.Speech.WasapiRecorder.RecordLoudestAsync(2,
            (_, _) => Task.FromException<int>(new InvalidOperationException("no device")), pcm, CancellationToken.None));
    }

    [Fact]
    public async Task ExtraBuffersAreClearedOnSuccessCancellationAndFailure()
    {
        var seen = new List<byte[]>();
        Task<int> Record(int index, byte[] buffer)
        {
            seen.Add(buffer);
            return Fill(buffer, Speech);
        }
        await VoiceAgent.Speech.WasapiRecorder.RecordLoudestAsync(2, Record, new byte[64], CancellationToken.None);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VoiceAgent.Speech.WasapiRecorder.RecordLoudestAsync(2, Record, new byte[64], cancelled.Token));

        await Assert.ThrowsAsync<InvalidOperationException>(() => VoiceAgent.Speech.WasapiRecorder.RecordLoudestAsync(2, (index, buffer) =>
        {
            seen.Add(buffer);
            Fill(buffer, Speech);
            return Task.FromException<int>(new InvalidOperationException("failed after capturing"));
        }, new byte[64], CancellationToken.None));

        Assert.Equal(6, seen.Count);
        Assert.All(seen, buffer => Assert.All(buffer, value => Assert.Equal(0, value)));
    }
}