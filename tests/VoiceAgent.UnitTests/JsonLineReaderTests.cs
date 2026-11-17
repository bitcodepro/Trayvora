using VoiceAgent.Codex;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class JsonLineReaderTests
{
    [Fact]
    public async Task ReadsSplitUtf16ChunksAndPreservesJsonEscapes()
    {
        var first = "{\"text\":\"" + new string('я', 9000) + "\\n\"}";
        using var input = new StringReader(first + "\r\n{\"id\":1}\n");
        var lines = new List<string>();
        await foreach (var line in JsonLineReader.ReadAsync(input, default))
        {
            lines.Add(line);
        }

        Assert.Equal(new[] { first, "{\"id\":1}" }, lines);
    }

    [Fact]
    public async Task RejectsTruncatedLine()
    {
        using var input = new StringReader("{\"id\":");
        await using var lines = JsonLineReader.ReadAsync(input, default).GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidDataException>(() => lines.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task RejectsOversizedLine()
    {
        using var input = new StringReader(new string('x', (1024 * 1024) + 1));
        await using var lines = JsonLineReader.ReadAsync(input, default).GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidDataException>(() => lines.MoveNextAsync().AsTask());
    }
}
