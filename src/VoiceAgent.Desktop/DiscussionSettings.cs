namespace VoiceAgent.Desktop;

/// <summary>Session options saved in settings\discussion.json.</summary>
internal sealed record DiscussionOptions(int Rounds = DiscussionOptions.DefaultRounds)
{
    public const int DefaultRounds = 5;
    public const int MinRounds = 1;
    public const int MaxRounds = 10;
}

internal sealed class DiscussionSettings(string directory)
{
    private string FilePath => Path.Combine(directory, "discussion.json");

    /// <summary>How many times the lead may hand the plan to the other agent in a "discuss with …" request.</summary>
    public int Rounds
    {
        get
        {
            try
            {
                var saved = SettingsFile.Read<DiscussionOptions>(FilePath);
                return Math.Clamp(saved?.Rounds ?? DiscussionOptions.DefaultRounds, DiscussionOptions.MinRounds, DiscussionOptions.MaxRounds);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return DiscussionOptions.DefaultRounds;
            }
        }
    }

    public void SaveRounds(int rounds) =>
        SettingsFile.Write(FilePath, new DiscussionOptions(Math.Clamp(rounds, DiscussionOptions.MinRounds, DiscussionOptions.MaxRounds)));
}
