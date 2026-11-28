using System.Diagnostics;
using System.Text.Json;

namespace VoiceAgent.Codex;

public sealed record DesktopChat(Guid Id, string Title, string? Workspace)
{
    public override string ToString() => Title;
}

public sealed record DesktopTurn(string Id, string Status, string Question, string Answer, IReadOnlyList<string> ClientIds)
{
    public IReadOnlyList<CodexActivity> Activity { get; init; } = [];
    public bool InterruptionConfirmed { get; init; }
    public bool IsTerminal => Status is "completed" or "failed" || Status == "interrupted" && InterruptionConfirmed;
}

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
        IProgress<string> progress, CancellationToken ct, IProgress<string>? answer = null, TimeSpan? wait = null, Guid? delivery = null, Action? onAccepted = null,
        IReadOnlyList<string>? attachments = null, IProgress<CodexActivity>? activity = null, Func<Task>? beforeDispatch = null,
        Action<Func<string, CancellationToken, Task>?>? steer = null, Func<string, Task>? onTurnStarted = null)
    {
        if (chat == Guid.Empty || string.IsNullOrWhiteSpace(text) || text.Length > 16000)
            throw new ArgumentException("Select a chat and enter up to 16,000 characters.");
        using var slot = await AgentTurnQueue.EnterAsync(profile, chat, ct).ConfigureAwait(false);
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        return await SubmitAsync(server, chat, text, progress, ct, answer, wait, delivery, onAccepted, attachments, activity, beforeDispatch, observeEvents: true, steer, onTurnStarted).ConfigureAwait(false);
    }

    internal static async Task<DesktopTurn?> SubmitAsync(ICodexConnection server, Guid chat, string text,
        IProgress<string> progress, CancellationToken ct, IProgress<string>? answer = null, TimeSpan? wait = null, Guid? delivery = null, Action? onAccepted = null,
        IReadOnlyList<string>? attachments = null, IProgress<CodexActivity>? activity = null, Func<Task>? beforeDispatch = null,
        bool observeEvents = false, Action<Func<string, CancellationToken, Task>?>? steer = null, Func<string, Task>? onTurnStarted = null)
    {
        var messageId = (delivery ?? Guid.NewGuid()).ToString();
        var input = TurnInput(text, attachments);
        using var listening = new CancellationTokenSource();
        var finished = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Status, string Answer)>();
        var live = new CodexTurnEvents(null, answer, activity);
        string? liveTurn = null;
        var receipt = 0;
        var observations = observeEvents ? ObserveTurnAsync() : Task.CompletedTask;
        string? submission = null;
        var submissionStarted = false;
        string? boundTurn = null;
        DesktopTurn? observed = null;
        try
        {
            if (beforeDispatch is not null) await beforeDispatch().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            progress.Report("Submitting to the selected Codex chat…");
            // Once queue/add starts, get its reply even if Stop is pressed while the server is accepting it.
            using var admission = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            submissionStarted = true;
            JsonElement queued;
            try
            {
                queued = await server.RequestAsync("thread/queue/add", new
                {
                    threadId = chat.ToString(), clientUserMessageId = messageId, input,
                }, admission.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is TimeoutException or OperationCanceledException)
            {
                throw new IOException("Codex did not confirm the submission. Use Check reply to check this message.", error);
            }
            var accepted = queued.GetProperty("queuedSubmission");
            if (ReadString(accepted, "clientUserMessageId") != messageId)
                throw new InvalidDataException("Codex did not acknowledge the same message ID.");
            submission = ReadString(accepted, "id");
            ConfirmReceipt();

            progress.Report("Queued in Codex. Waiting for this message's reply…");
            var deadline = Stopwatch.StartNew();
            var limit = wait ?? TimeSpan.FromMinutes(2);
            var shown = "";
            var searchedBoundaries = new HashSet<string>();
            while (limit == Timeout.InfiniteTimeSpan || deadline.Elapsed < limit)
            {
                ct.ThrowIfCancellationRequested();
                observed = await FindTurnAsync(server, chat, messageId, ct, searchedBoundaries).ConfigureAwait(false) ?? observed;
                if (observed is not null)
                {
                    if (observed.Id.Length > 0 && observed.Id != boundTurn)
                    {
                        boundTurn = observed.Id;
                        if (onTurnStarted is not null) await onTurnStarted(boundTurn).ConfigureAwait(false);
                        steer?.Invoke(observed.Status == "inProgress" ? Clarify(server, chat.ToString(), boundTurn, ct) : null);
                    }
                    foreach (var step in observed.Activity) activity?.Report(step);
                    if (finished.TryGetValue(observed.Id, out var completion))
                        return await ConfirmedTurnAsync(observed, completion, ct).ConfigureAwait(false);
                    if (observed.IsTerminal) return observed;
                    progress.Report("Codex Desktop is working on it…");
                    if (answer is not null && Volatile.Read(ref liveTurn) != observed.Id && observed.Answer.Length > 0 && observed.Answer != shown)
                    {
                        shown = observed.Answer;
                        answer.Report(shown);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            return null;
        }
        catch (Exception error) when (submissionStarted && ct.IsCancellationRequested &&
            error is OperationCanceledException or IOException or InvalidDataException or TimeoutException or InvalidOperationException or KeyNotFoundException)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (submission is null)
                {
                    try { submission = await FindSubmissionAsync(server, chat, messageId, stop.Token).ConfigureAwait(false); }
                    catch (AppServerRpcException) { }
                }
                if (submission is not null)
                {
                    try
                    {
                        var deleted = await server.RequestAsync("thread/queue/delete",
                            new { threadId = chat.ToString(), queuedSubmissionId = submission }, stop.Token).ConfigureAwait(false);
                        if (deleted.TryGetProperty("deleted", out var removed) && removed.ValueKind == JsonValueKind.True)
                            return StopResult("removed");
                    }
                    catch (AppServerRpcException) { } // older clients or the item has just started
                }
                observed = await FindTurnAsync(server, chat, messageId, stop.Token).ConfigureAwait(false) ?? observed;
                if (observed?.Id is { Length: > 0 } turnId)
                {
                    if (onTurnStarted is not null && boundTurn != turnId) await onTurnStarted(turnId).ConfigureAwait(false);
                    if (finished.TryGetValue(turnId, out var completion))
                        return await ConfirmedTurnAsync(observed, completion, stop.Token).ConfigureAwait(false);
                    if (observed.IsTerminal) return observed;
                    try { await server.RequestAsync("turn/interrupt", new { threadId = chat.ToString(), turnId }, stop.Token).ConfigureAwait(false); }
                    catch (AppServerRpcException) when (finished.ContainsKey(turnId)) { }
                    do
                    {
                        if (finished.TryGetValue(turnId, out completion))
                            return await ConfirmedTurnAsync(observed, completion, stop.Token).ConfigureAwait(false);
                        if (observations.IsCompleted) break;
                        await Task.WhenAny(observations, Task.Delay(150, stop.Token)).ConfigureAwait(false);
                        stop.Token.ThrowIfCancellationRequested();
                    } while (true);
                    return StopResult("stopRequested");
                }
            }
            catch (Exception stopError) when (stopError is OperationCanceledException or IOException or InvalidDataException or TimeoutException or InvalidOperationException or KeyNotFoundException) { }
            return StopResult("detached");
        }
        finally
        {
            steer?.Invoke(null);
            await listening.CancelAsync().ConfigureAwait(false);
            await observations.ConfigureAwait(false);
        }

        async Task<DesktopTurn> ConfirmedTurnAsync(DesktopTurn current, (string Status, string Answer) completion, CancellationToken token)
        {
            // An event can arrive while a history read is in progress. Read once more, retaining any newer live text.
            try { current = await FindTurnAsync(server, chat, current.Id, token, byTurnId: true).ConfigureAwait(false) ?? current; }
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or TimeoutException) { }
            var text = current.Answer;
            if (completion.Answer.Length > text.Length && completion.Answer.StartsWith(text, StringComparison.Ordinal))
                text = completion.Answer;
            return current with { Status = completion.Status, Answer = text, InterruptionConfirmed = completion.Status == "interrupted" };
        }

        void ConfirmReceipt()
        {
            if (Interlocked.Exchange(ref receipt, 1) == 0) onAccepted?.Invoke();
        }

        DesktopTurn StopResult(string status) => observed is null
            ? new DesktopTurn("", status, "", "", [messageId]) : observed with { Status = status };

        async Task ObserveTurnAsync()
        {
            try
            {
                await foreach (var notification in server.ReadEventsAsync(listening.Token).WithCancellation(listening.Token).ConfigureAwait(false))
                {
                    if (!notification.TryGetProperty("params", out var data) || ReadString(data, "threadId") != chat.ToString()) continue;
                    var method = ReadString(notification, "method");
                    var turnId = ReadString(data, "turnId");
                    if (liveTurn is null && turnId is { Length: > 0 } && method is "item/started" or "item/completed"
                        && data.TryGetProperty("item", out var item) && ReadString(item, "type") == "userMessage"
                        && ReadString(item, "clientId") == messageId)
                    {
                        Volatile.Write(ref liveTurn, turnId);
                        ConfirmReceipt();
                    }
                    if (method == "turn/completed" && data.TryGetProperty("turn", out var done)
                        && ReadString(done, "id") is { Length: > 0 } id
                        && ReadString(done, "status") is ("completed" or "failed" or "interrupted") and { } status)
                    {
                        if (id == liveTurn) live.FlushAnswer();
                        if (finished.Count >= 128) finished.Clear();
                        finished[id] = (status, id == liveTurn ? live.Answer : "");
                    }
                    else if (method is not null && liveTurn is not null && turnId == liveTurn && !live.Observe(method, data))
                        live.FlushAnswer();
                }
            }
            catch (Exception readError) when (readError is OperationCanceledException or IOException or InvalidDataException) { }
            finally { live.FlushAnswer(); }
        }
    }

    public async Task<DesktopTurn?> CheckDeliveryAsync(string profile, Guid chat, Guid delivery, CancellationToken ct)
    {
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        return await CheckDeliveryAsync(server, chat, delivery, ct).ConfigureAwait(false);
    }

    internal static Task<DesktopTurn?> CheckDeliveryAsync(ICodexConnection server, Guid chat, Guid delivery, CancellationToken ct) =>
        FindTurnAsync(server, chat, delivery.ToString(), ct);

    public async Task<DesktopTurn?> CheckTurnAsync(string profile, Guid chat, string turnId, CancellationToken ct)
    {
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        return await FindTurnAsync(server, chat, turnId, ct, byTurnId: true).ConfigureAwait(false);
    }

    private static async Task<DesktopTurn?> FindTurnAsync(ICodexConnection server, Guid chat, string messageId, CancellationToken ct, HashSet<string>? searchedBoundaries = null, bool byTurnId = false)
    {
        string? cursor = null;
        var seen = new HashSet<string>();
        do
        {
            JsonElement result;
            try
            {
                result = await server.RequestAsync("thread/turns/list", new
                {
                    threadId = chat.ToString(), limit = PollTurns, cursor, itemsView = "full",
                }, ct).ConfigureAwait(false);
            }
            catch (AppServerRpcException error) when (error.TurnHistoryNotReady) { return null; }
            var turns = ParseTurns(result);
            var found = turns.FirstOrDefault(turn => byTurnId ? turn.Id == messageId : turn.ClientIds.Contains(messageId));
            if (found is not null) { searchedBoundaries?.Clear(); return found; }
            if (cursor is null && turns.Count > 0 && searchedBoundaries is not null && !searchedBoundaries.Add(turns[^1].Id)) return null;
            cursor = ReadString(result, "nextCursor");
        } while (cursor is { Length: > 0 } && seen.Add(cursor));
        return null;
    }

    /// <summary>Creates a Desktop chat and submits its first message with the saved recovery ID.</summary>
    public async Task<(DesktopChat Chat, DesktopTurn? Turn)> StartChatAsync(string profile, string workspace, string title, string text,
        IProgress<string> progress, IProgress<string> answer, CancellationToken ct, Func<DesktopChat, Task<Guid>> saveDelivery,
        Action? onAccepted = null, Func<string, Task>? onTurnStarted = null, Func<Task>? beforeDispatch = null,
        IReadOnlyList<string>? attachments = null, IProgress<CodexActivity>? activity = null, Action<Func<string, CancellationToken, Task>?>? steer = null)
    {
        if (!Directory.Exists(workspace)) throw new IOException("The folder for the new Codex chat does not exist.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new ArgumentException("Enter up to 16,000 characters.");
        await using var server = Connect(profile);
        await server.InitializeAsync(ct, experimental: true).ConfigureAwait(false);
        return await StartChatAsync(server, workspace, title, text, progress, answer, ct, saveDelivery,
            onAccepted, onTurnStarted, beforeDispatch, attachments, activity, steer, observeEvents: true).ConfigureAwait(false);
    }

    internal static async Task<(DesktopChat Chat, DesktopTurn? Turn)> StartChatAsync(ICodexConnection server, string workspace, string title, string text,
        IProgress<string> progress, IProgress<string> answer, CancellationToken ct, Func<DesktopChat, Task<Guid>> saveDelivery,
        Action? onAccepted = null, Func<string, Task>? onTurnStarted = null, Func<Task>? beforeDispatch = null,
        IReadOnlyList<string>? attachments = null, IProgress<CodexActivity>? activity = null,
        Action<Func<string, CancellationToken, Task>?>? steer = null, bool observeEvents = false)
    {
        progress.Report("Creating the Codex Desktop chat…");
        // Trayvora cannot answer approval prompts, so the turn must not wait for one.
        var started = await server.RequestAsync("thread/start", new { cwd = workspace, ephemeral = false, approvalPolicy = "never" }, ct).ConfigureAwait(false);
        var threadId = ReadString(started.GetProperty("thread"), "id");
        if (!Guid.TryParse(threadId, out var id) || id == Guid.Empty) throw new InvalidDataException("Codex returned an unexpected chat ID.");
        var chat = new DesktopChat(id, title, workspace);
        var delivery = await saveDelivery(chat).ConfigureAwait(false);
        if (delivery == Guid.Empty) throw new ArgumentException("The first message needs a saved delivery ID.", nameof(saveDelivery));
        try { await server.RequestAsync("thread/name/set", new { threadId, name = title }, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException) { } // the name is optional

        // Queueing on an idle thread starts its turn. The user item keeps clientUserMessageId even if the reply to queue/add is lost.
        var turn = await SubmitAsync(server, id, text, progress, ct, answer, TimeSpan.FromMinutes(10), delivery,
            onAccepted, attachments, activity, beforeDispatch, observeEvents, steer, onTurnStarted).ConfigureAwait(false);
        return (chat, turn);
    }

    private static Func<string, CancellationToken, Task> Clarify(ICodexConnection server, string threadId, string turnId, CancellationToken stopping) =>
        async (text, ct) =>
        {
            if (stopping.IsCancellationRequested) throw new InvalidOperationException("This turn is stopping.");
            var reply = await server.RequestAsync("turn/steer", new { threadId, expectedTurnId = turnId, input = TurnInput(text, null) }, ct).ConfigureAwait(false);
            if (ReadString(reply, "turnId") != turnId) throw new InvalidDataException("Codex did not confirm the original turn.");
        };

    private static async Task<string?> FindSubmissionAsync(ICodexConnection server, Guid thread, string message, CancellationToken ct)
    {
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var list = await server.RequestAsync("thread/queue/list", new { threadId = thread.ToString(), cursor, limit = 100 }, ct).ConfigureAwait(false);
            if (list.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var queued in data.EnumerateArray())
                    if (ReadString(queued, "clientUserMessageId") == message) return ReadString(queued, "id");
            cursor = ReadString(list, "nextCursor");
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return null;
    }

    private static object[] TurnInput(string text, IReadOnlyList<string>? attachments) =>
        [new { type = "text", text, text_elements = Array.Empty<object>() }, .. CodexSession.AttachmentInput(attachments)];

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
            var activity = new List<CodexActivity>();
            var reducer = new CodexTurnEvents(null, null, new ActivityCollector(activity));
            foreach (var item in turn.GetProperty("items").EnumerateArray())
            {
                reducer.UpdateItem(item, completed: ReadString(item, "status") is not "inProgress" and not "running");
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
                string.Join("\n\n", final.Count > 0 ? final : answers), ids)
            {
                Activity = activity,
                // App Server can project a still-running or abandoned turn as interrupted with no recorded end.
                InterruptionConfirmed = turn.TryGetProperty("completedAt", out var end) && end.ValueKind == JsonValueKind.Number
                    && end.TryGetInt64(out var completedAt) && completedAt > 0,
            });
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

    private sealed class ActivityCollector(List<CodexActivity> activity) : IProgress<CodexActivity>
    {
        public void Report(CodexActivity item) => activity.Add(item);
    }

    private static string? ReadString(JsonElement element, string property) => CodexTurnEvents.String(element, property);
}
