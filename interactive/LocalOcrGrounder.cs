using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AshaLive;

internal readonly record struct OcrTextMatch(int X, int Y, int Width, int Height);

internal sealed record OcrGroundingResolution(
    OcrTextMatch? Match,
    GroundedEntityResolutionKind Kind,
    string? MatchedText,
    double Score,
    double RunnerUpScore,
    IReadOnlyList<string> Alternatives,
    int CandidateCount)
{
    public bool RequiresClarification =>
        Match is null &&
        Kind is GroundedEntityResolutionKind.Clarification or GroundedEntityResolutionKind.Ambiguous;
}

internal sealed record OcrTextCandidate(string Text, OcrTextMatch Bounds);

/// <summary>
/// Uses Windows' built-in, on-device OCR to snap a model's approximate visual
/// target to text that is genuinely present in the supplied screenshot.
/// Images and OCR results never leave the machine through this component.
/// </summary>
internal static class LocalOcrGrounder
{
    private static readonly HashSet<string> DescriptiveTokens = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "for", "in", "inside", "on", "at", "of",
        "folder", "account", "button", "item", "row", "control", "application", "app", "outlook",
        "der", "die", "das", "den", "dem", "ein", "eine", "einer", "einen", "im", "in", "auf", "von", "für", "fuer",
        "ordner", "konto", "schaltfläche", "schaltflaeche", "element", "zeile", "anwendung",
    };

    public static async Task<OcrTextMatch?> FindNearestAsync(
        byte[] png,
        string requestedText,
        int hintX,
        int hintY,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveNearestAsync(
            png,
            requestedText,
            hintX,
            hintY,
            cancellationToken).ConfigureAwait(false);
        return resolution.Kind == GroundedEntityResolutionKind.HighConfidence
            ? resolution.Match
            : null;
    }

    public static async Task<OcrGroundingResolution> ResolveNearestAsync(
        byte[] png,
        string requestedText,
        int hintX,
        int hintY,
        CancellationToken cancellationToken)
    {
        var query = SignificantTokens(requestedText);
        if (png.Length == 0 || query.Length == 0)
            return EmptyResolution();

        cancellationToken.ThrowIfCancellationRequested();
        var decoded = await DecodeForOcrAsync(png);
        using var bitmap = decoded.Bitmap;
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return EmptyResolution();
        var result = await engine.RecognizeAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = BuildCandidates(
            result,
            decoded.Scale,
            Math.Clamp(query.Length + 2, 2, 6));
        return ResolveCandidates(requestedText, candidates, hintX, hintY);
    }

    private static IReadOnlyList<OcrTextCandidate> BuildCandidates(
        OcrResult result,
        double scale,
        int maximumSpanLength)
    {
        const int maximumCandidates = 1_500;
        var candidates = new List<OcrTextCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddSpans(IReadOnlyList<PositionedOcrWord> words)
        {
            var maximumLength = Math.Min(maximumSpanLength, words.Count);
            for (var length = 1; length <= maximumLength; length++)
            {
                for (var start = 0; start + length <= words.Count; start++)
                {
                    var selectedWords = words.Skip(start).Take(length).ToArray();
                    var text = string.Join(' ', selectedWords.Select(word => word.Text)).Trim();
                    if (text.Length == 0) continue;
                    var left = selectedWords.Min(word => word.X);
                    var top = selectedWords.Min(word => word.Y);
                    var right = selectedWords.Max(word => word.X + word.Width);
                    var bottom = selectedWords.Max(word => word.Y + word.Height);
                    var bounds = new OcrTextMatch(
                        Math.Max(0, (int)Math.Floor(left / scale)),
                        Math.Max(0, (int)Math.Floor(top / scale)),
                        Math.Max(1, (int)Math.Ceiling((right - left) / scale)),
                        Math.Max(1, (int)Math.Ceiling((bottom - top) / scale)));
                    var key = $"{SemanticUiVocabulary.CanonicalizeText(text)}|{bounds.X}|{bounds.Y}|{bounds.Width}|{bounds.Height}";
                    if (!seen.Add(key)) continue;
                    candidates.Add(new OcrTextCandidate(text, bounds));
                    if (candidates.Count >= maximumCandidates) return;
                }
            }
        }

        var positionedWords = new List<PositionedOcrWord>();
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .Select(word => new PositionedOcrWord(
                    word.Text.Trim(),
                    word.BoundingRect.X,
                    word.BoundingRect.Y,
                    word.BoundingRect.Width,
                    word.BoundingRect.Height))
                .OrderBy(word => word.X)
                .ToArray();
            positionedWords.AddRange(words);
            AddSpans(words);
            if (candidates.Count >= maximumCandidates) return candidates;
        }

        // Windows OCR sometimes divides one visually continuous row into
        // several OcrLine objects. Reconstruct rows from geometry as a second
        // candidate source, while splitting very large horizontal gaps so
        // unrelated controls in the same band are not joined.
        var visualRows = new List<List<PositionedOcrWord>>();
        foreach (var word in positionedWords
                     .OrderBy(word => word.CenterY)
                     .ThenBy(word => word.X))
        {
            var row = visualRows
                .Where(candidate => candidate.Any(existing => SharesVisualRow(existing, word)))
                .OrderBy(candidate => Math.Abs(candidate.Average(existing => existing.CenterY) - word.CenterY))
                .FirstOrDefault();
            if (row is null)
            {
                row = [];
                visualRows.Add(row);
            }
            row.Add(word);
        }

        foreach (var row in visualRows)
        {
            var ordered = row.OrderBy(word => word.X).ToArray();
            if (ordered.Length == 0) continue;
            var segment = new List<PositionedOcrWord> { ordered[0] };
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                var gap = current.X - (previous.X + previous.Width);
                var rowHeight = Math.Max(previous.Height, current.Height);
                if (gap > Math.Max(24, rowHeight * 4))
                {
                    AddSpans(segment);
                    if (candidates.Count >= maximumCandidates) return candidates;
                    segment = [];
                }
                segment.Add(current);
            }
            AddSpans(segment);
            if (candidates.Count >= maximumCandidates) return candidates;
        }
        return candidates;
    }

    private static bool SharesVisualRow(PositionedOcrWord left, PositionedOcrWord right)
    {
        var overlap = Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y);
        var minimumHeight = Math.Max(1, Math.Min(left.Height, right.Height));
        if (overlap / minimumHeight >= 0.45) return true;
        return Math.Abs(left.CenterY - right.CenterY) <= Math.Max(left.Height, right.Height) * 0.65;
    }

    private static OcrGroundingResolution ResolveCandidates(
        string requestedText,
        IReadOnlyList<OcrTextCandidate> candidates,
        int hintX,
        int hintY)
    {
        if (candidates.Count == 0) return EmptyResolution();

        var nearestPerText = candidates
            .Where(candidate => candidate.Text.Length > 0)
            .GroupBy(
                candidate => SemanticUiVocabulary.CanonicalizeText(candidate.Text),
                StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .Select(group => group
                .OrderBy(candidate => DistanceSquared(candidate.Bounds, hintX, hintY))
                .First())
            .ToArray();
        if (nearestPerText.Length == 0) return EmptyResolution();

        var entityResolution = GroundedEntityResolver.Resolve(
            requestedText,
            nearestPerText.Select(candidate => new GroundedEntityCandidate(candidate.Text)));
        var alternatives = entityResolution.Alternatives
            .Select(candidate => candidate.Value)
            .Where(candidate => IsUsefulAlternative(requestedText, candidate))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(3)
            .ToArray();
        if (entityResolution.Kind != GroundedEntityResolutionKind.HighConfidence ||
            entityResolution.Candidate is null)
        {
            return new OcrGroundingResolution(
                null,
                entityResolution.Kind,
                entityResolution.Candidate?.Value,
                entityResolution.Score,
                entityResolution.RunnerUpScore,
                alternatives,
                nearestPerText.Length);
        }

        var matched = nearestPerText
            .Where(candidate => string.Equals(
                SemanticUiVocabulary.CanonicalizeText(candidate.Text),
                SemanticUiVocabulary.CanonicalizeText(entityResolution.Candidate.Value),
                StringComparison.Ordinal))
            .OrderBy(candidate => DistanceSquared(candidate.Bounds, hintX, hintY))
            .FirstOrDefault();
        return matched is null
            ? EmptyResolution(nearestPerText.Length)
            : new OcrGroundingResolution(
                matched.Bounds,
                GroundedEntityResolutionKind.HighConfidence,
                matched.Text,
                entityResolution.Score,
                entityResolution.RunnerUpScore,
                alternatives,
                nearestPerText.Length);
    }

    internal static int BestContiguousMatchLengthForTesting(string requestedText, string recognizedLine)
    {
        var query = SignificantTokens(requestedText);
        var recognized = Tokenize(recognizedLine);
        return MatchingSpans(recognized, query).Select(span => span.Length).DefaultIfEmpty(0).Max();
    }

    internal static async Task<IReadOnlyList<string>> RecognizeLinesForTestingAsync(
        byte[] png,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = await DecodeForOcrAsync(png);
        using var bitmap = decoded.Bitmap;
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return [];
        var result = await engine.RecognizeAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Lines.Select(line => line.Text).ToArray();
    }

    internal static OcrGroundingResolution ResolveCandidateTextsForTesting(
        string requestedText,
        IReadOnlyList<string> recognizedCandidates)
    {
        var candidates = recognizedCandidates
            .Select((text, index) => new OcrTextCandidate(
                text,
                new OcrTextMatch(index * 100, 0, 80, 20)))
            .ToArray();
        return ResolveCandidates(requestedText, candidates, 0, 0);
    }

    private static async Task<(SoftwareBitmap Bitmap, double Scale)> DecodeForOcrAsync(byte[] png)
    {
        using var memory = new MemoryStream(png, writable: false);
        using var randomAccess = memory.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccess);
        const double maximumDimension = 2400d;
        var scale = Math.Min(
            2d,
            Math.Min(
                maximumDimension / Math.Max(1u, decoder.PixelWidth),
                maximumDimension / Math.Max(1u, decoder.PixelHeight)));
        scale = Math.Max(1d, scale);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return (bitmap, scale);
    }

    private static IEnumerable<TokenSpan> MatchingSpans(IReadOnlyList<string> recognized, IReadOnlyList<string> query)
    {
        for (var length = query.Count; length >= 1; length--)
        {
            for (var queryStart = 0; queryStart + length <= query.Count; queryStart++)
            {
                for (var recognizedStart = 0; recognizedStart + length <= recognized.Count; recognizedStart++)
                {
                    var matches = true;
                    for (var offset = 0; offset < length; offset++)
                    {
                        if (string.Equals(recognized[recognizedStart + offset], query[queryStart + offset], StringComparison.Ordinal))
                            continue;
                        matches = false;
                        break;
                    }
                    if (matches) yield return new TokenSpan(recognizedStart, length);
                }
            }
        }
    }

    private static string[] SignificantTokens(string text)
    {
        var tokens = Tokenize(text);
        var significant = tokens.Where(token => !DescriptiveTokens.Contains(token)).ToArray();
        return significant.Length > 0 ? significant : tokens;
    }

    private static bool IsUsefulAlternative(string requestedText, string candidateText)
    {
        var requested = SignificantTokens(requestedText);
        var candidate = SignificantTokens(candidateText);
        if (requested.Length <= 1) return candidate.Length > 0;
        if (candidate.Length < 2) return false;
        return GroundedEntityResolver.ScorePair(requestedText, candidateText) >= 0.60;
    }

    private static string[] Tokenize(string text) => text
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeToken)
        .Where(token => token.Length > 0)
        .ToArray();

    private static string NormalizeToken(string text) =>
        SemanticUiVocabulary.CanonicalizeToken(text);

    private static double DistanceSquared(OcrTextMatch candidate, int hintX, int hintY)
    {
        var centerX = candidate.X + (candidate.Width / 2d);
        var centerY = candidate.Y + (candidate.Height / 2d);
        return Math.Pow(centerX - hintX, 2) + Math.Pow(centerY - hintY, 2);
    }

    private static OcrGroundingResolution EmptyResolution(int candidateCount = 0) =>
        new(
            null,
            GroundedEntityResolutionKind.None,
            null,
            0,
            0,
            [],
            candidateCount);

    private readonly record struct TokenSpan(int Start, int Length);

    private readonly record struct PositionedOcrWord(
        string Text,
        double X,
        double Y,
        double Width,
        double Height)
    {
        public double CenterY => Y + (Height / 2d);
    }
}
