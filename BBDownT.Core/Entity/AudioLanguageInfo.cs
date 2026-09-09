namespace BBDownT.Core.Entity;

/// <summary>A server-provided playback language, selected through cur_language.</summary>
public sealed record AudioLanguageInfo(string Code, string Title, bool IsAi);

/// <summary>The requested playback language is unavailable or was not returned.</summary>
public sealed class AudioLanguageUnavailableException(string message) : InvalidOperationException(message)
{
}
