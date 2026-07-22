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
    public static async Task<OcrTextMatch?> FindNearestAsync(
        byte[] png,
        string requestedText,
        int hintX,
        int hintY,
        CancellationToken cancellationToken)
    {
        var query = Tokenize(requestedText);
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
        foreach (var line in result.Lines)
        {
            var words = line.Words.ToArray();
            var normalized = words.Select(word => NormalizeToken(word.Text)).ToArray();
            for (var start = 0; start + query.Length <= normalized.Length; start++)
            {
                var matches = true;
                for (var offset = 0; offset < query.Length; offset++)
                {
                    if (!string.Equals(normalized[start + offset], query[offset], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }
                if (!matches) continue;

                var selected = words.Skip(start).Take(query.Length).Select(word => word.BoundingRect).ToArray();
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
                if (distance >= nearestDistance) continue;
                nearest = candidate;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    private static string[] Tokenize(string text) => text
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeToken)
        .Where(token => token.Length > 0)
        .ToArray();

    private static string NormalizeToken(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
