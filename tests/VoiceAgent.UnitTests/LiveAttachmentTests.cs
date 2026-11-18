using System.IO.Compression;
using System.Text;
using VoiceAgent.Codex;
using VoiceAgent.Desktop;
using Xunit;
using Xunit.Abstractions;

namespace VoiceAgent.UnitTests;

/// <summary>
/// Sends synthetic long text and a picture to the local Codex (Trayvora profile) and Claude Code the way the
/// Sessions window does. Opt-in: TRAYVORA_LIVE_AGENTS=1 plus TRAYVORA_LIVE_TEST_PROFILE and VOICEAGENT_CODEX_EXE
/// for Codex; Claude uses the default Claude Code profile. Each test works in its own temporary folders.
/// </summary>
public sealed class LiveAttachmentTests(ITestOutputHelper log)
{
    private const string Marker = "KIWI-7342-ZEBRA";
    private static bool Enabled => Environment.GetEnvironmentVariable("TRAYVORA_LIVE_AGENTS") == "1";
    private static bool CodexReady => Enabled && Environment.GetEnvironmentVariable("TRAYVORA_LIVE_TEST_PROFILE") is { Length: > 0 }
        && Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE") is { Length: > 0 };

    /// <summary>About 60,000 characters of filler with the marker only at the very end.</summary>
    private static string LongText()
    {
        var text = new StringBuilder();
        for (var i = 0; text.Length < 60_000; i++) text.Append($"Line {i}: synthetic filler about nothing in particular.\n");
        return text.Append($"The secret word at the end of this text is {Marker}.\n").ToString();
    }

