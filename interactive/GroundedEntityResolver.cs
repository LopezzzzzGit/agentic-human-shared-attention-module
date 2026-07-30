namespace AshaLive;

internal enum GroundedEntityResolutionKind
{
    None,
    HighConfidence,
    Clarification,
    Ambiguous,
}

internal sealed record GroundedEntityCandidate(
    string Value,
    string? Role = null,
    string? Container = null,
    IReadOnlyList<string>? ConfirmedAliases = null);

internal sealed record GroundedEntityResolution(
    GroundedEntityResolutionKind Kind,
    string Query,
    GroundedEntityCandidate? Candidate,
    double Score,
    double RunnerUpScore,
    IReadOnlyList<GroundedEntityCandidate> Alternatives);

/// <summary>
/// Provider-independent spelling tolerance for references to currently
/// grounded candidates. It proposes a resolution; permission, safety, current
/// UI state, and action verification remain separate requirements.
/// </summary>
internal static class GroundedEntityResolver
{
    private static readonly HashSet<string> GlueTokens = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "to", "at", "in", "on", "of", "for",
        "der", "die", "das", "den", "dem", "ein", "eine", "einen", "einer",
        "im", "in", "am", "auf", "von", "fur",
    };

    public static GroundedEntityResolution Resolve(
        string query,
        IEnumerable<GroundedEntityCandidate> candidates)
    {
        var ranked = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value))
            .Select(candidate => new { Candidate = candidate, Score = Score(query, candidate) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ranked.Length == 0)
            return new GroundedEntityResolution(
                GroundedEntityResolutionKind.None,
                query,
                null,
                0,
                0,
                []);

        var best = ranked[0];
        var runnerUp = ranked.Length > 1 ? ranked[1].Score : 0;
        var margin = best.Score - runnerUp;
        var kind =
            best.Score >= 0.995 && runnerUp < 0.995
                ? GroundedEntityResolutionKind.HighConfidence
                : best.Score >= 0.88 && margin >= 0.08
                ? GroundedEntityResolutionKind.HighConfidence
                : best.Score >= 0.70 && margin >= 0.05
                    ? GroundedEntityResolutionKind.Clarification
                    : best.Score >= 0.70
                        ? GroundedEntityResolutionKind.Ambiguous
                        : best.Score >= 0.60 && margin >= 0.10
                            ? GroundedEntityResolutionKind.Clarification
                            : best.Score >= 0.60
                                ? GroundedEntityResolutionKind.Ambiguous
                                : GroundedEntityResolutionKind.None;
        return new GroundedEntityResolution(
            kind,
            query,
            best.Candidate,
            best.Score,
            runnerUp,
            ranked.Take(3).Select(item => item.Candidate).ToArray());
    }

    internal static double ScorePair(string query, string candidate) =>
        Score(query, new GroundedEntityCandidate(candidate));

    private static double Score(string query, GroundedEntityCandidate candidate)
    {
        var queryPhrase = NormalizePhrase(query);
        var candidatePhrase = NormalizePhrase(candidate.Value);
        if (queryPhrase.Length == 0 || candidatePhrase.Length == 0) return 0;
        if (string.Equals(queryPhrase, candidatePhrase, StringComparison.Ordinal)) return 1;
        if (candidate.ConfirmedAliases is not null &&
            candidate.ConfirmedAliases.Any(alias =>
                string.Equals(queryPhrase, NormalizePhrase(alias), StringComparison.Ordinal)))
            return 0.99;
        if (ContainsWholePhrase(candidatePhrase, queryPhrase)) return 0.96;

        var queryTokens = Tokenize(queryPhrase);
        var candidateTokens = Tokenize(candidatePhrase);
        if (queryTokens.Length == 0 || candidateTokens.Length == 0) return 0;
        if (ContainsWholePhrase(queryPhrase, candidatePhrase))
        {
            // A short OCR fragment contained in a longer requested title is
            // evidence of a partial match, not proof of the whole target.
            // Weight it by how much of the requested identity it actually
            // covers so words such as "Pitch" or "V9" cannot win alone.
            var tokenCoverage = Math.Min(1, candidateTokens.Length / (double)queryTokens.Length);
            var characterCoverage = Math.Min(1, candidatePhrase.Length / (double)queryPhrase.Length);
            return Math.Min(
                0.93,
                0.42 + (tokenCoverage * 0.32) + (characterCoverage * 0.18));
        }

        var similarities = queryTokens
            .Select(queryToken => candidateTokens.Max(candidateToken => TokenSimilarity(queryToken, candidateToken)))
            .ToArray();
        var strongCoverage = similarities.Count(score => score >= 0.72) / (double)similarities.Length;
        var average = similarities.Average();
        if (strongCoverage < 0.5) return average * 0.55;
        var compactness = Math.Min(1, queryTokens.Length / (double)Math.Max(queryTokens.Length, candidateTokens.Length));
        return Math.Clamp((average * 0.88) + (strongCoverage * 0.08) + (compactness * 0.04), 0, 1);
    }

    private static bool ContainsWholePhrase(string value, string phrase) =>
        value.Equals(phrase, StringComparison.Ordinal) ||
        value.StartsWith(phrase + " ", StringComparison.Ordinal) ||
        value.EndsWith(" " + phrase, StringComparison.Ordinal) ||
        value.Contains(" " + phrase + " ", StringComparison.Ordinal);

    private static string[] Tokenize(string phrase)
    {
        var tokens = phrase.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meaningful = tokens
            .Where(token => token.Length > 1 && !GlueTokens.Contains(token))
            .ToArray();
        return meaningful.Length > 0 ? meaningful : tokens;
    }

    private static double TokenSimilarity(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return 1;
        var maximum = Math.Max(left.Length, right.Length);
        if (maximum < 4) return 0;
        var distance = LevenshteinDistance(left, right);
        var similarity = 1 - (distance / (double)maximum);
        if (left[0] != right[0]) similarity -= 0.08;
        return Math.Clamp(similarity, 0, 1);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++) previous[column] = column;
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static string NormalizePhrase(string value) =>
        SemanticUiVocabulary.CanonicalizeText(value);
}
