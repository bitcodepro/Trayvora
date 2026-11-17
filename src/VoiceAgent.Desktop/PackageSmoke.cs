using System.Text.Json;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using Whisper.net;

namespace VoiceAgent.Desktop;

/// <summary>Explicit CI-only probe of the real published executable and its native dependencies.</summary>
internal static class PackageSmoke
{
    public const string Argument = "--verify-package";

    public static int Run(string reportPath)
    {
        var report = new Dictionary<string, object?>();
        var success = false;
        try
        {
            if (!Path.IsPathFullyQualified(reportPath) || File.Exists(reportPath))
                throw new ArgumentException("Use an absolute, new report file path.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The portable package targets Windows.");
            var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath)
                ?? throw new InvalidOperationException("Cannot locate the executable.");
            if (!string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                    Path.TrimEndingDirectorySeparator(executableDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Portable data paths do not resolve beside the executable.");
            report["appDirectory"] = AppContext.BaseDirectory;
#pragma warning disable IL3000 // Deliberately assert that CI is running the bundle, not a loose build.
            var singleFile = string.IsNullOrEmpty(typeof(PackageSmoke).Assembly.Location);
#pragma warning restore IL3000
            report["singleFile"] = singleFile;
            if (!singleFile) throw new InvalidOperationException("The smoke probe must run the published single-file executable.");

            // These calls must resolve native libraries from the package/bundle, not just load managed assemblies.
            using (var bitmap = new SKBitmap(2, 2))
            {
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Red);
                canvas.Flush();
                if (bitmap.GetPixel(0, 0) != SKColors.Red) throw new InvalidOperationException("Skia draw failed.");
                report["skia"] = "draw verified";
            }

            var scratch = Directory.CreateTempSubdirectory("trayvora-package-smoke-");
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(scratch.FullName, "probe.db"), Pooling = false,
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE probe (value INTEGER); INSERT INTO probe VALUES (42); SELECT value FROM probe;";
                if (Convert.ToInt64(command.ExecuteScalar()) != 42) throw new InvalidOperationException("SQLite round trip failed.");
                report["sqlite"] = "file database round trip verified";
            }
            finally { scratch.Delete(recursive: true); }

            var runtime = WhisperFactory.GetRuntimeInfo();
            if (string.IsNullOrWhiteSpace(runtime) || !WhisperFactory.GetSupportedLanguages().Contains("en"))
                throw new InvalidOperationException("Whisper native runtime probe failed.");
            report["whisper"] = runtime;
            success = true;
        }
        catch (Exception error)
        {
            report["error"] = error.ToString();
        }

        report["success"] = success;
        try
        {
            // Never overwrite an existing file, even if the supplied path failed validation above.
            if (!Path.IsPathFullyQualified(reportPath)) return 1;
            using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception) { return 1; }
        return success ? 0 : 1;
    }
}
