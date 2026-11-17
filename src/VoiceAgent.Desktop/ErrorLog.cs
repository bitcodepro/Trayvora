namespace VoiceAgent.Desktop;

/// <summary>Appends unexpected UI errors (type, message and stack trace only) to settings\logs\errors.log.</summary>
internal static class ErrorLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string File => Path.Combine(
        Environment.GetEnvironmentVariable("VOICEAGENT_SETTINGS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "settings"),
        "logs", "errors.log");

    public static void Write(Exception error)
    {
        try
        {
            lock (Gate)
            {
                var file = File;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (System.IO.File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                    System.IO.File.Move(file, file + ".old", overwrite: true);
                System.IO.File.AppendAllText(file, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {error}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }
}
