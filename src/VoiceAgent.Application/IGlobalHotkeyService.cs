namespace VoiceAgent.Application;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? Pressed;

    HotkeyRegistration Start();
}

public readonly record struct HotkeyRegistration(bool Registered, int ErrorCode)
{
    public const int AlreadyRegistered = 1409;
}
