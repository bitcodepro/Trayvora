using Xunit;

namespace VoiceAgent.UnitTests;

/// <summary>
/// Tests that change process-wide environment variables (TEMP, LOCALAPPDATA, VOICEAGENT_*) run alone,
/// so parallel tests never create their temporary folders inside a redirected location.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
