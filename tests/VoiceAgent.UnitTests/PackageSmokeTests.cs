using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class PackageSmokeTests
{
    [Fact]
    public void ProbeDoesNotOverwriteAnExistingReport()
    {
        var directory = Directory.CreateTempSubdirectory("trayvora-smoke-test-");
        try
        {
            var report = Path.Combine(directory.FullName, "report.json");
            File.WriteAllText(report, "keep this file");
            Assert.Equal(1, PackageSmoke.Run(report));
            Assert.Equal("keep this file", File.ReadAllText(report));
            Assert.Single(Directory.GetFiles(directory.FullName));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void ProbeRejectsRelativeReportPaths()
    {
        var relative = $"trayvora-smoke-{Guid.NewGuid():N}.json";
        Assert.Equal(1, PackageSmoke.Run(relative));
        Assert.False(File.Exists(relative));
    }
}
