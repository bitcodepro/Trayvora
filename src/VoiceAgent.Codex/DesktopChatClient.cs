using System.Diagnostics;
using System.Text.Json;

namespace VoiceAgent.Codex;

public sealed record DesktopChat(Guid Id, string Title, string? Workspace)
{
    public override string ToString() => Title;
}

public sealed record DesktopTurn(string Id, string Status, string Question, string Answer, IReadOnlyList<string> ClientIds);

public sealed class DesktopChatClient(string scratchDirectory)
{
    public async Task<IReadOnlyList<DesktopChat>> ListAsync(string profile, CancellationToken ct)
    {
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        return await ListAsync(server, ct).ConfigureAwait(false);
    }

    public sealed record ChatPage(IReadOnlyList<DesktopChat> Chats, string? Next);

    public async Task<ChatPage> PageAsync(string profile, string? cursor, CancellationToken ct)
    {
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        return await PageAsync(server, cursor, ct).ConfigureAwait(false);
    }

    private static async Task<ChatPage> PageAsync(ICodexConnection server, string? cursor, CancellationToken ct)
    {
        var result = await server.RequestAsync("thread/list", new
        {
            limit = 50, cursor, sortKey = "updated_at",
            sourceKinds = new[] { "cli", "vscode", "appServer", "unknown" },
        }, ct).ConfigureAwait(false);
        var page = new List<DesktopChat>();
        foreach (var entry in result.GetProperty("data").EnumerateArray())
        {
            if (!Guid.TryParse(ReadString(entry, "id"), out var id)) continue;
            var title = ReadString(entry, "name") ?? ReadString(entry, "preview");
            title = string.IsNullOrWhiteSpace(title) ? $"Chat {id.ToString()[..8]}" : title.ReplaceLineEndings(" ");
            page.Add(new DesktopChat(id, title, ReadString(entry, "cwd")));
        }
        return new(page, ReadString(result, "nextCursor"));
    }

    internal static async Task<IReadOnlyList<DesktopChat>> ListAsync(ICodexConnection server, CancellationToken ct)
    {
        var chats = new Dictionary<Guid, DesktopChat>();
        var seen = new HashSet<string>();
        string? cursor = null;
        do
        {
            var page = await PageAsync(server, cursor, ct).ConfigureAwait(false);
            foreach (var chat in page.Chats) chats.TryAdd(chat.Id, chat);
            cursor = page.Next;
        } while (!string.IsNullOrWhiteSpace(cursor) && seen.Add(cursor));
        return chats.Values.ToArray();
    }

    public async Task<IReadOnlyList<DesktopTurn>> ReadTurnsAsync(string profile, Guid chat, CancellationToken ct)
    {
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        return await ReadTurnsAsync(server, chat, ct, HistoryTurns).ConfigureAwait(false);
    }

    /// <summary>Turns loaded by Read history.</summary>
    internal const int HistoryTurns = 20;

    /// <summary>
    /// Turns read while waiting for a reply. The message just queued is among the newest, so only those are read:
    /// a long chat's last 20 turns with their full items can be tens of megabytes.
    /// </summary>
    internal const int PollTurns = 3;

    /// <param name="answer">Receives the reply written so far while the turn runs.</param>
    /// <param name="wait">How long to wait for the reply before returning null (the turn stays queued in Codex Desktop).</param>
    public async Task<DesktopTurn?> SubmitAsync(string profile, Guid chat, string text,
        IProgress<string> progress, CancellationToken ct, IProgress<string>? answer = null, TimeSpan? wait = null, Guid? delivery = null)
    {
        if (chat == Guid.Empty || string.IsNullOrWhiteSpace(text) || text.Length > 16000)
            throw new ArgumentException("Select a chat and enter up to 16,000 characters.");
        using var slot = await AgentTurnQueue.EnterAsync(profile, chat, ct).ConfigureAwait(false);
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        return await SubmitAsync(server, chat, text, progress, ct, answer, wait, delivery).ConfigureAwait(false);
    }

