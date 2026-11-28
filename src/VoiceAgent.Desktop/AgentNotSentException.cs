namespace VoiceAgent.Desktop;

/// <summary>The adapter rejected a request before it could reach the agent.</summary>
internal sealed class AgentNotSentException(string message, Exception? inner = null) : IOException(message, inner);
