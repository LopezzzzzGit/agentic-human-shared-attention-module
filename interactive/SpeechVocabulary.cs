using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AshaLive;

internal enum SpeechVocabularyScope
{
    Global,
    Profile,
    Project,
    Session,
}

internal sealed record SpeechVocabularyEntry(
    string Id,
    string Canonical,
    IReadOnlyList<string> Aliases,
    SpeechVocabularyScope Scope,
    string? ScopeId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastConfirmedAtUtc);

internal sealed record SpeechVocabularyContext(
    string? ProfileId = null,
    string? ProjectId = null,
    string? SessionId = null);

internal sealed record SpeechRecognitionBias(string Hotwords, string InitialPrompt)
{
    public static SpeechRecognitionBias Empty { get; } = new(string.Empty, string.Empty);
    public bool IsEmpty => Hotwords.Length == 0 && InitialPrompt.Length == 0;
}

internal sealed record SpeechVocabularyChange(
    string EntryId,
    string Alias,
    string Canonical,
    SpeechVocabularyScope Scope);

internal sealed record SpeechVocabularyNormalization(
    string RawText,
    string ResolvedText,
    IReadOnlyList<SpeechVocabularyChange> Changes)
{
    public bool Changed => Changes.Count > 0;
}

/// <summary>
/// Local, explicitly taught speech vocabulary. It contains no bundled words
/// and never infers or persists an alias without a caller-confirmed entry.
/// </summary>
internal sealed class SpeechVocabularyStore
{
    private const int MaximumEntries = 2_000;
    private const int MaximumAliasesPerEntry = 12;
    private const int MaximumTermLength = 160;
    private const int MaximumBiasTerms = 40;
    private const int MaximumHotwordCharacters = 600;
    private readonly object _gate = new();
    private readonly string _path;
    private List<SpeechVocabularyEntry> _entries;

    private SpeechVocabularyStore(string path, List<SpeechVocabularyEntry> entries)
    {
        _path = path;
        _entries = entries;
    }

    public static SpeechVocabularyStore LoadDefault() => Load(VocabularyPath());

