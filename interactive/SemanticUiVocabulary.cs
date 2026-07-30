using System.Globalization;
using System.Text;

namespace AshaLive;

/// <summary>
/// Canonicalizes a small set of common human-facing UI concepts across
/// languages. This is a general grounding vocabulary, not an application
/// recipe: it never decides what to click and only helps independently visible
/// labels such as "Inbox" and "Posteingang" compare as the same concept.
/// </summary>
internal static class SemanticUiVocabulary
{
    private static readonly IReadOnlyDictionary<string, string> CanonicalTokens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["inbox"] = "inbox",
            ["posteingang"] = "inbox",
            ["draft"] = "drafts",
            ["drafts"] = "drafts",
            ["entwurf"] = "drafts",
            ["entwurfe"] = "drafts",
            ["sent"] = "sent",
            ["sentitems"] = "sent",
            ["gesendet"] = "sent",
            ["gesendete"] = "sent",
            ["postausgang"] = "outbox",
            ["outbox"] = "outbox",
            ["junk"] = "junk",
            ["junkemail"] = "junk",
            ["spam"] = "junk",
            ["deleted"] = "trash",
            ["deleteditems"] = "trash",
            ["trash"] = "trash",
            ["papierkorb"] = "trash",
            ["geloscht"] = "trash",
            ["geloschte"] = "trash",
            ["archive"] = "archive",
            ["archiv"] = "archive",
            ["account"] = "account",
            ["konto"] = "account",
            ["settings"] = "settings",
            ["setting"] = "settings",
            ["einstellungen"] = "settings",
            ["search"] = "search",
            ["suchen"] = "search",
        };

    public static string CanonicalizeToken(string text)
    {
        var normalized = NormalizeToken(text);
        return CanonicalTokens.TryGetValue(normalized, out var canonical)
            ? canonical
            : normalized;
    }

    public static string CanonicalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || character == 'ß')
            {
                current.Append(character);
                continue;
            }
            Flush();
        }
        Flush();
        return string.Join(' ', tokens);

        void Flush()
        {
            if (current.Length == 0) return;
            var canonical = CanonicalizeToken(current.ToString());
            if (canonical.Length > 0) tokens.Add(canonical);
            current.Clear();
        }
    }

    public static string NormalizeToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var decomposed = text
            .Replace("ß", "ss", StringComparison.OrdinalIgnoreCase)
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
