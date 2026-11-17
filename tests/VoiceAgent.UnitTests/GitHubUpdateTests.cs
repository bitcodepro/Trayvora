using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class GitHubUpdateTests
{
    [Fact]
    public void OnlyNewerReleasesAreOfferedAndUnreleasedBuildsGetTheLatest()
    {
        Assert.True(GitHubUpdate.IsNewer(new Version(0, 1, 2), new Version(0, 1, 1)));
        Assert.False(GitHubUpdate.IsNewer(new Version(0, 1, 1), new Version(0, 1, 1)));
        Assert.False(GitHubUpdate.IsNewer(new Version(0, 1, 1), new Version(0, 1, 10)));
        Assert.True(GitHubUpdate.IsNewer(new Version(0, 1, 1), null));
    }

    [Fact]
    public void LatestReleaseLinksMustPointAtTheTrayvoraRepository()
    {
        const string good = """
            {"html_url":"https://github.com/bitcodepro/Trayvora/releases/tag/v0.1.2","assets":[
              {"name":"trayvora-win-x64.zip","browser_download_url":"https://github.com/bitcodepro/Trayvora/releases/download/v0.1.2/trayvora-win-x64.zip"},
              {"name":"release.json","browser_download_url":"https://github.com/bitcodepro/Trayvora/releases/download/v0.1.2/release.json"}]}
            """;
        var parsed = GitHubUpdate.Parse(good);
        Assert.NotNull(parsed);
        Assert.EndsWith("v0.1.2/trayvora-win-x64.zip", parsed.Value.Zip);
        Assert.Null(GitHubUpdate.Parse(good.Replace("https://github.com/bitcodepro/Trayvora/releases/download/v0.1.2/trayvora", "https://evil.example/trayvora")));
        Assert.Null(GitHubUpdate.Parse("""{"assets":[]}"""));
    }

    [Fact]
    public void InstalledVersionComesFromReleaseJsonAndChecksumIsVerified()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-version-");
        try
        {
            Assert.Null(GitHubUpdate.CurrentVersion(folder.FullName));
            File.WriteAllText(Path.Combine(folder.FullName, "release.json"), """{"version":"0.1.7","runtime":"win-x64"}""");
            Assert.Equal(new Version(0, 1, 7), GitHubUpdate.CurrentVersion(folder.FullName));
            var file = Path.Combine(folder.FullName, "a.bin");
            File.WriteAllText(file, "abc");
            Assert.True(GitHubUpdate.HashMatches(file, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
            Assert.False(GitHubUpdate.HashMatches(file, "00"));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static void Put(string folder, string relative, string text = "x")
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void FirstUpdateDeletesNothingItDoesNotOwnAndLaterUpdatesRemoveDroppedProgramFiles()
    {
        var root = Directory.CreateTempSubdirectory("trayvora-stale-");
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app")).FullName;
            var update = Directory.CreateDirectory(Path.Combine(root.FullName, "new")).FullName;
            Put(app, "Trayvora.exe", "old");
            Put(app, "Old.Library.dll");
            Put(app, "MyOwnLibrary.dll", "mine");
            Put(app, Path.Combine("runtimes", "personal", "notes.txt"), "mine");
            Put(app, Path.Combine("settings", "speech.json"), "mine");
            Put(app, Path.Combine("models", "ggml-base.bin"), "model");
            Put(update, "Trayvora.exe", "new");
            Put(update, "New.Library.dll");
            Put(update, Path.Combine("runtimes", "win-x64", "whisper.dll"));

            // No inventory yet: ownership is unknown, so nothing is deleted.
            PortableUpdate.CopyProgram(update, app);
            Assert.Empty(PortableUpdate.RemoveStale(update, app));
            Assert.Equal("new", File.ReadAllText(Path.Combine(app, "Trayvora.exe")));
            Assert.True(File.Exists(Path.Combine(app, "Old.Library.dll")));
            Assert.True(File.Exists(Path.Combine(app, "MyOwnLibrary.dll")));
            Assert.True(File.Exists(Path.Combine(app, "runtimes", "personal", "notes.txt")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "settings", "speech.json")));
            Assert.Equal("model", File.ReadAllText(Path.Combine(app, "models", "ggml-base.bin")));

            // The next update knows what it installed and removes only that.
            File.Delete(Path.Combine(update, "New.Library.dll"));
            Assert.Empty(PortableUpdate.RemoveStale(update, app));
            Assert.False(File.Exists(Path.Combine(app, "New.Library.dll")));
            Assert.True(File.Exists(Path.Combine(app, "MyOwnLibrary.dll")));
            Assert.True(File.Exists(Path.Combine(app, "runtimes", "personal", "notes.txt")));
            Assert.True(File.Exists(Path.Combine(app, "runtimes", "win-x64", "whisper.dll")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void StaleFileThatCannotBeDeletedStaysListedAndIsRetried()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("trayvora-locked-");
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app")).FullName;
            var update = Directory.CreateDirectory(Path.Combine(root.FullName, "new")).FullName;
            Put(update, "Trayvora.exe");
            Put(update, "Dropped.dll");
            PortableUpdate.CopyProgram(update, app);
            PortableUpdate.RemoveStale(update, app);
            File.Delete(Path.Combine(update, "Dropped.dll"));

            using (new FileStream(Path.Combine(app, "Dropped.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Equal(["Dropped.dll"], PortableUpdate.RemoveStale(update, app));
            Assert.Contains("Dropped.dll", File.ReadAllLines(PortableUpdate.ManifestFile(app)));

            Assert.Empty(PortableUpdate.RemoveStale(update, app));
            Assert.False(File.Exists(Path.Combine(app, "Dropped.dll")));
            Assert.DoesNotContain("Dropped.dll", File.ReadAllLines(PortableUpdate.ManifestFile(app)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void OnlyOneUpdateCanRunAtATime()
    {
        Assert.True(PortableUpdate.TryBegin());
        try
        {
            Assert.False(PortableUpdate.TryBegin());
        }
        finally
        {
            PortableUpdate.End();
        }
        Assert.True(PortableUpdate.TryBegin());
        PortableUpdate.End();
    }
}
