using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AshaLive;

internal readonly record struct OcrTextMatch(int X, int Y, int Width, int Height);

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
        var query = SignificantTokens(requestedText);
        if (png.Length == 0 || query.Length == 0) return null;

        cancellationToken.ThrowIfCancellationRequested();
        using var memory = new MemoryStream(png, writable: false);
        using var randomAccess = memory.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccess);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;
        var result = await engine.RecognizeAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        OcrTextMatch? nearest = null;
        double nearestDistance = double.MaxValue;
        var bestTokenLength = 0;
        foreach (var line in result.Lines)
        {
            var words = line.Words.ToArray();
            var normalized = words.Select(word => NormalizeToken(word.Text)).ToArray();
            foreach (var span in MatchingSpans(normalized, query))
            {
                var selected = words.Skip(span.Start).Take(span.Length).Select(word => word.BoundingRect).ToArray();
                var left = selected.Min(rectangle => rectangle.X);
                var top = selected.Min(rectangle => rectangle.Y);
                var right = selected.Max(rectangle => rectangle.X + rectangle.Width);
                var bottom = selected.Max(rectangle => rectangle.Y + rectangle.Height);
                var candidate = new OcrTextMatch(
                    Math.Max(0, (int)Math.Floor(left)),
                    Math.Max(0, (int)Math.Floor(top)),
                    Math.Max(1, (int)Math.Ceiling(right - left)),
                    Math.Max(1, (int)Math.Ceiling(bottom - top)));
                var centerX = candidate.X + (candidate.Width / 2d);
                var centerY = candidate.Y + (candidate.Height / 2d);
                var distance = Math.Pow(centerX - hintX, 2) + Math.Pow(centerY - hintY, 2);
                if (span.Length < bestTokenLength ||
                    (span.Length == bestTokenLength && distance >= nearestDistance)) continue;
                nearest = candidate;
                bestTokenLength = span.Length;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    internal static int BestContiguousMatchLengthForTesting(string requestedText, string recognizedLine)
    {
        var query = SignificantTokens(requestedText);
        var recognized = Tokenize(recognizedLine);
        return MatchingSpans(recognized, query).Select(span => span.Length).DefaultIfEmpty(0).Max();
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

    private static string[] Tokenize(string text) => text
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeToken)
        .Where(token => token.Length > 0)
        .ToArray();

    private static string NormalizeToken(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private readonly record struct TokenSpan(int Start, int Length);
}
