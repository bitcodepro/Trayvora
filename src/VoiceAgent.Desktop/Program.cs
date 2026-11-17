using System.Security.Cryptography;
using System.Text;
using Avalonia;

namespace VoiceAgent.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A staged copy of a newer version replaces the program files, then starts the updated app.
        if (args.Length == 3 && args[0] == PortableUpdate.ApplyArgument && int.TryParse(args[2], out var oldProcess))
        {
            PortableUpdate.Apply(args[1], oldProcess);
            return;
        }

        var installation = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).ToUpperInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation)));
        using var instance = new Mutex(initiallyOwned: true, @"Local\Trayvora-" + id, out var firstInstance);
        if (!firstInstance) return;

        // Folder layout, private Codex profile and codex.exe discovery (formerly Trayvora-Launch.ps1).
        if (OperatingSystem.IsWindows()) PortableEnvironment.Apply(AppContext.BaseDirectory);
        PortableUpdate.CleanUp(AppContext.BaseDirectory);
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }
}
