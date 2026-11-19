using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using VoiceAgent.Codex;
using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class InteractiveWorkspaceTests
{
    [Fact]
    public void RoomSurvivesRestartBeforeFirstDesktopTurn()
    {
        var tmp = Directory.CreateTempSubdirectory();
        try
        {
            var store = new DiscussionStore(tmp.FullName);
            var room = store.Create(null);
            room.CodexChatId = Guid.Empty;
            room.CodexDesktop = true;
            room.NewCodexFolder = tmp.FullName;
            room.Messages.Add(new(DiscussionMember.Boss, DiscussionMember.Codex, "saved", DateTimeOffset.Now));
            store.Save(room);
            var header = Assert.Single(store.ListHeaders());
            Assert.Empty(header.Messages);
            Assert.Equal("saved", header.Preview);
            Assert.Equal(Guid.Empty, store.ReadRoom(header.Id).CodexChatId);
            var dir = Directory.CreateDirectory(Path.Combine(tmp.FullName, "attachments", room.Id.ToString("N")));
            var attachment = Path.Combine(dir.FullName, "generated.txt");
            File.WriteAllText(attachment, "test");
            store.Delete(room.Id);
            Assert.False(File.Exists(attachment));
            Assert.Empty(store.ListHeaders());
        }
        finally { tmp.Delete(true); }
    }

    [Fact]
    public void FileReferenceUsesItsWorkspaceAndSelectsRequestedLines()
    {
        var tmp = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(tmp.FullName, "класс 1.cs");
            File.WriteAllLines(file, ["first", "second", "third", "fourth"]);
            var link = "класс%201.cs#L2-L3";
            Assert.True(FilePreview.IsReference(link));
            Assert.Equal(file, FilePreview.Resolve(link, tmp.FullName));
            var page = FilePreview.ReadPage(file, 2, 3);
            Assert.Contains("second", page.Text[page.Start..page.End]);
            Assert.DoesNotContain("fourth", page.Text[page.Start..page.End]);
            Assert.Throws<IOException>(() => FilePreview.Resolve("../outside.cs", tmp.FullName));
            Assert.False(FilePreview.IsReference("file://server/data/c.cs"));
            Assert.False(FilePreview.IsReference("command:run"));
            Assert.Equal(42, FilePreview.Parse(@"E:\example.cs:42").Line);
        }
        finally { tmp.Delete(true); }
    }

    [Fact]
    public async Task TwoWritersToOneChatWaitAndCancelledWaitersReleaseTheirSlot()
    {
        var id = Guid.NewGuid();
        var folder = Path.GetTempPath();
        using (await AgentTurnQueue.EnterAsync(folder, id, default))
        {
            using var cts = new CancellationTokenSource();
            var queued = AgentTurnQueue.EnterAsync(folder, id, cts.Token);
            Assert.False(queued.IsCompleted);
            using var other = await AgentTurnQueue.EnterAsync(folder, Guid.NewGuid(), default);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        }
        using var next = await AgentTurnQueue.EnterAsync(folder, id, default);
    }

    private sealed class Handler(byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage _, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    }

    [Fact]
    public async Task DownloadVerifiesBytesBeforeReplacingAPackage()
    {
        var tmp = Directory.CreateTempSubdirectory();
        try
        {
            byte[] data = [1, 2, 3, 4];
            using var http = new HttpClient(new Handler(data));
            var release = new AvailableUpdate(new Version(9, 0), "https://example.invalid/update", Convert.ToHexString(SHA256.HashData(data)), "");
            var file = await GitHubUpdate.DownloadAsync(http, release, tmp.FullName, null, default);
            await Assert.ThrowsAsync<InvalidDataException>(() => GitHubUpdate.DownloadAsync(http, release with { Sha256 = new string('0', 64) }, tmp.FullName, null, default));
            Assert.Equal(data, File.ReadAllBytes(file));
            Assert.Single(tmp.GetFiles());
        }
        finally { tmp.Delete(true); }
    }

    [Fact]
    public void EditorExtensionExportsAnInstallableManifest()
    {
        var tmp = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(tmp.FullName, "bridge.vsix");
            IdeSession.Export(file);
            using var zip = ZipFile.OpenRead(file);
            Assert.NotNull(zip.GetEntry("extension/package.json"));
            Assert.NotNull(zip.GetEntry("extension/extension.js"));
            Assert.NotNull(zip.GetEntry("extension.vsixmanifest"));
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        }
        finally { tmp.Delete(true); }
    }
}
