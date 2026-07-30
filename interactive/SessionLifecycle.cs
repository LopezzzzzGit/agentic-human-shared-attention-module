namespace AshaLive;

internal enum ActiveSessionRetention
{
    None,
    Retained,
    Temporary,
}

internal enum TemporarySessionDecision
{
    Cancel,
    Keep,
    Discard,
}

/// <summary>
/// Runtime-owned session-boundary rules. These deliberately contain no
/// application names, spoken phrases, or model-authored state.
/// </summary>
internal static class SessionLifecyclePolicy
{
    internal const string TemporaryIdPrefix = "temporary-";

    public static ActiveSessionRetention DefaultNewSessionRetention =>
        ActiveSessionRetention.Retained;

    public static bool RestoreRecentSessionAutomatically => false;

    public static string CreateTemporarySessionId(DateTime now, Guid nonce)
    {
        var value = $"{TemporaryIdPrefix}{now:yyyyMMdd-HHmmss}-{nonce:N}";
        return value[..Math.Min(31, value.Length)];
    }

    public static bool IsTemporarySessionId(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        sessionId.StartsWith(TemporaryIdPrefix, StringComparison.Ordinal) &&
        sessionId.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_');

    public static bool RequiresTemporaryResolution(
        ActiveSessionRetention retention,
        int conversationMessages,
        int bufferedEvents,
        bool hasEvidence) =>
        retention == ActiveSessionRetention.Temporary &&
        (conversationMessages > 0 || bufferedEvents > 1 || hasEvidence);
}
