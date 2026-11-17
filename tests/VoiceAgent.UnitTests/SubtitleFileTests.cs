using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class SubtitleFileTests
{
    [Fact]
    public void StampUsesSrtFormat()
    {
        Assert.Equal("00:01:02,345", SubtitleFile.Stamp(new TimeSpan(0, 0, 1, 2, 345)));
        Assert.Equal("01:00:00,000", SubtitleFile.Stamp(TimeSpan.FromHours(1)));
        Assert.Equal("00:00:00,000", SubtitleFile.Stamp(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ToSrtNumbersCuesSkipsEmptyAndSeparatesWithBlankLines()
    {
        var srt = SubtitleFile.ToSrt(new (TimeSpan, TimeSpan, string)[]
        {
            (TimeSpan.Zero, TimeSpan.FromSeconds(2.5), " Hello. "),
            (TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3), "   "),
            (TimeSpan.FromSeconds(3), new TimeSpan(0, 0, 1, 2, 345), "World"),
        });
        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:02,500\r\nHello.\r\n\r\n" +
            "2\r\n00:00:03,000 --> 00:01:02,345\r\nWorld\r\n\r\n", srt);
    }

    [Fact]
    public void ToSrtOfNothingIsEmpty() => Assert.Equal("", SubtitleFile.ToSrt(Array.Empty<(TimeSpan, TimeSpan, string)>()));

    [Fact]
    public void SpreadSharesPartTimeBySentenceAndKeepsOffset()
    {
        var start = TimeSpan.FromMinutes(2);
        var cues = SubtitleFile.Spread(start, start + TimeSpan.FromSeconds(10), "Aaaa. Bbbb.", maxChars: 5);
        Assert.Equal(2, cues.Count);
        Assert.Equal(start, cues[0].Start);
        Assert.Equal(cues[0].End, cues[1].Start);
        Assert.Equal(start + TimeSpan.FromSeconds(10), cues[1].End);
        Assert.Equal("Bbbb.", cues[1].Text);
        Assert.Single(SubtitleFile.Spread(start, start, "One. Two."));
        Assert.Empty(SubtitleFile.Spread(start, start, "  "));
    }
}