    internal static SpeechVocabularyStore Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SpeechVocabularyStore(path, []);
            var document = JsonSerializer.Deserialize<SpeechVocabularyDocument>(
                File.ReadAllText(path),
                JsonOptions);
            var entries = (document?.Entries ?? [])
                .Select(NormalizeEntry)
                .Where(entry => entry is not null)
                .Cast<SpeechVocabularyEntry>()
                .Take(MaximumEntries)
                .ToList();
            return new SpeechVocabularyStore(path, entries);
        }
        catch
        {
            // A damaged optional vocabulary must never prevent ASHA starting.
            return new SpeechVocabularyStore(path, []);
        }
    }

    public IReadOnlyList<SpeechVocabularyEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    public SpeechVocabularyEntry Confirm(
        string canonical,
        IEnumerable<string>? aliases,
        SpeechVocabularyScope scope,
        string? scopeId = null,
        string? entryId = null)
    {
        var canonicalValue = ValidateTerm(canonical, nameof(canonical));
        var normalizedScopeId = NormalizeScopeId(scope, scopeId);
        var aliasValues = (aliases ?? [])
            .Select(alias => alias?.Trim() ?? string.Empty)
            .Where(alias => alias.Length > 0)
            .Select(alias => ValidateTerm(alias, nameof(aliases)))
            .Where(alias => !string.Equals(
                NormalizePhrase(alias),
                NormalizePhrase(canonicalValue),
                StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumAliasesPerEntry)
            .ToArray();
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var existingIndex = -1;
            if (!string.IsNullOrWhiteSpace(entryId))
                existingIndex = _entries.FindIndex(item => string.Equals(item.Id, entryId, StringComparison.Ordinal));
            if (existingIndex < 0)
            {
                existingIndex = _entries.FindIndex(item =>
                    item.Scope == scope &&
                    string.Equals(item.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
                    string.Equals(
                        NormalizePhrase(item.Canonical),
                        NormalizePhrase(canonicalValue),
                        StringComparison.Ordinal));
            }

            var entry = new SpeechVocabularyEntry(
                existingIndex >= 0 ? _entries[existingIndex].Id : $"speech-word-{Guid.NewGuid():N}",
                canonicalValue,
                aliasValues,
                scope,
                normalizedScopeId,
                existingIndex >= 0 ? _entries[existingIndex].CreatedAtUtc : now,
                now);
            if (existingIndex >= 0)
                _entries[existingIndex] = entry;
            else
            {
                if (_entries.Count >= MaximumEntries)
                    throw new InvalidOperationException("The local speech vocabulary has reached its entry limit.");
                _entries.Add(entry);
            }
            SaveLocked();
            return entry;
        }
    }

    public bool Delete(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return false;
        lock (_gate)
        {
            var removed = _entries.RemoveAll(item => string.Equals(item.Id, entryId, StringComparison.Ordinal)) > 0;
            if (removed) SaveLocked();
            return removed;
        }
    }

    public IReadOnlyList<SpeechVocabularyEntry> Select(SpeechVocabularyContext context, int maximumEntries = MaximumBiasTerms)
    {
        maximumEntries = Math.Clamp(maximumEntries, 0, MaximumBiasTerms);
        lock (_gate)
        {
            return _entries
                .Where(entry => AppliesTo(entry, context))
                .OrderBy(entry => ScopePriority(entry.Scope))
                .ThenByDescending(entry => entry.LastConfirmedAtUtc)
                .Take(maximumEntries)
                .ToArray();
        }
    }

    public SpeechRecognitionBias BuildRecognitionBias(SpeechVocabularyContext context)
    {
        var selected = Select(context);
        if (selected.Count == 0) return SpeechRecognitionBias.Empty;

        var terms = new List<string>();
        foreach (var entry in selected)
        {
            Add(entry.Canonical);
            foreach (var alias in entry.Aliases) Add(alias);
        }
        var hotwords = BoundedJoin(terms, ", ", MaximumHotwordCharacters);
        if (hotwords.Length == 0) return SpeechRecognitionBias.Empty;
        var promptTerms = BoundedJoin(selected.Select(entry => entry.Canonical), ", ", 420);
        return new SpeechRecognitionBias(
            hotwords,
            promptTerms.Length == 0 ? string.Empty : $"Likely names and domain terms: {promptTerms}.");

        void Add(string value)
        {
            if (terms.Count >= MaximumBiasTerms) return;
            if (terms.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
            terms.Add(value);
        }
    }

    public SpeechVocabularyNormalization NormalizeConfirmedAliases(
        string rawText,
        SpeechVocabularyContext context)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new SpeechVocabularyNormalization(rawText, rawText, []);

        var resolved = rawText;
        var changes = new List<SpeechVocabularyChange>();
        SpeechVocabularyEntry[] applicableEntries;
        lock (_gate)
        {
            applicableEntries = _entries
                .Where(entry => AppliesTo(entry, context))
                .OrderBy(entry => ScopePriority(entry.Scope))
                .ThenByDescending(entry => entry.LastConfirmedAtUtc)
                .ToArray();
        }
        var aliases = applicableEntries
            .SelectMany(entry => entry.Aliases.Select(alias => new { Entry = entry, Alias = alias }))
            .OrderByDescending(item => item.Alias.Length)
            .ToArray();
        foreach (var item in aliases)
        {
            var pattern =
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(item.Alias).Replace(@"\ ", @"\s+")}(?![\p{{L}}\p{{N}}])";
            var replaced = false;
            resolved = Regex.Replace(
                resolved,
                pattern,
                _ =>
                {
                    replaced = true;
                    return item.Entry.Canonical;
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (replaced)
                changes.Add(new SpeechVocabularyChange(
                    item.Entry.Id,
                    item.Alias,
                    item.Entry.Canonical,
                    item.Entry.Scope));
        }
        return new SpeechVocabularyNormalization(rawText, resolved, changes);
    }

    private static bool AppliesTo(SpeechVocabularyEntry entry, SpeechVocabularyContext context) =>
        entry.Scope switch
        {
            SpeechVocabularyScope.Global => true,
            SpeechVocabularyScope.Profile =>
                !string.IsNullOrWhiteSpace(context.ProfileId) &&
                string.Equals(entry.ScopeId, context.ProfileId, StringComparison.Ordinal),
            SpeechVocabularyScope.Project =>
                !string.IsNullOrWhiteSpace(context.ProjectId) &&
                string.Equals(entry.ScopeId, context.ProjectId, StringComparison.Ordinal),
            SpeechVocabularyScope.Session =>
                !string.IsNullOrWhiteSpace(context.SessionId) &&
                string.Equals(entry.ScopeId, context.SessionId, StringComparison.Ordinal),
            _ => false,
        };

    private static int ScopePriority(SpeechVocabularyScope scope) => scope switch
    {
        SpeechVocabularyScope.Session => 0,
        SpeechVocabularyScope.Project => 1,
        SpeechVocabularyScope.Profile => 2,
        _ => 3,
    };

    private static string? NormalizeScopeId(SpeechVocabularyScope scope, string? scopeId)
    {
        if (scope == SpeechVocabularyScope.Global) return null;
        var value = scopeId?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTermLength)
            throw new ArgumentException("Profile, project, and session vocabulary require a bounded scope ID.", nameof(scopeId));
        return value;
    }

    private static string ValidateTerm(string value, string parameter)
    {
        var trimmed = Regex.Replace(value?.Trim() ?? string.Empty, @"\s+", " ");
        if (trimmed.Length is < 1 or > MaximumTermLength ||
            trimmed.Any(character => char.IsControl(character)))
            throw new ArgumentException($"Speech vocabulary terms must contain 1 to {MaximumTermLength} printable characters.", parameter);
        return trimmed;
    }

    private static SpeechVocabularyEntry? NormalizeEntry(SpeechVocabularyEntry? entry)
    {
        if (entry is null) return null;
        try
        {
            var canonical = ValidateTerm(entry.Canonical, nameof(entry.Canonical));
            var scopeId = NormalizeScopeId(entry.Scope, entry.ScopeId);
            var aliases = (entry.Aliases ?? [])
                .Select(value => ValidateTerm(value, nameof(entry.Aliases)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumAliasesPerEntry)
                .ToArray();
            return entry with
            {
                Id = string.IsNullOrWhiteSpace(entry.Id) ? $"speech-word-{Guid.NewGuid():N}" : entry.Id,
                Canonical = canonical,
                Aliases = aliases,
                ScopeId = scopeId,
            };
        }
        catch
        {
            return null;
        }
    }

    internal static string NormalizePhrase(string value)
    {
        var decomposed = value
            .Replace("ß", "ss", StringComparison.OrdinalIgnoreCase)
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString();
    }

    private static string BoundedJoin(IEnumerable<string> values, string separator, int maximumCharacters)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var addition = builder.Length == 0 ? value : separator + value;
            if (builder.Length + addition.Length > maximumCharacters) break;
            builder.Append(addition);
        }
        return builder.ToString();
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        var document = new SpeechVocabularyDocument(1, _entries);
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, _path, true);
    }

    private static string VocabularyPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "asha",
        "speech-vocabulary.json");

    private sealed record SpeechVocabularyDocument(int Version, IReadOnlyList<SpeechVocabularyEntry> Entries);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
