using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class LocalActionsTests
{
    [Fact]
    public void AcceptsExistingWorkspaceAndNormalizesItsPath()
    {
        var folder = Directory.CreateTempSubdirectory("voiceagent-workspace-");
        try
        {
            Assert.Equal(folder.FullName, LocalActions.Workspace(Path.Combine(folder.FullName, ".")));
        }
        finally { folder.Delete(); }
    }

    [Fact]
    public async Task ReadsDisposableGitRepositoryWithoutChangingItsMetadata()
    {
        var folder = Directory.CreateTempSubdirectory("voiceagent-git-");
        try
        {
            // A minimal separate test repository keeps this test away from user repositories.
            var git = Directory.CreateDirectory(Path.Combine(folder.FullName, ".git"));
            Directory.CreateDirectory(Path.Combine(git.FullName, "objects"));
            Directory.CreateDirectory(Path.Combine(git.FullName, "refs"));
            var head = Path.Combine(git.FullName, "HEAD");
            File.WriteAllText(head, "ref: refs/heads/voiceagent-test\n");
            var before = File.ReadAllBytes(head);
            var text = await LocalActions.ReadGitHeadAsync(folder.FullName);
            Assert.Contains("Branch: voiceagent-test", text);
            Assert.Contains("no commit yet", text);
            Assert.Equal(before, File.ReadAllBytes(head));
            Assert.False(File.Exists(Path.Combine(git.FullName, "index")));
            Assert.False(File.Exists(Path.Combine(git.FullName, "index.lock")));
        }
        finally
        {
            var git = Path.Combine(folder.FullName, ".git");
            File.Delete(Path.Combine(git, "HEAD"));
            Directory.Delete(Path.Combine(git, "objects"));
            Directory.Delete(Path.Combine(git, "refs"));
            Directory.Delete(git);
            folder.Delete();
        }
    }

    [Fact]
    public async Task InitializesOnlyAnEmptyFolder()
    {
        var folder = Directory.CreateTempSubdirectory("voiceagent-git-init-");
        try
        {
            await LocalActions.InitializeEmptyRepositoryAsync(folder.FullName);
            Assert.Contains("no commit yet", await LocalActions.ReadGitHeadAsync(folder.FullName));
            await Assert.ThrowsAsync<IOException>(() => LocalActions.InitializeEmptyRepositoryAsync(folder.FullName));
        }
        finally
        {
            var path = Path.GetFullPath(folder.FullName);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(path, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"relative\project")]
    [InlineData(@"\\server\project")]
    [InlineData(@"\\?\E:\project")]
    [InlineData(@"C:\project")]
    public void RejectsPathsOutsideLocalDrive(string path) =>
        Assert.Throws<IOException>(() => LocalActions.Workspace(path));
}
