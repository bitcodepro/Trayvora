using VoiceAgent.Desktop;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class DiscussionStoreConcurrencyTests
{
    [Fact]
    public async Task ReadingWhileSavingNeverLosesASession()
    {
        var folder = Directory.CreateTempSubdirectory("trayvora-store-");
        try
        {
            var store = new DiscussionStore(folder.FullName);
            var room = store.Create(null);
            var failures = 0;
            using var stop = new CancellationTokenSource();
            // Reads run as often as possible while the same session is replaced 300 times.
            var reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                    if (store.Load().Count != 1) Interlocked.Increment(ref failures);
            });
            for (var i = 0; i < 300; i++)
            {
                room.Title = $"Title {i}";
                store.Save(room);
            }
            stop.Cancel();
            await reader;
            Assert.Equal(0, failures);
            Assert.Equal("Title 299", Assert.Single(store.Load()).Title);
        }
        finally { folder.Delete(recursive: true); }
    }
}
