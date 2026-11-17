using System.Buffers.Binary;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceAgent.Application;

namespace VoiceAgent.Speech;

[SupportedOSPlatform("windows")]
public sealed class WasapiRecorder : IAudioRecorder
{
    public IReadOnlyList<Microphone> ListMicrophones()
    {
        using var devices = new MMDeviceEnumerator();
        var result = new List<Microphone>();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new Microphone(device.ID, device.FriendlyName));
            }
        }

        return result;
    }

    public Task<int> RecordAsync(string? deviceId, byte[] pcm, IProgress<double> peak,
        CancellationToken stop, CancellationToken cancel) => RecordCoreAsync(deviceId, pcm, peak, stop, cancel, loopback: false);

    /// <summary>
    /// Records what plays on the computer. Every active output device is recorded at once (calls often play
    /// through a headset that is not the default device) and the loudest recording is returned.
    /// </summary>
    public async Task<int> RecordOutputAsync(byte[] pcm, IProgress<double> peak, CancellationToken stop, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var ids = new List<string>();
        using (var devices = new MMDeviceEnumerator())
        {
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device) ids.Add(device.ID);
        }
        if (ids.Count <= 1) return await RecordCoreAsync(ids.FirstOrDefault(), pcm, peak, stop, cancel, loopback: true).ConfigureAwait(false);
        var levels = new double[ids.Count];
        return await RecordLoudestAsync(ids.Count, (index, buffer) => RecordCoreAsync(ids[index], buffer, new Progress<double>(level =>
        {
            levels[index] = level;
            peak.Report(levels.Max());
        }), stop, cancel, loopback: true), pcm, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="count"/> recordings into separate buffers and copies the loudest successful one into
    /// <paramref name="pcm"/>. Failed devices never win; if every device failed the first failure is thrown.
    /// Every extra buffer is cleared afterwards, also on cancellation and errors.
    /// </summary>
    public static async Task<int> RecordLoudestAsync(int count, Func<int, byte[], Task<int>> record, byte[] pcm, CancellationToken cancel)
    {
        var buffers = Enumerable.Range(0, count).Select(_ => new byte[pcm.Length]).ToArray();
        try
        {
            var runs = buffers.Select((buffer, index) => record(index, buffer)).ToArray();
            try { await Task.WhenAll(runs).ConfigureAwait(false); }
            catch (Exception) when (!cancel.IsCancellationRequested && runs.Any(run => run.IsCompletedSuccessfully)) { }
            cancel.ThrowIfCancellationRequested();
            var usable = Enumerable.Range(0, count).Where(index => runs[index].IsCompletedSuccessfully).ToArray();
            if (usable.Length == 0) await runs[0].ConfigureAwait(false);
            var best = usable[Loudest(usable.Select(index => buffers[index]).ToArray(), usable.Select(index => runs[index].Result).ToArray())];
            var recorded = runs[best].Result;
            buffers[best].AsSpan(0, recorded).CopyTo(pcm);
            return recorded;
        }
        finally
        {
            foreach (var buffer in buffers) System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }
    /// <summary>Index of the recording with the most sound energy (16-bit mono PCM).</summary>
    public static int Loudest(IReadOnlyList<byte[]> buffers, IReadOnlyList<int> counts)
    {
        var best = 0;
        double bestEnergy = -1;
        for (var index = 0; index < buffers.Count; index++)
        {
            double energy = 0;
            var samples = buffers[index].AsSpan(0, counts[index] & ~1);
            for (var offset = 0; offset < samples.Length; offset += 2)
            {
                double value = BinaryPrimitives.ReadInt16LittleEndian(samples[offset..]);
                energy += value * value;
            }
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                best = index;
            }
        }
        return best;
    }

    private static async Task<int> RecordCoreAsync(string? deviceId, byte[] pcm, IProgress<double> peak,
        CancellationToken stop, CancellationToken cancel, bool loopback)
    {
        cancel.ThrowIfCancellationRequested();
        using var devices = new MMDeviceEnumerator();
        using var device = deviceId is null
            ? devices.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture, Role.Console)
            : devices.GetDevice(deviceId);
        // This capture API lets Windows negotiate and convert the client stream to 16 kHz mono PCM.
#pragma warning disable CS0618
        using WasapiCapture capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        capture.WaveFormat = new WaveFormat(VoiceCapture.SampleRate, 16, 1);

#pragma warning restore CS0618
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bytes = 0;
        capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                finished.TrySetException(args.Exception);
            }
            else
            {
                finished.TrySetResult();
            }
        };
        capture.DataAvailable += (_, args) =>
        {
            var count = Math.Min(args.BytesRecorded, pcm.Length - bytes) & ~1;
            var packet = args.Buffer.AsSpan(0, count);
            packet.CopyTo(pcm.AsSpan(bytes));
            bytes += count;

            var maximum = 0;
            for (var offset = 0; offset < packet.Length; offset += 2)
            {
                maximum = Math.Max(maximum, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(packet[offset..])));
            }

            peak.Report(maximum / 32768d);
            if (bytes >= pcm.Length)
            {
                capture.StopRecording();
            }
        };

        if (stop.IsCancellationRequested)
        {
            return 0;
        }

        capture.StartRecording();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(VoiceCapture.MaxSeconds));
        using var stopRegistration = stop.Register(capture.StopRecording);
        using var cancelRegistration = cancel.Register(capture.StopRecording);
        using var timeoutRegistration = timeout.Token.Register(capture.StopRecording);
        await finished.Task.ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        return bytes;
    }
}
