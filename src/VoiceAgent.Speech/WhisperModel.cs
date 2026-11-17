using System.Security.Cryptography;

namespace VoiceAgent.Speech;

public sealed class WhisperModel(string path)
{
    public const int Size = 147951465;
    private const string Hash = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
    private const string BaseUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin";
    private (int Bytes, string Digest, string Url) Descriptor => System.IO.Path.GetFileName(Path) switch
    {
        "ggml-small.bin" => (487601967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", ModelUrl("ggml-small.bin")),
        "ggml-medium.bin" => (1533763059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208", ModelUrl("ggml-medium.bin")),
        "ggml-large-v3-turbo.bin" => (1624555275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69", ModelUrl("ggml-large-v3-turbo.bin")),
        _ => (Size, Hash, BaseUrl),
    };

    private static string ModelUrl(string filename) => BaseUrl.Replace("ggml-base.bin", filename, StringComparison.Ordinal);

    private static readonly HttpClient Client = new();

    public string Path { get; } = path;
    public bool Exists => File.Exists(Path);

    public async Task<FileStream> OpenVerifiedAsync(CancellationToken ct)
    {
        var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            if (stream.Length != Descriptor.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream, ct))
                    .Equals(Descriptor.Digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new WhisperModelVerificationException();
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public async Task DownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        var folder = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("The model path must be absolute.");

        var volume = System.IO.Path.GetPathRoot(folder);

        Directory.CreateDirectory(folder);
        if (volume is not null && new DriveInfo(volume).AvailableFreeSpace < Descriptor.Bytes)
        {
            throw new IOException($"Free at least {Math.Ceiling(Descriptor.Bytes / 1e6)} MB for this Whisper model.");
        }

        var partial = Path + "." + Guid.NewGuid().ToString("N") + ".part";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            using (var response = await Client.GetAsync(Descriptor.Url, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                var total = 0;
                for (var count = await input.ReadAsync(buffer, deadline.Token); count > 0;
                     count = await input.ReadAsync(buffer, deadline.Token))
                {
                    total += count;
                    if (total > Descriptor.Bytes)
                    {
                        throw new InvalidDataException("The Whisper model exceeds its expected size.");
                    }

                    var data = buffer.AsMemory(0, count);
                    hash.AppendData(data.Span);
                    await output.WriteAsync(data, deadline.Token);
                    progress.Report((double)total / Descriptor.Bytes);
                }

                if (total != Descriptor.Bytes || !Convert.ToHexString(hash.GetHashAndReset())
                        .Equals(Descriptor.Digest, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The Whisper model download failed verification.");
                }
            }

            ct.ThrowIfCancellationRequested();
            File.Move(partial, Path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }
}

public sealed class WhisperModelVerificationException() : IOException("The Whisper model failed verification. Download it again.");