    internal static async Task<DesktopTurn?> SubmitAsync(ICodexConnection server, Guid chat, string text,
        IProgress<string> progress, CancellationToken ct, IProgress<string>? answer = null, TimeSpan? wait = null, Guid? delivery = null)
    {
        var messageId = (delivery ?? Guid.NewGuid()).ToString();
        ct.ThrowIfCancellationRequested();
        progress.Report("Submitting to the selected Codex chat…");
        var queued = await server.RequestAsync("thread/queue/add", new
        {
            threadId = chat.ToString(), clientUserMessageId = messageId,
            input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } },
        }, ct).ConfigureAwait(false);
        if (ReadString(queued.GetProperty("queuedSubmission"), "clientUserMessageId") != messageId)
            throw new InvalidDataException("Codex did not acknowledge the same message ID.");

        progress.Report("Queued in Codex. Waiting for this message's reply…");
        var deadline = Stopwatch.StartNew();
        var limit = wait ?? TimeSpan.FromMinutes(2);
        var shown = "";
        var searchedBoundaries = new HashSet<string>();
        while (limit == Timeout.InfiniteTimeSpan || deadline.Elapsed < limit)
        {
            var turn = await FindTurnAsync(server, chat, messageId, ct, searchedBoundaries).ConfigureAwait(false);
            // A separate reader can report a running Desktop turn as interrupted until it persists its final status.
            if (turn is { Status: "completed" or "failed" }) return turn;
            if (turn is not null)
            {
                progress.Report("Codex Desktop is working on it…");
                if (answer is not null && turn.Answer.Length > 0 && turn.Answer != shown)
                {
                    shown = turn.Answer;
                    answer.Report(shown);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<DesktopTurn?> CheckDeliveryAsync(string profile, Guid chat, Guid delivery, CancellationToken ct)
    {
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        return await FindTurnAsync(server, chat, delivery.ToString(), ct).ConfigureAwait(false);
    }

    private static async Task<DesktopTurn?> FindTurnAsync(ICodexConnection server, Guid chat, string messageId, CancellationToken ct, HashSet<string>? searchedBoundaries = null)
    {
        string? cursor = null;
        var seen = new HashSet<string>();
        do
        {
            var result = await server.RequestAsync("thread/turns/list", new
            {
                threadId = chat.ToString(), limit = PollTurns, cursor, itemsView = "full",
            }, ct).ConfigureAwait(false);
            var turns = ParseTurns(result);
            var found = turns.FirstOrDefault(turn => turn.ClientIds.Contains(messageId));
            if (found is not null) { searchedBoundaries?.Clear(); return found; }
            if (cursor is null && turns.Count > 0 && searchedBoundaries is not null && !searchedBoundaries.Add(turns[^1].Id)) return null;
            cursor = ReadString(result, "nextCursor");
        } while (cursor is { Length: > 0 } && seen.Add(cursor));
        return null;
    }

    /// <summary>
    /// Creates a chat in the Codex Desktop profile with its first message and runs that turn here, so the chat
    /// appears in Codex Desktop (an empty chat is not saved, so it is created together with the first message).
    /// Later messages go through <see cref="SubmitAsync(string, Guid, string, IProgress{string}, CancellationToken, IProgress{string}?, TimeSpan?)"/>.
    /// </summary>
    /// <returns>The new chat, the turn status and the reply.</returns>
    public async Task<(DesktopChat Chat, string Status, string Answer)> StartChatAsync(string profile, string workspace, string title, string text,
        IProgress<string> progress, IProgress<string> delta, CancellationToken ct)
    {
        if (!Directory.Exists(workspace)) throw new IOException("The folder for the new Codex chat does not exist.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new ArgumentException("Enter up to 16,000 characters.");
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        progress.Report("Creating the Codex Desktop chat…");
        // Trayvora cannot answer approval prompts, so the turn must not wait for one.
        var started = await server.RequestAsync("thread/start", new { cwd = workspace, ephemeral = false, approvalPolicy = "never" }, ct).ConfigureAwait(false);
        var threadId = started.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException("Codex did not report the chat ID.");
        if (!Guid.TryParse(threadId, out var id)) throw new InvalidDataException("Codex returned an unexpected chat ID.");
        try { await server.RequestAsync("thread/name/set", new { threadId, name = title }, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException) { } // the name is optional

        var turn = await server.RequestAsync("turn/start", new
        {
            threadId, input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } },
        }, ct).ConfigureAwait(false);
        var turnId = turn.GetProperty("turn").GetProperty("id").GetString();
        progress.Report("Codex is working on it…");
        var answer = new System.Text.StringBuilder();
        var chat = new DesktopChat(id, title, workspace);
        using var drain = new CancellationTokenSource();
        using var boundWait = ct.Register(() => drain.CancelAfter(TimeSpan.FromSeconds(5)));
        using var interrupt = ct.Register(() => _ = server.RequestAsync("turn/interrupt", new { threadId, turnId }, CancellationToken.None));
        await foreach (var message in server.ReadEventsAsync(drain.Token).WithCancellation(drain.Token))
        {
            if (!message.TryGetProperty("params", out var data) || data.ValueKind != JsonValueKind.Object ||
                ReadString(data, "threadId") != threadId || !message.TryGetProperty("method", out var method)) continue;
            switch (method.GetString())
            {
                case "item/agentMessage/delta" when ReadString(data, "turnId") == turnId:
                    var piece = ReadString(data, "delta") ?? "";
                    answer.Append(piece);
                    delta.Report(piece);
                    break;
                case "turn/completed" when data.TryGetProperty("turn", out var done) && ReadString(done, "id") == turnId:
                    return (chat, ReadString(done, "status") ?? "unknown", answer.ToString());
            }
        }
        return (chat, ct.IsCancellationRequested ? "interrupted" : "unknown", answer.ToString());
    }

    private static async Task<IReadOnlyList<DesktopTurn>> ReadTurnsAsync(ICodexConnection server, Guid chat, CancellationToken ct, int limit)
    {
        var result = await server.RequestAsync("thread/turns/list", new
        {
            threadId = chat.ToString(), limit, itemsView = "full",
        }, ct).ConfigureAwait(false);
        return ParseTurns(result);
    }

    internal static IReadOnlyList<DesktopTurn> ParseTurns(JsonElement response)
    {
        var turns = new List<DesktopTurn>();
        foreach (var turn in response.GetProperty("data").EnumerateArray())
        {
            var ids = new List<string>();
            var questions = new List<string>();
            var answers = new List<string>();
            var final = new List<string>();
            foreach (var item in turn.GetProperty("items").EnumerateArray())
            {
                switch (ReadString(item, "type"))
                {
                    case "userMessage":
                        if (ReadString(item, "clientId") is { } clientId) ids.Add(clientId);
                        foreach (var content in item.GetProperty("content").EnumerateArray())
                            if (ReadString(content, "type") == "text" && ReadString(content, "text") is { } question)
                                questions.Add(question);
                        break;
                    case "agentMessage":
                        if (ReadString(item, "text") is not { } answer) break;
                        var phase = ReadString(item, "phase");
                        if (phase == "final_answer") final.Add(answer);
                        if (phase != "commentary") answers.Add(answer);
                        break;
                }
            }

            turns.Add(new DesktopTurn(ReadString(turn, "id") ?? "",
                ReadString(turn, "status") ?? "unknown", string.Join("\n", questions),
                string.Join("\n\n", final.Count > 0 ? final : answers), ids));
        }
        return turns;
    }

    private AppServerConnection Connect(string profile)
    {
        if (!Path.IsPathFullyQualified(profile) || !Directory.Exists(Path.Combine(profile, "sessions")))
            throw new IOException("Choose the existing Codex Desktop profile folder.");
        var scratch = Path.GetFullPath(scratchDirectory);
        Directory.CreateDirectory(scratch);
        var start = new ProcessStartInfo(CodexSession.FindExecutable())
        {
            WorkingDirectory = scratch, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in new[] { "app-server", "--listen", "stdio://" }) start.ArgumentList.Add(argument);
        start.Environment["CODEX_HOME"] = Path.GetFullPath(profile);
        start.Environment.Remove("CODEX_SQLITE_HOME");
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("CODEX_API_KEY");
        start.Environment.Remove("ACCESS_TOKEN");
        start.Environment.Remove("OPENAI_BASE_URL");
        start.Environment["TEMP"] = scratch;
        start.Environment["TMP"] = scratch;
        return AppServerConnection.Start(start);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
