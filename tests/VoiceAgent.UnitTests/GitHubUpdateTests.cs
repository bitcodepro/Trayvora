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

    [Fact]
    public void UpdateRemovesOldProgramFilesButKeepsSettingsModelsAndUserFiles()
    {
        var root = Directory.CreateTempSubdirectory("trayvora-stale-");
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app")).FullName;
            var update = Directory.CreateDirectory(Path.Combine(root.FullName, "new")).FullName;
            void Put(string folder, string relative, string text = "x")
            {
                var path = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            Put(app, "Trayvora.exe", "old");
            Put(app, "Old.Library.dll");
            Put(app, Path.Combine("runtimes", "win-x86", "old.dll"));
            Put(app, Path.Combine("settings", "speech.json"), "mine");
            Put(app, Path.Combine("history", "chat.db"), "mine");
            Put(app, Path.Combine("models", "ggml-base.bin"), "model");
            Put(app, "my-notes.txt", "mine");
            Put(update, "Trayvora.exe", "new");
            Put(update, "New.Library.dll");
            Put(update, Path.Combine("runtimes", "win-x64", "whisper.dll"));

            PortableUpdate.CopyProgram(update, app);
            PortableUpdate.RemoveStale(update, app);

            Assert.Equal("new", File.ReadAllText(Path.Combine(app, "Trayvora.exe")));
            Assert.True(File.Exists(Path.Combine(app, "New.Library.dll")));
            Assert.False(File.Exists(Path.Combine(app, "Old.Library.dll")));
            Assert.False(File.Exists(Path.Combine(app, "runtimes", "win-x86", "old.dll")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "settings", "speech.json")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "history", "chat.db")));
            Assert.Equal("model", File.ReadAllText(Path.Combine(app, "models", "ggml-base.bin")));
            Assert.True(File.Exists(Path.Combine(app, "my-notes.txt")));

            // The next update uses the recorded list, so a dropped non-library program file goes too.
            File.Delete(Path.Combine(update, "New.Library.dll"));
            PortableUpdate.RemoveStale(update, app);
            Assert.False(File.Exists(Path.Combine(app, "New.Library.dll")));
            Assert.True(File.Exists(Path.Combine(app, "my-notes.txt")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
