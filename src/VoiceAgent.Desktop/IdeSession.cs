using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VoiceAgent.Desktop;

internal static class IdeSession
{
    internal sealed record Options(bool Enabled);
    internal sealed record Target(string Agent, Guid Id);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    public static bool InCode()
    {
        if (!OperatingSystem.IsWindows()) return false;
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName is "Code" or "Code - Insiders" or "VSCodium"; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
    }

    public static async Task<Target?> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var pipe = new NamedPipeClientStream(".", "trayvora-active-agent-v1", PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe);
            var buffer = new char[256];
            var n = await reader.ReadBlockAsync(buffer.AsMemory(), timeout.Token);
            if (n == 0 || n == buffer.Length) return null;
            var value = JsonSerializer.Deserialize<Target>(buffer.AsSpan(0, n), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return value is { Agent: "Claude" or "Codex" } && value.Id != Guid.Empty ? value : null;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Export(string target)
    {
        var manifest = """<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"><Metadata><Identity Language="en-US" Id="trayvora-session" Version="0.1.1" Publisher="bitcodepro"/><DisplayName>Trayvora Active Session</DisplayName><Description xml:space="preserve">Local agent session bridge</Description><Properties><Property Id="Microsoft.VisualStudio.Code.Engine" Value="^1.90.0"/></Properties></Metadata><Installation><InstallationTarget Id="Microsoft.VisualStudio.Code"/></Installation><Dependencies/><Assets><Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true"/></Assets></PackageManifest>""";
        var types = """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="json" ContentType="application/json"/><Default Extension="js" ContentType="application/javascript"/><Default Extension="vsixmanifest" ContentType="text/xml"/></Types>""";
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var name in new[] { "package.json", "extension.js" })
                {
                    using var input = typeof(IdeSession).Assembly.GetManifestResourceStream("Trayvora.IDE." + name)!;
                    using var output = zip.CreateEntry("extension/" + name).Open();
                    input.CopyTo(output);
                }
                foreach (var (name, data) in new[] { ("extension.vsixmanifest", manifest), ("[Content_Types].xml", types) })
                { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(data); }
            }
            File.Move(temp, target, true);
        }
        finally { File.Delete(temp); }
    }
}
