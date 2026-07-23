using System.Text.Json;
using System.IO;

namespace AshaLive;

/// <summary>
/// Stores the uncurated conversation of an explicitly started ASHA session.
/// It deliberately lives beside the local ledger rather than in the project
/// repository or a cloud provider. The ledger retains semantic references;
/// this file retains the actual words a person and ASHA exchanged.
/// </summary>
public static class SessionTranscriptStore
{
    public static Task AppendAsync(string sessionId, ConversationMessage message)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "asha", "sessions", $"{sessionId}.conversation.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var entry = new
        {
            id = $"message-{Guid.NewGuid():N}",
            at = message.Timestamp.ToUniversalTime().ToString("O"),
            speaker = message.Speaker,
            text = message.Text,
        };
        return File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    public static async Task<IReadOnlyList<ConversationMessage>> ReadAsync(string sessionId)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "asha", "sessions", $"{sessionId}.conversation.jsonl");
        if (!File.Exists(path)) return [];

        var messages = new List<ConversationMessage>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var speaker = root.TryGetProperty("speaker", out var speakerValue) ? speakerValue.GetString() : null;
                var text = root.TryGetProperty("text", out var textValue) ? textValue.GetString() : null;
                var timestamp = root.TryGetProperty("at", out var timestampValue) && DateTime.TryParse(timestampValue.GetString(), out var parsed)
                    ? parsed.ToLocalTime()
                    : DateTime.Now;
                if (!string.IsNullOrWhiteSpace(speaker) && !string.IsNullOrWhiteSpace(text)) messages.Add(new ConversationMessage(timestamp, speaker, text));
            }
            catch
            {
                // The main session remains usable even when an old transcript
                // line was interrupted by a system shutdown.
            }
        }
        return messages;
    }
}
