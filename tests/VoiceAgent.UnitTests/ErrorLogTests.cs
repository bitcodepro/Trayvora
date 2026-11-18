using System.Text.Json;
using VoiceAgent.Codex;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

[Collection("ErrorLog")]
public sealed class ErrorLogTests
{
    [Fact]
    public void ErrorsAreKeptSevenDaysWithWhereAndWhichChat()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-errors-");
        ErrorLog.UseFolder(folder.FullName);
        try
        {
            // An entry from 8 days ago is dropped the next time the log is read.
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog.File)!);
            var old = new ErrorEntry(DateTimeOffset.Now.AddDays(-8), "Old", null, "IOException", "old", "old");
            File.WriteAllText(ErrorLog.File, JsonSerializer.Serialize(old) + "\n{ damaged line\n");

            ErrorLog.Write(new IOException("wrapped", new InvalidDataException("Codex App Server emitted an oversized JSON line.")),
                "Sessions · Codex", "session \"Synthetic\" · Codex Desktop chat \"Test\"");
            ErrorLog.Write(new OperationCanceledException()); // a cancel is not an error
            // Writing already removed the 8-day-old entry; the Error log page was never opened.
            Assert.DoesNotContain("\"Old\"", File.ReadAllText(ErrorLog.File));

            var entries = ErrorLog.Entries();
            var entry = Assert.Single(entries);
            Assert.Equal("Sessions · Codex", entry.Where);
            Assert.Contains("Synthetic", entry.Context);
            Assert.Equal("IOException", entry.Type);
            Assert.Contains("InvalidDataException", entry.Details);
            Assert.Contains("oversized JSON line", ErrorLog.Format(entry));
            Assert.DoesNotContain("Old", File.ReadAllText(ErrorLog.File));

            ErrorLog.Clear();
            Assert.Empty(ErrorLog.Entries());
        }
        finally
        {
            ErrorLog.UseFolder(null);
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ALongChatsHistoryLineIsRead()
    {
        // thread/turns/list for an old Codex Desktop chat returned more than the former 1 MB line limit.
        var line = "{\"x\":\"" + new string('a', 5 * 1024 * 1024) + "\"}";
        using var reader = new StringReader(line + "\n{}\n");
        var lines = new List<string>();
        await foreach (var item in JsonLineReader.ReadAsync(reader, CancellationToken.None)) lines.Add(item);
        Assert.Equal(2, lines.Count);
        Assert.Equal(line.Length, lines[0].Length);
        Assert.True(DesktopChatClient.PollTurns < DesktopChatClient.HistoryTurns);
    }
}
