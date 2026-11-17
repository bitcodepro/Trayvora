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
    public void KeepsThirtyPressesWithAllTheirSteps()
    {
        var log = new ActivityLog(null);
        for (var i = 1; i <= 35; i++)
        {
            log.Add(ActivityLog.PressKind, $"press {i}");
            log.Add("Recording", $"start {i}");
            log.Add("Recorded", $"stop {i}");
            log.Add("OpenAI", $"answer {i}");
        }
        var entries = log.Entries;
        Assert.Equal(ActivityLog.Limit * 4, entries.Count);
        Assert.Equal(ActivityLog.Limit, entries.Count(e => e.Kind == ActivityLog.PressKind));
        Assert.Equal("press 6", entries[^1].Text);
        Assert.Equal("answer 35", entries[0].Text);
    }

    [Fact]
    public void StepsWithoutAPressAreCappedOnAPressBoundary()
    {
        var list = new List<ActivityEntry> { new(DateTimeOffset.Now, "Recording", "window button") };
        for (var i = 0; i < 5; i++)
        {
            list.Add(new(DateTimeOffset.Now, ActivityLog.PressKind, $"press {i}"));
            for (var j = 0; j < ActivityLog.MaxLines / 4; j++) list.Add(new(DateTimeOffset.Now, "Recording", "step"));
        }
        ActivityLog.Trim(list);
        Assert.True(list.Count <= ActivityLog.MaxLines);
        Assert.Equal(ActivityLog.PressKind, list[0].Kind);
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
