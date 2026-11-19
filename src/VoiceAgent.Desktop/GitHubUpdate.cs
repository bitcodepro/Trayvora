using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceAgent.Desktop;

/// <summary>A newer Trayvora release found on GitHub.</summary>
internal sealed record AvailableUpdate(Version Version, string ZipUrl, string Sha256, string PageUrl);

/// <summary>
/// Finds the latest Trayvora release on GitHub (bitcodepro/Trayvora) and downloads its portable zip,
/// checking the SHA-256 published in the release's release.json before anything is installed.
/// </summary>
internal static class GitHubUpdate
{
    public const string Repository = "bitcodepro/Trayvora";
    public const string ZipName = "trayvora-win-x64.zip";
    private const string ManifestName = "release.json";
    private static readonly HttpClient Http = CreateClient();

    /// <summary>The installed version from release.json next to Trayvora.exe; null for builds that were not released.</summary>
    public static Version? CurrentVersion(string? appDirectory = null)
    {
        try
        {
            var file = Path.Combine(appDirectory ?? AppContext.BaseDirectory, ManifestName);
            if (!File.Exists(file)) return null;
            return JsonNode.Parse(File.ReadAllText(file))?["version"]?.GetValue<string>() is { } text && Version.TryParse(text, out var version) ? version : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="latest"/> should be offered over <paramref name="current"/> (unknown counts as older).</summary>
    internal static bool IsNewer(Version latest, Version? current) => current is null || latest > current;

    /// <summary>Returns the latest release if it is newer than this copy, otherwise null.</summary>
    public static async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellation = default)
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellation).ConfigureAwait(false);
        var release = Parse(json);
        if (release is null) throw new InvalidDataException("The release has no Windows package or manifest.");
        var (zipUrl, manifestUrl, page) = release.Value;
        var manifest = JsonNode.Parse(await Http.GetStringAsync(manifestUrl, cancellation).ConfigureAwait(false));
        var versionText = manifest?["version"]?.GetValue<string>();
        var sha = manifest?["sha256"]?.GetValue<string>();
        if (sha?.Length != 64 || !sha.All(Uri.IsHexDigit) || !Version.TryParse(versionText, out var version)) throw new InvalidDataException("Invalid release manifest.");
        return IsNewer(version, CurrentVersion()) ? new AvailableUpdate(version, zipUrl, sha, page) : null;
    }

    /// <summary>Reads the zip and release.json download links from a GitHub "latest release" reply.</summary>
    internal static (string Zip, string Manifest, string Page)? Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject release || release["assets"] is not JsonArray assets) return null;
        string? Url(string name) => assets.OfType<JsonObject>()
            .FirstOrDefault(asset => asset["name"]?.GetValue<string>() == name)?["browser_download_url"]?.GetValue<string>();
        var zip = Url(ZipName);
        var manifest = Url(ManifestName);
        if (zip is null || manifest is null || !IsGitHub(zip) || !IsGitHub(manifest)) return null;
        return (zip, manifest, release["html_url"]?.GetValue<string>() ?? $"https://github.com/{Repository}/releases/latest");
    }

    private static bool IsGitHub(string url) =>
        url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the update zip into <paramref name="folder"/> and returns its path once the SHA-256 matches.</summary>
    public static Task<string> DownloadAsync(AvailableUpdate update, string folder, IProgress<double>? progress = null, CancellationToken cancellation = default) =>
        DownloadAsync(Http, update, folder, progress, cancellation);

    internal static async Task<string> DownloadAsync(HttpClient http, AvailableUpdate update, string folder, IProgress<double>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ZipName);
        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var response = await http.GetAsync(update.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long done = 0;
                int read, lastPercent = -1;
                while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0)
                    {
                        var share = Math.Clamp((double)done / total.Value, 0, 1);
                        if ((int)(100 * share) != lastPercent) progress?.Report(share);
                        lastPercent = (int)(100 * share);
                    }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update failed its checksum.");
            File.Move(temporary, path, true);
            return path;
        }
        finally { File.Delete(temporary); }
    }

    internal static bool HashMatches(string file, string expected)
    {
        using var stream = File.OpenRead(file);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var version = CurrentVersion()?.ToString() ?? "dev";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Trayvora", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
