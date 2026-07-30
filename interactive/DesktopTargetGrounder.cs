namespace AshaLive;

internal sealed record GroundedDesktopTarget(
    string Source,
    string Name,
    string? Role,
    int X,
    int Y,
    int Width,
    int Height)
{
    public int CenterX => X + (Width / 2);
    public int CenterY => Y + (Height / 2);
}

internal sealed record GroundedDesktopResolution(
    GroundedDesktopTarget? Target,
    GroundedEntityResolutionKind Kind,
    IReadOnlyList<string> Alternatives,
    string Source = "none",
    double Score = 0,
    double RunnerUpScore = 0,
    int CandidateCount = 0)
{
    public bool RequiresClarification =>
        Target is null &&
        Kind is GroundedEntityResolutionKind.Clarification or GroundedEntityResolutionKind.Ambiguous;
}

/// <summary>
/// Resolves a model-proposed point to a target that Windows can independently
/// establish. Accessibility metadata is preferred; local OCR is the fallback.
/// Neither path sends additional desktop data to a provider.
/// </summary>
internal static class DesktopTargetGrounder
{
    private static readonly HashSet<string> DescriptiveTokens = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "for", "in", "inside", "on", "at", "of",
        "button", "link", "menu", "item", "row", "tab", "account", "folder",
        "control", "application", "app",
        "der", "die", "das", "den", "dem", "ein", "eine", "einer", "einen",
        "im", "in", "auf", "von", "für", "fuer", "konto", "ordner",
        "schaltfläche", "schaltflaeche", "element", "zeile", "anwendung",
    };

    public static async Task<GroundedDesktopTarget?> ResolveAsync(
        VisionAttachment vision,
        string targetName,
        string? requestedRole,
        string? containerName,
        int hintImageX,
        int hintImageY,
        CancellationToken cancellationToken) =>
        (await ResolveDetailedAsync(
            vision,
            targetName,
            requestedRole,
            containerName,
            hintImageX,
            hintImageY,
            cancellationToken).ConfigureAwait(false)).Target;

    public static async Task<GroundedDesktopResolution> ResolveDetailedAsync(
        VisionAttachment vision,
        string targetName,
        string? requestedRole,
        string? containerName,
        int hintImageX,
        int hintImageY,
        CancellationToken cancellationToken)
    {
        if (!vision.TryMapImagePoint(hintImageX, hintImageY, out var hintDesktopX, out var hintDesktopY))
            return new GroundedDesktopResolution(
                null,
                GroundedEntityResolutionKind.None,
                []);

        cancellationToken.ThrowIfCancellationRequested();
        var accessibility = FindAccessibilityTarget(
            vision,
            targetName,
            requestedRole,
            containerName,
            hintDesktopX,
            hintDesktopY,
            out var entityResolution);
        if (accessibility is not null)
            return new GroundedDesktopResolution(
                accessibility.Target,
                GroundedEntityResolutionKind.HighConfidence,
                [],
                accessibility.Target.Source,
                entityResolution?.Score ?? 1,
                entityResolution?.RunnerUpScore ?? 0,
                vision.DesktopSnapshot?.Elements.Count ?? 0);

        var ocrHintX = hintImageX;
        var ocrHintY = hintImageY;
        if (!vision.TryMapImagePointToGrounding(
                hintImageX,
                hintImageY,
                out ocrHintX,
                out ocrHintY))
        {
            ocrHintX = hintImageX;
            ocrHintY = hintImageY;
        }
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            var containerResolution = await LocalOcrGrounder.ResolveNearestAsync(
                vision.GroundingBytes,
                containerName,
                ocrHintX,
                ocrHintY,
                cancellationToken);
            if (containerResolution.Match is not { } container)
                return FromUncertainResolution(entityResolution);
            ocrHintX = container.X + (container.Width / 2);
            ocrHintY = container.Y + container.Height + 36;
        }

        var ocrResolution = await LocalOcrGrounder.ResolveNearestAsync(
            vision.GroundingBytes,
            targetName,
            ocrHintX,
            ocrHintY,
            cancellationToken);
        if (ocrResolution.Match is not { } match)
        {
            if (ocrResolution.RequiresClarification)
            {
                return new GroundedDesktopResolution(
                    null,
                    ocrResolution.Kind,
                    ocrResolution.Alternatives,
                    "local_windows_ocr",
                    ocrResolution.Score,
                    ocrResolution.RunnerUpScore,
                    ocrResolution.CandidateCount);
            }
            return FromUncertainResolution(entityResolution);
        }
        if (!vision.TryMapGroundingPointToImage(match.X, match.Y, out var imageLeft, out var imageTop) ||
            !vision.TryMapImagePoint(imageLeft, imageTop, out var left, out var top))
            return FromUncertainResolution(entityResolution);

        var width = Math.Max(
            2,
            vision.MapImageWidth(vision.MapGroundingWidthToImage(match.Width)));
        var height = Math.Max(
            2,
            vision.MapImageHeight(vision.MapGroundingHeightToImage(match.Height)));
        return new GroundedDesktopResolution(
            new GroundedDesktopTarget(
                "local_windows_ocr",
                ocrResolution.MatchedText ?? targetName,
                requestedRole,
                left,
                top,
                width,
                height),
            GroundedEntityResolutionKind.HighConfidence,
            [],
            "local_windows_ocr",
            ocrResolution.Score,
            ocrResolution.RunnerUpScore,
            ocrResolution.CandidateCount);
    }

    internal static int BestNameMatchScoreForTesting(string requestedName, string candidateName) =>
        NameMatchScore(requestedName, candidateName);

    internal static bool RoleMatchesForTesting(string requestedRole, string actualRole) =>
        RoleMatches(requestedRole, actualRole);

    private static AccessibilityTargetMatch? FindAccessibilityTarget(
        VisionAttachment vision,
        string targetName,
        string? requestedRole,
        string? containerName,
        int hintX,
        int hintY,
        out GroundedEntityResolution? entityResolution)
    {
        entityResolution = null;
        var snapshot = vision.DesktopSnapshot;
        if (snapshot is null) return null;
        var candidates = new List<AccessibilityTargetCandidate>();
        foreach (var element in snapshot.Elements)
        {
            var name = element.Name.Trim();
            if (name.Length == 0) continue;
            if (!string.IsNullOrWhiteSpace(containerName) &&
                NameMatchScore(containerName, element.ParentName ?? string.Empty) == 0)
                continue;
            if (!IntersectsVision(vision, element.X, element.Y, element.Width, element.Height))
                continue;
            if (!string.IsNullOrWhiteSpace(requestedRole) &&
                !RoleMatches(requestedRole, element.Role))
                continue;

            var nameScore = NameMatchScore(targetName, name);
            var roleBonus = RoleMatches(requestedRole, element.Role) ? 180 : 0;
            var centerX = element.X + (element.Width / 2d);
            var centerY = element.Y + (element.Height / 2d);
            var distance = Math.Sqrt(Math.Pow(centerX - hintX, 2) + Math.Pow(centerY - hintY, 2));
            var rank = (nameScore * 1_000d) + roleBonus - Math.Min(900d, distance / 3d);
            candidates.Add(new AccessibilityTargetCandidate(
                new AccessibilityTargetMatch(
                    new GroundedDesktopTarget(
                        "isolated_windows_ui_automation",
                        name,
                        element.Role,
                        element.X,
                        element.Y,
                        element.Width,
                        element.Height)),
                nameScore,
                rank,
                element.ParentName));
        }

        var directCandidates = candidates
                .Where(candidate => candidate.NameScore > 0)
                .OrderByDescending(candidate => candidate.NameScore)
                .ThenByDescending(candidate => candidate.Rank)
                .ToArray();
        if (directCandidates.Length > 0)
        {
            var bestScore = directCandidates[0].NameScore;
            var equallyStrong = directCandidates
                .Where(candidate => candidate.NameScore == bestScore)
                .ToArray();
            if (equallyStrong.Length == 1)
                return equallyStrong[0].Match;

            entityResolution = new GroundedEntityResolution(
                GroundedEntityResolutionKind.Ambiguous,
                targetName,
                null,
                bestScore / 100d,
                bestScore / 100d,
                equallyStrong
                    .Take(3)
                    .Select(candidate => new GroundedEntityCandidate(
                        candidate.Match.Target.Name,
                        candidate.Match.Target.Role,
                        candidate.SemanticContainer))
                    .ToArray());
            return null;
        }

        entityResolution = GroundedEntityResolver.Resolve(
            targetName,
            candidates.Select(candidate => new GroundedEntityCandidate(
                candidate.Match.Target.Name,
                candidate.Match.Target.Role,
                candidate.SemanticContainer)));
        if (entityResolution.Kind != GroundedEntityResolutionKind.HighConfidence ||
            entityResolution.Candidate is null)
            return null;

        var resolvedCandidate = entityResolution.Candidate;
        return candidates
            .Where(candidate =>
                string.Equals(
                    candidate.Match.Target.Name,
                    resolvedCandidate.Value,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    candidate.Match.Target.Role,
                    resolvedCandidate.Role,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Rank)
            .Select(candidate => candidate.Match)
            .FirstOrDefault();
    }

    private static GroundedDesktopResolution FromUncertainResolution(
        GroundedEntityResolution? resolution)
    {
        if (resolution is null)
            return new GroundedDesktopResolution(
                null,
                GroundedEntityResolutionKind.None,
                []);
        return new GroundedDesktopResolution(
            null,
            resolution.Kind,
            resolution.Alternatives
                .Select(candidate => string.IsNullOrWhiteSpace(candidate.Container)
                    ? candidate.Value
                    : $"{candidate.Value} under {candidate.Container}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray(),
            "isolated_windows_ui_automation",
            resolution.Score,
            resolution.RunnerUpScore,
            resolution.Alternatives.Count);
    }

    private static bool IntersectsVision(VisionAttachment vision, int x, int y, int width, int height)
    {
        if (!vision.HasDesktopMapping) return false;
        var right = x + width;
        var bottom = y + height;
        var visionRight = vision.ContextX!.Value + vision.ContextWidth!.Value;
        var visionBottom = vision.ContextY!.Value + vision.ContextHeight!.Value;
        return right > vision.ContextX.Value &&
               bottom > vision.ContextY.Value &&
               x < visionRight &&
               y < visionBottom;
    }

    private static int NameMatchScore(string requestedName, string candidateName)
    {
        var requested = SearchTokens(requestedName);
        var candidate = SearchTokens(candidateName);
        if (requested.Length == 0 || candidate.Length == 0) return 0;

        var requestedJoined = string.Concat(requested);
        var candidateJoined = string.Concat(candidate);
        if (string.Equals(requestedJoined, candidateJoined, StringComparison.Ordinal)) return 100;
        if (candidateJoined.Length >= 4 && requestedJoined.Contains(candidateJoined, StringComparison.Ordinal))
            return 75 + Math.Min(20, candidateJoined.Length / 2);
        if (requestedJoined.Length >= 4 && candidateJoined.Contains(requestedJoined, StringComparison.Ordinal))
            return 75 + Math.Min(20, requestedJoined.Length / 2);

        var requestedDistinct = requested.Distinct(StringComparer.Ordinal).ToArray();
        var candidateDistinct = candidate.Distinct(StringComparer.Ordinal).ToArray();
        var overlap = requestedDistinct.Intersect(candidateDistinct, StringComparer.Ordinal).Count();
        if (overlap == 0) return 0;

        // A shared person name, date, or ordinary sentence fragment must not
        // let pointer proximity override the distinctive target identity.
        // Substring matches above already handle short exact visible labels.
        var requestedCoverage = overlap / (double)requestedDistinct.Length;
        var candidateCoverage = overlap / (double)candidateDistinct.Length;
        if (requestedDistinct.Length == 1 ||
            requestedCoverage < 0.75 ||
            candidateCoverage < 0.20)
            return 0;
        return 45 +
               (int)Math.Round(requestedCoverage * 35) +
               (int)Math.Round(candidateCoverage * 15);
    }

    private static string[] SearchTokens(string text)
    {
        var tokens = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(token => token.Length > 0)
            .ToArray();
        var significant = tokens.Where(token => !DescriptiveTokens.Contains(token)).ToArray();
        return significant.Length > 0 ? significant : tokens;
    }

    private static string Normalize(string text) =>
        SemanticUiVocabulary.CanonicalizeToken(text);

    private static bool RoleMatches(string? requestedRole, string? actualRole)
    {
        if (string.IsNullOrWhiteSpace(requestedRole)) return false;
        if (string.IsNullOrWhiteSpace(actualRole)) return false;
        var requested = Normalize(requestedRole);
        var actual = Normalize(actualRole);
        return requested == actual ||
               (requested == "account" && actual is "treeitem" or "listitem") ||
               (requested == "folder" && actual is "treeitem" or "listitem") ||
               (requested is "listitem" or "item" && actual is "treeitem" or "listitem" or "dataitem") ||
               (requested == "textfield" && actual is "edit" or "document") ||
               (requested == "menuitem" && actual == "menuitem");
    }

    private sealed record AccessibilityTargetMatch(GroundedDesktopTarget Target);

    private sealed record AccessibilityTargetCandidate(
        AccessibilityTargetMatch Match,
        int NameScore,
        double Rank,
        string? SemanticContainer);
}
