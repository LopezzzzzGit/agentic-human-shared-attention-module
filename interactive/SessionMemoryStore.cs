using System.IO;
using System.Text.Json;

namespace AshaLive;

/// <summary>
/// Persists only derived model-context memory. The complete conversation stays
/// in SessionTranscriptStore's append-only JSONL and is never shortened or
/// replaced by this summary.
/// </summary>
public static class SessionMemoryStore
{
    // Full transcripts remain append-only on disk. Provider turns carry only
    // a compact recent conversational window; older material is available
    // through the durable session summary.
    public const int RecentCharacterBudget = 8_000;

    public static async Task<SessionMemorySnapshot> ReadAsync(string sessionId)
    {
        var path = MemoryPath(sessionId);
        if (!File.Exists(path)) return new SessionMemorySnapshot(0, string.Empty, DateTime.MinValue);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SessionMemorySnapshot>(stream)
                ?? new SessionMemorySnapshot(0, string.Empty, DateTime.MinValue);
        }
        catch
        {
            return new SessionMemorySnapshot(0, string.Empty, DateTime.MinValue);
        }
    }

    public static async Task WriteAsync(string sessionId, SessionMemorySnapshot snapshot)
    {
        var path = MemoryPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public static SessionMemoryPartition Partition(IReadOnlyList<ConversationMessage> messages)
    {
        var start = messages.Count;
        var characters = 0;
        while (start > 0)
        {
            var next = messages[start - 1].Text.Length + messages[start - 1].Speaker.Length + 8;
            if (characters > 0 && characters + next > RecentCharacterBudget) break;
            characters += next;
            start--;
        }
        return new SessionMemoryPartition(start, messages.Skip(start).ToArray());
    }

    private static string MemoryPath(string sessionId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "asha", "sessions", $"{sessionId}.memory.json");
}

public sealed record SessionMemorySnapshot(int CoveredMessageCount, string Summary, DateTime UpdatedAtUtc);
public sealed record SessionMemoryPartition(int OlderMessageCount, IReadOnlyList<ConversationMessage> RecentMessages);
