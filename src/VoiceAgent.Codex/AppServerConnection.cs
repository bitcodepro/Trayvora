using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VoiceAgent.Codex;

// Owns one stdio session. The caller supplies a trusted executable and reads events concurrently.
internal sealed class AppServerConnection : ICodexConnection
{
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _requestLock = new(1);
    private readonly SemaphoreSlim _writeLock = new(1);
    private readonly Channel<JsonElement> _events = Channel.CreateBounded<JsonElement>(128);
    private readonly Task _readTask;
    private readonly Task _stderrTask;
    private PendingRequest? _pending;
    private int _id;

    private AppServerConnection(Process process)
    {
        _process = process;
        _readTask = ReadAsync();
        _stderrTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null, _lifetime.Token);
    }

    public static AppServerConnection Start(string executable, string workingDirectory, string home, IReadOnlyList<string> disabledFeatures, string? revqora = null)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("Use absolute executable and working-directory paths.");
        }

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "app-server", "--listen", "stdio://" })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var value in new[] { "model_provider=\"openai\"", "web_search=\"disabled\"", "forced_login_method=\"chatgpt\"", "cli_auth_credentials_store=\"file\"" })
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(value);
        }

        foreach (var feature in disabledFeatures)
        {
            start.ArgumentList.Add("--disable");
            start.ArgumentList.Add(feature);
        }


        if (revqora is not null)
        {
            AddOverride("mcp_servers.revqora.enabled=true");
            AddOverride("mcp_servers.revqora.command=" + JsonSerializer.Serialize(revqora));
            AddOverride("mcp_servers.revqora.args=[\"--mcp\"]");
            AddOverride("mcp_servers.revqora.enabled_tools=" + JsonSerializer.Serialize(CodexSession.RevqoraTools));
            AddOverride("mcp_servers.revqora.required=true");
        }

        void AddOverride(string value)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(value);
        }

        var temporary = Path.Combine(home, "tmp");
        Directory.CreateDirectory(temporary);
        start.Environment["CODEX_HOME"] = home;
        start.Environment["CODEX_SQLITE_HOME"] = home;
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;

        foreach (var variable in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "ACCESS_TOKEN", "OPENAI_BASE_URL" })
        {
            start.Environment.Remove(variable);
        }

        return Start(start);
    }

    internal static AppServerConnection Start(ProcessStartInfo start) =>
        new(Process.Start(start) ?? throw new IOException("Could not start Codex App Server."));

    public IAsyncEnumerable<JsonElement> ReadEventsAsync(CancellationToken ct = default) =>
        _events.Reader.ReadAllAsync(ct);

    public Task InitializeAsync(CancellationToken ct = default) => InitializeAsync(ct, experimental: false);

    public async Task InitializeAsync(CancellationToken ct, bool experimental)
    {
        await RequestAsync("initialize", new
        {
            clientInfo = new { name = "voiceagent", title = "VoiceAgent", version = "0.1.0" },
            capabilities = new { experimentalApi = experimental },
        }, ct).ConfigureAwait(false);

        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendAsync(new { method = "initialized" }, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await _requestLock.WaitAsync(token).ConfigureAwait(false);
        var request = new PendingRequest(Interlocked.Increment(ref _id));
        try
        {
            Volatile.Write(ref _pending, request);
            await SendAsync(new { id = request.Id, method, @params = parameters }, token).ConfigureAwait(false);
            return await request.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Codex App Server did not answer {method} in time.");
        }
        finally
        {
            Volatile.Write(ref _pending, null);
            _requestLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            if (!_process.HasExited)
            {
                TerminateProcess();
            }

            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.WhenAll(_readTask, _stderrTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation also stops the stderr drain.
        }
        finally
        {
            _process.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task ReadAsync()
    {
        Exception failure = new IOException("Codex App Server closed its output.");
        try
        {
            await foreach (var line in JsonLineReader.ReadAsync(_process.StandardOutput, _lifetime.Token).ConfigureAwait(false))
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Expected a JSON-RPC object from Codex.");
                }

                if (message.TryGetProperty("method", out var method))
                {
                    if (message.TryGetProperty("id", out var serverId))
                    {
                        await DenyServerRequestAsync(method.ValueKind == JsonValueKind.String ? method.GetString() : null, serverId).ConfigureAwait(false);
                    }
                    else if (!_events.Writer.TryWrite(message.Clone()))
                    {
                        throw new IOException("Codex App Server event buffer is full.");
                    }
                }
                else if (Volatile.Read(ref _pending) is { } request &&
                    message.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number == request.Id)
                {
                    if (message.TryGetProperty("result", out var result))
                    {
                        request.Completion.TrySetResult(result.Clone());
                    }
                    else
                    {
                        request.Completion.TrySetException(new AppServerRpcException(message));
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            failure = error is JsonException ? new InvalidDataException("Invalid JSON from Codex App Server.") : error;
        }
        finally
        {
            Volatile.Read(ref _pending)?.Completion.TrySetException(failure);
            _events.Writer.TryComplete(failure);
            if (!_lifetime.IsCancellationRequested && !_process.HasExited)
            {
                TerminateProcess();
            }
        }
    }

    private async Task DenyServerRequestAsync(string? method, JsonElement id)
    {
        var (category, denial) = method switch
        {
            "item/commandExecution/requestApproval" => ("Command execution", (object)new { decision = "cancel" }),
            "item/fileChange/requestApproval" => ("File change", new { decision = "cancel" }),
            "item/permissions/requestApproval" => ("Permission expansion", new { permissions = new { }, scope = "turn" }),
            "mcpServer/elicitation/request" => ("MCP confirmation", new { action = "decline" }),
            _ => ("Unrecognized server request", null),
        };

        if (!_events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { voiceAgentBlocked = category })))
            throw new IOException("Codex App Server event buffer is full.");

        if (denial is null || id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            throw new InvalidDataException("Trayvora stopped an unsupported server request.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        // Serialize the wire write only. An outstanding RPC may be waiting for this denial.
        await SendAsync(new { id, result = denial }, timeout.Token).ConfigureAwait(false);
    }

    internal void Abort()
    {
        try { TerminateProcess(); }
        catch (System.ComponentModel.Win32Exception) when (_process.HasExited) { }
    }

    private void TerminateProcess()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (_process.HasExited)
        {
            // The server exited before the kill reached it.
        }
    }

    private async Task SendAsync<T>(T data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private sealed record PendingRequest(int Id)
    {
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class AppServerRpcException(JsonElement response) : IOException("Codex App Server rejected the request.")
{
    public bool ThreadMissing { get; } = IsThreadMissing(response);

    private static bool IsThreadMissing(JsonElement response) =>
        response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
        error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
        code.TryGetInt32(out var value) && value == -32600 &&
        error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
        (message.GetString()!.StartsWith("no rollout found for thread", StringComparison.OrdinalIgnoreCase)
            || message.GetString()!.StartsWith("thread not found", StringComparison.OrdinalIgnoreCase));
}
