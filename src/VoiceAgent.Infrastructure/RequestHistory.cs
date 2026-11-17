using Microsoft.Data.Sqlite;

namespace VoiceAgent.Infrastructure;

public enum RequestStatus { Running, Completed, Interrupted, Cancelled, Failed, Unknown }

public sealed record RequestSummary(Guid Id, DateTimeOffset Started, RequestStatus Status, string Preview)
{
    public override string ToString() => $"{Started.ToLocalTime():g}   ·   {Status}   ·   {Preview}";
}

public sealed record RequestDetails(RequestSummary Summary, DateTimeOffset? Finished, string Prompt, string Answer, string? Detail, string ToolSummary);

public sealed record Conversation(Guid Id, string Title)
{
    public override string ToString() => Title;
}

public sealed class RequestHistory(string directory) : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _lease;
    private SqliteConnection? _connection;
    private bool _disposed;

    public (IReadOnlyList<Conversation> Chats, Guid Active) Conversations()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id, title, active FROM chats ORDER BY rowid DESC;");
            using var reader = command.ExecuteReader();
            var chats = new List<Conversation>();
            var active = Guid.Empty;
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                chats.Add(new Conversation(id, reader.GetString(1)));
                if (reader.GetBoolean(2)) active = id;
            }
            return (chats, active);
        }
    }

    public void AddConversation(Guid id, string title)
    {
        lock (_gate)
        {
            using var command = Command("INSERT OR IGNORE INTO chats (id, title) VALUES ($id, $title);",
                ("$id", id.ToString()), ("$title", title));
            command.ExecuteNonQuery();
        }
    }

    public void SelectConversation(Guid id)
    {
        lock (_gate)
        {
            using var command = Command("""
                UPDATE chats SET active = (id = $id) WHERE EXISTS (SELECT 1 FROM chats WHERE id = $id);
                """, ("$id", id.ToString()));
            if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("Chat not found.");
        }
    }

    public string RecentLocalResults(Guid chatId)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT substr(prompt, 1, 400), substr(answer, 1, 1200) FROM requests
                WHERE chat_id = $chat AND local_git = 1 AND status = 'Completed'
                ORDER BY rowid DESC LIMIT 6;
                """, ("$chat", chatId.ToString()));
            using var reader = command.ExecuteReader();
            var items = new List<object>();
            while (reader.Read()) items.Add(new { Question = reader.GetString(0), Answer = reader.GetString(1) });
            items.Reverse();
            return items.Count == 0 ? "" : System.Text.Json.JsonSerializer.Serialize(items);
        }
    }

    public Guid Start(string prompt, Guid? chatId = null, bool localGit = false)
    {
        var id = Guid.NewGuid();
        lock (_gate)
        {
            using var command = Command("""
                INSERT INTO requests (id, started_ms, status, prompt, answer, chat_id, local_git)
                VALUES ($id, $time, 'Running', $prompt, '', $chat, $local);
                """, ("$id", id.ToString()), ("$time", Now()), ("$prompt", prompt),
                ("$chat", chatId?.ToString()), ("$local", localGit));
            command.ExecuteNonQuery();
        }

        return id;
    }

    public void Finish(Guid id, RequestStatus status, string answer, string? detail, string toolSummary = "")
    {
        if (status == RequestStatus.Running)
        {
            throw new ArgumentException("A final status is required.", nameof(status));
        }

        lock (_gate)
        {
            var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = Command("""
                UPDATE requests SET status = $status, answer = $answer, detail = $detail, tool_summary = $tools, finished_ms = $time
                WHERE id = $id AND status = 'Running';
                DELETE FROM requests WHERE status <> 'Running' AND rowid IN
                  (SELECT old.rowid FROM requests AS old
                   WHERE old.status <> 'Running' AND old.chat_id IS
                     (SELECT chat_id FROM requests WHERE id = $id)
                   ORDER BY old.started_ms DESC, old.rowid DESC LIMIT -1 OFFSET 200);
                """, ("$status", status.ToString()), ("$answer", answer), ("$detail", detail),
                ("$time", Now()), ("$id", id.ToString()), ("$tools", toolSummary));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public IReadOnlyList<RequestSummary> Recent(Guid? chatId = null)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT id, started_ms, status, substr(prompt, 1, 96)
                FROM requests WHERE $chat IS NULL OR chat_id = $chat
                ORDER BY started_ms DESC, rowid DESC LIMIT 200;
                """, ("$chat", chatId?.ToString()));
            using var reader = command.ExecuteReader();
            var entries = new List<RequestSummary>();
            while (reader.Read())
            {
                entries.Add(ReadSummary(reader));
            }
            return entries;
        }
    }

    public RequestDetails? Find(Guid id)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT id, started_ms, status, substr(prompt, 1, 96), finished_ms, prompt, answer, detail, tool_summary
                FROM requests WHERE id = $id;
                """, ("$id", id.ToString()));
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new RequestDetails(ReadSummary(reader), reader.IsDBNull(4) ? null : Timestamp(reader.GetInt64(4)),
                    reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8))
                : null;
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            using var command = Command("DELETE FROM requests WHERE id = $id AND status <> 'Running';", ("$id", id.ToString()));
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is not null)
        {
            return _connection;
        }

        var path = Path.GetFullPath(directory);

        Directory.CreateDirectory(path);
        var lease = new FileStream(Path.Combine(path, "history.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(path, "history.db"), Pooling = false, DefaultTimeout = 3,
        }.ToString());
        try
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version;";
            switch (Convert.ToInt32(command.ExecuteScalar()))
            {
                case 0:
                    command.CommandText = """
                        CREATE TABLE requests (
                            id TEXT NOT NULL PRIMARY KEY,
                            started_ms INTEGER NOT NULL,
                            finished_ms INTEGER,
                            status TEXT NOT NULL,
                            prompt TEXT NOT NULL,
                            answer TEXT NOT NULL,
                            detail TEXT
                        );
                        CREATE INDEX requests_recency ON requests (started_ms DESC);
                        PRAGMA user_version = 1;
                        """;
                    command.ExecuteNonQuery();
                    goto case 1;
                case 1:
                    command.CommandText = """
                        ALTER TABLE requests ADD COLUMN tool_summary TEXT NOT NULL DEFAULT '';
                        PRAGMA user_version = 2;
                        """;
                    command.ExecuteNonQuery();
                    goto case 2;
                case 2:
                    command.CommandText = """
                        CREATE TABLE chats (
                            id TEXT NOT NULL PRIMARY KEY,
                            title TEXT NOT NULL,
                            active INTEGER NOT NULL DEFAULT 0
                        );
                        INSERT INTO chats (id, title, active)
                        VALUES ('00000000-0000-0000-0000-000000000000', 'Existing Codex chat', 1);
                        ALTER TABLE requests ADD COLUMN chat_id TEXT;
                        ALTER TABLE requests ADD COLUMN local_git INTEGER NOT NULL DEFAULT 0;
                        CREATE INDEX requests_chat ON requests (chat_id, started_ms DESC);
                        PRAGMA user_version = 3;
                        """;
                    command.ExecuteNonQuery();
                    break;
                case 3:
                    break;
                default:
                    throw new InvalidDataException("The history database was created by a newer Trayvora version.");
            }

            command.CommandText = """
                UPDATE requests SET status = 'Unknown', finished_ms = $time,
                  detail = 'VoiceAgent closed before recording the final status.'
                WHERE status = 'Running';
                """;
            command.Parameters.AddWithValue("$time", Now());
            command.ExecuteNonQuery();
            transaction.Commit();
            _lease = lease;
            _connection = connection;
            return connection;
        }
        catch
        {
            connection.Dispose();
            lease.Dispose();
            throw;
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = Open().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }

    private static RequestSummary ReadSummary(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Timestamp(reader.GetInt64(1)),
        Enum.Parse<RequestStatus>(reader.GetString(2)),
        reader.GetString(3).ReplaceLineEndings(" "));

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static DateTimeOffset Timestamp(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try { _connection?.Dispose(); }
            finally { _lease?.Dispose(); }
        }
    }
}
