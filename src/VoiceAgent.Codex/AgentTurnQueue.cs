namespace VoiceAgent.Codex;

/// <summary>Serializes Trayvora writers to the same external chat, including sends from different windows.</summary>
public sealed class AgentTurnQueue : IDisposable
{
    private sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users;
    }

    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _key;
    private readonly Slot _slot;
    private bool _disposed;

    private AgentTurnQueue(string key, Slot slot) { _key = key; _slot = slot; }

    public static async Task<AgentTurnQueue> EnterAsync(string profile, Guid id, CancellationToken ct)
    {
        var key = Path.Combine(Path.GetFullPath(profile), id.ToString("N"));
        Slot slot;
        lock (Slots)
        {
            if (!Slots.TryGetValue(key, out var existing)) Slots[key] = slot = new();
            else slot = existing;
            slot.Users++;
        }
        try { await slot.Gate.WaitAsync(ct).ConfigureAwait(false); }
        catch { ReleaseReference(key, slot); throw; }
        return new(key, slot);
    }

    private static void ReleaseReference(string key, Slot slot)
    {
        lock (Slots) if (--slot.Users == 0) { Slots.Remove(key); slot.Gate.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _slot.Gate.Release();
        ReleaseReference(_key, _slot);
    }
}
