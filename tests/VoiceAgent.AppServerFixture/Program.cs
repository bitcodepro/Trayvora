using System.Text.Json;

namespace VoiceAgent.AppServerFixture;

// A subprocess for exercising the real stdio transport without loading a shell or an installed Codex profile.
public static class Program
{
    private static int Main(string[] args) => args is [var scenario] ? scenario switch
    {
        "handshake" => Handshake(),
        "approvals" => VerifyApprovals(),
        "hang" => Hang(),
        "malformed" => MalformedReply(),
        _ => 2,
    } : 2;

    private static int Handshake()
    {
        var initial = Read();
        if (initial.GetProperty("method").GetString() != "initialize") return 1;
        Reply(initial, new { });
        if (Read().GetProperty("method").GetString() != "initialized") return 2;

        var request = Read();
        if (request.GetProperty("method").GetString() != "test/request") return 3;
        Console.Error.WriteLine("diagnostic ignored");
        Console.WriteLine("""{"method":"test/event","params":{"value":42}}""");
        Reply(request, new { ok = true });
        return 0;
    }

    private static int VerifyApprovals()
    {
        var request = Read();
        Console.Error.WriteLine("test-secret-9876");

        Console.WriteLine("""{"id":"cmd","method":"item/commandExecution/requestApproval","params":{"command":"test-secret-9876"}}""");
        var command = Read();
        if (command.GetProperty("id").GetString() != "cmd" || command.GetProperty("result").GetProperty("decision").GetString() != "cancel")
            return 1;

        Console.WriteLine("""{"id":17,"method":"item/fileChange/requestApproval","params":{"reason":"test-secret-9876"}}""");
        var patch = Read();
        if (patch.GetProperty("id").GetInt32() != 17 || patch.GetProperty("result").GetProperty("decision").GetString() != "cancel")
            return 2;

        Console.WriteLine("""{"id":18,"method":"item/permissions/requestApproval","params":{"reason":"test-secret-9876"}}""");
        var permissions = Read();
        var result = permissions.GetProperty("result");
        if (permissions.GetProperty("id").GetInt32() != 18 || result.GetProperty("scope").GetString() != "turn"
            || result.GetProperty("permissions").EnumerateObject().Any())
            return 3;

        Console.WriteLine("""{"id":19,"method":"mcpServer/elicitation/request","params":{"message":"test-secret-9876"}}""");
        var mcp = Read();
        if (mcp.GetProperty("id").GetInt32() != 19 || mcp.GetProperty("result").GetProperty("action").GetString() != "decline")
            return 4;

        Reply(request, new { ok = true });
        return 0;
    }

    private static int Hang()
    {
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static int MalformedReply()
    {
        _ = Read();
        Console.WriteLine("{broken");
        return 0;
    }

    private static JsonElement Read()
    {
        using var json = JsonDocument.Parse(Console.ReadLine() ?? throw new EndOfStreamException());
        return json.RootElement.Clone();
    }

    private static void Reply(JsonElement request, object result) =>
        Console.WriteLine(JsonSerializer.Serialize(new { id = request.GetProperty("id"), result }));
}