    private async Task<string> CodexAsync(string prompt, IReadOnlyList<string>? attachments = null)
    {
        var home = Environment.GetEnvironmentVariable("TRAYVORA_LIVE_TEST_PROFILE")!;
        var executable = Environment.GetEnvironmentVariable("VOICEAGENT_CODEX_EXE")!;
        var workspace = Directory.CreateTempSubdirectory("trayvora-live-attach-");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var reply = new StringBuilder();
            var steps = new List<string>();
            await using var connection = AppServerConnection.Start(executable, workspace.FullName, home, CodexSession.DisabledFeatures);
            var status = await CodexSession.RunTurnAsync(connection, workspace.FullName, Path.Combine(workspace.FullName, "thread.txt"),
                prompt, new ReplyStream.Reporter(value => reply.Append(value)), deadline.Token,
                steps: new ReplyStream.Reporter(steps.Add), attachments: attachments);
            log.WriteLine($"Codex status: {status}; steps: {string.Join(" | ", steps)}");
            log.WriteLine("Codex reply: " + reply);
            Assert.Equal(CodexTurnStatus.Completed, status);
            return reply.ToString();
        }
        finally
        {
            try { workspace.Delete(recursive: true); } catch (IOException) { }
        }
    }

    private async Task<string> ClaudeAsync(string prompt, string? readable)
    {
        var workspace = Directory.CreateTempSubdirectory("trayvora-live-claude-");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var stream = new ReplyStream();
            var chat = new ClaudeChat(Guid.NewGuid(), workspace.FullName, "New Claude chat · Trayvora attachment test");
            string reply;
            try
            {
                reply = await ClaudeChats.SendAsync("", ClaudeChats.DefaultProfile(), chat, prompt, deadline.Token, stream, readableFolder: readable);
            }
            catch (IOException error)
            {
                reply = "ERROR: " + error.Message;
            }
            log.WriteLine($"Claude steps: {string.Join(" | ", stream.Steps.Recent)}; notes: {stream.Notes}");
            log.WriteLine("Claude reply: " + reply);
            return reply;
        }
        finally
        {
            try { workspace.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task CodexGetsALongMessageWholeAsAttachedText()
    {
        if (!CodexReady) return;
        var folder = Directory.CreateTempSubdirectory("trayvora-live-files-");
        try
        {
            var file = Path.Combine(folder.FullName, "long.txt");
            await File.WriteAllTextAsync(file, LongText());
            var reply = await CodexAsync("What is the secret word at the end of the attached file? Reply with the word only.", [file]);
            Assert.Contains(Marker, reply);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task CodexCannotOpenAFileByItsPathAlone()
    {
        if (!CodexReady) return;
        var folder = Directory.CreateTempSubdirectory("trayvora-live-files-");
        try
        {
            var envelope = DiscussionWindow.ForAgent(LongText(), folder.FullName, "Claude-to-Codex");
            var reply = await CodexAsync(envelope + "\n\nWhat is the secret word at the end of the full text? Reply with the word only, or NOFILE if you cannot read the file.");
            log.WriteLine(reply.Contains(Marker) ? "Codex read the file by path." : "Codex could not read the file by path.");
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task CodexReadsALongMessageFileTheWayTheSessionsWindowSendsIt()
    {
        if (!CodexReady) return;
        var folder = Directory.CreateTempSubdirectory("trayvora-live-files-");
        try
        {
            var envelope = DiscussionWindow.ForAgent(LongText(), folder.FullName, "Claude-to-Codex") + "\n\nWhat is the secret word at the end of the full text? Reply with the word only.";
            var reply = await CodexAsync(envelope, CodexSession.FilesIn(envelope, folder.FullName));
            Assert.Contains(Marker, reply);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void FilesNamedInAMessageAreFoundOnlyInsideTheSessionFolder()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-files-");
        var other = Directory.CreateTempSubdirectory("trayvora-other-");
        try
        {
            var image = Path.Combine(folder.FullName, "shot.png");
            File.WriteAllBytes(image, SolidPng(2, 2, 0, 0, 0));
            var outside = Path.Combine(other.FullName, "secret.txt");
            File.WriteAllText(outside, "x");
            var envelope = DiscussionWindow.ForAgent(new string('a', DiscussionWindow.MaxLength + 1), folder.FullName, "x");
            var text = ComposerAttachments.WithFiles(envelope, [image, outside, Path.Combine(folder.FullName, "gone.txt")]);
            var found = CodexSession.FilesIn(text, folder.FullName);
            Assert.Equal(2, found.Count);
            Assert.EndsWith(".txt", found[0]);
            Assert.Equal(image, found[1]);
            Assert.Equal(2, CodexSession.AttachmentInput([image]).Count());
        }
        finally { folder.Delete(recursive: true); other.Delete(recursive: true); }
    }

    [Fact]
    public async Task CodexSeesAnAttachedPicture()
    {
        if (!CodexReady) return;
        var folder = Directory.CreateTempSubdirectory("trayvora-live-files-");
        try
        {
            var image = Path.Combine(folder.FullName, "colour.png");
            await File.WriteAllBytesAsync(image, SolidPng(64, 64, 220, 20, 20));
            var reply = await CodexAsync("What single colour fills the attached image? Reply with one English word.", [image]);
            Assert.Contains("red", reply, StringComparison.OrdinalIgnoreCase);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task ClaudeReadsALongMessageFileAndAPictureFromTheAttachmentsFolder()
    {
        if (!Enabled) return;
        var folder = Directory.CreateTempSubdirectory("trayvora-live-files-");
        try
        {
            var envelope = DiscussionWindow.ForAgent(LongText(), folder.FullName, "Codex-to-Claude");
            var text = await ClaudeAsync(envelope + "\n\nWhat is the secret word at the end of the full text? Reply with the word only.", folder.FullName);
            Assert.Contains(Marker, text);

            var image = Path.Combine(folder.FullName, "colour.png");
            await File.WriteAllBytesAsync(image, SolidPng(64, 64, 20, 40, 220));
            var picture = await ClaudeAsync(ComposerAttachments.WithFiles("What single colour fills the attached image? Reply with one English word.", [image]), folder.FullName);
            Assert.Contains("blue", picture, StringComparison.OrdinalIgnoreCase);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task ClaudeWithoutTheExtraFolder()
    {
        if (!Enabled) return;
        var folder = Directory.CreateTempSubdirectory("trayvora-live-files-");
        try
        {
            var envelope = DiscussionWindow.ForAgent(LongText(), folder.FullName, "Codex-to-Claude");
            var text = await ClaudeAsync(envelope + "\n\nWhat is the secret word at the end of the full text? Reply with the word only, or NOFILE if you cannot read the file.", null);
            log.WriteLine(text.Contains(Marker) ? "Claude read the file without --add-dir." : "Claude could not read the file without --add-dir.");
        }
        finally { folder.Delete(recursive: true); }
    }

    /// <summary>A PNG filled with one colour, built by hand so the test needs no imaging library.</summary>
    internal static byte[] SolidPng(int width, int height, byte red, byte green, byte blue)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        Chunk(output, "IHDR", header);
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = y * (1 + width * 3) + 1 + x * 3;
                raw[at] = red; raw[at + 1] = green; raw[at + 2] = blue;
            }
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
        Chunk(output, "IDAT", packed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        output.Write(length);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(body);
        var crc = new byte[4];
        WriteInt(crc, 0, (int)Crc32(body));
        output.Write(crc);
    }

    private static void WriteInt(byte[] buffer, int at, int value)
    {
        buffer[at] = (byte)(value >> 24); buffer[at + 1] = (byte)(value >> 16); buffer[at + 2] = (byte)(value >> 8); buffer[at + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
