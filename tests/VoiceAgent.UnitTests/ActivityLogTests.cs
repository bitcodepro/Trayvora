using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class ActivityLogTests
{
    [Fact]
    public void KeepsOnlyTheLastThirtyNewestFirstAndSurvivesRestart()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-activity-");
        try
        {
            var file = Path.Combine(folder.FullName, "logs", "activity.json");
            var log = new ActivityLog(file);
            for (var i = 1; i <= 35; i++) log.Add("Shortcut", $"press {i}");
            Assert.Equal(ActivityLog.Limit, log.Entries.Count);
            Assert.Equal("press 35", log.Entries[0].Text);
            Assert.Equal("press 6", log.Entries[^1].Text);

            var reopened = new ActivityLog(file);
            Assert.Equal(log.Entries.Select(e => e.Text), reopened.Entries.Select(e => e.Text));

            reopened.Clear();
            Assert.Empty(new ActivityLog(file).Entries);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ADamagedFileStartsEmpty()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-activity-");
        try
        {
            var file = Path.Combine(folder.FullName, "activity.json");
            File.WriteAllText(file, "{ not json");
            Assert.Empty(new ActivityLog(file).Entries);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
