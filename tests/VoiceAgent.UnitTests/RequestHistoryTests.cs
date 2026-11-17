using Microsoft.Data.Sqlite;
using VoiceAgent.Infrastructure;
using Xunit;

namespace VoiceAgent.UnitTests;

public sealed class RequestHistoryTests
{
    [Theory]
    [InlineData(RequestStatus.Completed)]
    [InlineData(RequestStatus.Failed)]
    [InlineData(RequestStatus.Interrupted)]
    [InlineData(RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Unknown)]
    public void ReopensTheSavedPromptAnswerAndFinalStatus(RequestStatus status)
    {
        var folder = Directory.CreateTempSubdirectory("voiceagent-history-");
        try
        {
            Guid id;
            using (var history = new RequestHistory(folder.FullName))
            {
                id = history.Start("Вопрос\nстрока");
                Assert.Equal(RequestStatus.Running, Assert.Single(history.Recent()).Status);
                history.Finish(id, status, "Частичный или полный ответ", "Summary", "Command execution · Completed");
                history.Finish(id, RequestStatus.Completed, "Duplicate completion", null);
            }

            using var reopened = new RequestHistory(folder.FullName);
            var summary = Assert.Single(reopened.Recent());
            Assert.Equal(id, summary.Id);
            Assert.Equal(status, summary.Status);
            Assert.Equal("Вопрос строка", summary.Preview);
            var entry = Assert.IsType<RequestDetails>(reopened.Find(id));
            Assert.Equal("Вопрос\nстрока", entry.Prompt);
            Assert.Equal("Частичный или полный ответ", entry.Answer);
            Assert.Equal("Summary", entry.Detail);
            Assert.Equal("Command execution · Completed", entry.ToolSummary);
            Assert.NotNull(entry.Finished);

            reopened.Delete(id);
            Assert.Empty(reopened.Recent());
        }
        finally
        {
            File.Delete(Path.Combine(folder.FullName, "history.db"));
            File.Delete(Path.Combine(folder.FullName, "history.lock"));
            folder.Delete();
        }
    }

    [Fact]
    public void MigratesVersionOneWithoutLosingExistingAnswers()
    {
        var folder = Directory.CreateTempSubdirectory("voiceagent-history-");
        var path = Path.Combine(folder.FullName, "history.db");
        try
        {
            var id = Guid.NewGuid();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE requests (id TEXT NOT NULL PRIMARY KEY, started_ms INTEGER NOT NULL,
                      finished_ms INTEGER, status TEXT NOT NULL, prompt TEXT NOT NULL, answer TEXT NOT NULL, detail TEXT);
                    INSERT INTO requests VALUES ($id, 0, 1, 'Completed', 'old prompt', 'old answer', NULL);
                    PRAGMA user_version = 1;
                    """;
                command.Parameters.AddWithValue("$id", id.ToString());
                command.ExecuteNonQuery();
            }

            using var migrated = new RequestHistory(folder.FullName);
            var entry = Assert.IsType<RequestDetails>(migrated.Find(id));
            Assert.Equal("old answer", entry.Answer);
            Assert.Empty(entry.ToolSummary);
        }
        finally
        {
            File.Delete(path);
            File.Delete(Path.Combine(folder.FullName, "history.lock"));
            folder.Delete();
        }
    }

    [Fact]
    public void ReopensTheSelectedChatAndKeepsDirectResultsInTheirOwnChat()
    {
        var folder = Directory.CreateTempSubdirectory("voiceagent-chats-");
        var chat = Guid.NewGuid();
        try
        {
            using (var history = new RequestHistory(folder.FullName))
            {
                history.AddConversation(chat, "Chat 2");
                history.SelectConversation(chat);
                var request = history.Start("Which branch?", chat, localGit: true);
                history.Finish(request, RequestStatus.Completed, "topic-branch", null);
                history.Start("What does it do?", chat);
                history.Start("Private to the first chat", Guid.Empty);
            }

            using var reopened = new RequestHistory(folder.FullName);
            var (chats, active) = reopened.Conversations();
            Assert.Equal(chat, active);
            Assert.Equal(2, chats.Count);
            Assert.Equal(2, reopened.Recent(chat).Count);
            Assert.Single(reopened.Recent(Guid.Empty));
            Assert.Equal(3, reopened.Recent().Count);
            Assert.Contains("topic-branch", reopened.RecentLocalResults(chat));
            Assert.Empty(reopened.RecentLocalResults(Guid.Empty));
        }
        finally
        {
            File.Delete(Path.Combine(folder.FullName, "history.db"));
            File.Delete(Path.Combine(folder.FullName, "history.lock"));
            folder.Delete();
        }
    }

    [Fact]
    public void RecoversAbandonedRequestOnlyAfterThePreviousOwnerExits()
    {
        var folder = Directory.CreateTempSubdirectory("voiceagent-history-");
        try
        {
            Guid id;
            using (var first = new RequestHistory(folder.FullName))
            {
                id = first.Start("Pending");
                using var second = new RequestHistory(folder.FullName);
                Assert.Throws<IOException>(() => second.Recent());
                Assert.Equal(RequestStatus.Running, Assert.Single(first.Recent()).Status);
                first.Delete(id);
                Assert.NotNull(first.Find(id));
            }

            using var reopened = new RequestHistory(folder.FullName);
            Assert.Equal(RequestStatus.Unknown, Assert.Single(reopened.Recent()).Status);
            Assert.NotNull(reopened.Find(id)?.Finished);
        }
        finally
        {
            File.Delete(Path.Combine(folder.FullName, "history.db"));
            File.Delete(Path.Combine(folder.FullName, "history.lock"));
            folder.Delete();
        }
    }
}
