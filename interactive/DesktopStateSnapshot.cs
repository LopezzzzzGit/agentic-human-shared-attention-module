using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AshaLive;

internal sealed record DesktopStateElement(
    int LocalId,
    string Name,
    string Role,
    string? ParentName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsEnabled,
    bool HasKeyboardFocus,
    bool? IsSelected,
    string? ExpandCollapseState,
    IReadOnlyList<string> Patterns);

internal sealed record DesktopImageCoordinateMap(
    int DesktopX,
    int DesktopY,
    int DesktopWidth,
    int DesktopHeight,
    int ImageWidth,
    int ImageHeight)
{
    public bool TryMapBounds(
        DesktopStateElement element,
        out int imageX,
        out int imageY,
        out int imageWidth,
        out int imageHeight)
    {
        imageX = imageY = imageWidth = imageHeight = 0;
        if (DesktopWidth <= 0 || DesktopHeight <= 0 || ImageWidth <= 0 || ImageHeight <= 0)
            return false;

        var left = Math.Max((long)DesktopX, element.X);
        var top = Math.Max((long)DesktopY, element.Y);
        var right = Math.Min((long)DesktopX + DesktopWidth, (long)element.X + element.Width);
        var bottom = Math.Min((long)DesktopY + DesktopHeight, (long)element.Y + element.Height);
        if (right <= left || bottom <= top) return false;

        imageX = Math.Clamp(
            (int)Math.Round((left - DesktopX) * ImageWidth / (double)DesktopWidth),
            0,
            ImageWidth);
        imageY = Math.Clamp(
            (int)Math.Round((top - DesktopY) * ImageHeight / (double)DesktopHeight),
            0,
            ImageHeight);
        var imageRight = Math.Clamp(
            (int)Math.Round((right - DesktopX) * ImageWidth / (double)DesktopWidth),
            imageX,
            ImageWidth);
        var imageBottom = Math.Clamp(
            (int)Math.Round((bottom - DesktopY) * ImageHeight / (double)DesktopHeight),
            imageY,
            ImageHeight);
        imageWidth = Math.Max(1, imageRight - imageX);
        imageHeight = Math.Max(1, imageBottom - imageY);
        return true;
    }
}

internal sealed record DesktopStateSnapshot(
    string Id,
    long Generation,
    DateTime CapturedAtUtc,
    string ProcessName,
    string WindowTitle,
    int ProcessId,
    IReadOnlyList<DesktopStateElement> Elements,
    int VisitedElementCount,
    bool IsTreeBlind)
{
    public string Signature
    {
        get
        {
            var canonical = new StringBuilder()
                .Append(ProcessId).Append('|')
                .Append(ProcessName).Append('|')
                .Append(WindowTitle);
            foreach (var element in Elements)
            {
                canonical.Append('\n')
                    .Append(element.Name).Append('|')
                    .Append(element.Role).Append('|')
                    .Append(element.ParentName).Append('|')
                    .Append(element.X).Append(',').Append(element.Y).Append(',')
                    .Append(element.Width).Append(',').Append(element.Height).Append('|')
                    .Append(element.IsEnabled).Append('|')
                    .Append(element.IsSelected).Append('|')
                    .Append(element.ExpandCollapseState).Append('|')
                    .AppendJoin(',', element.Patterns);
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
            return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
        }
    }

    public string ToModelContext(int maximumCharacters = 3_200) =>
        ToModelContext(null, maximumCharacters, null);

    public string ToModelContext(string? relevanceQuery, int maximumCharacters = 3_200)
        => ToModelContext(relevanceQuery, maximumCharacters, null);

    public string ToModelContext(
        string? relevanceQuery,
        int maximumCharacters,
        DesktopImageCoordinateMap? coordinateMap)
    {
        var text = new StringBuilder();
        text.Append("Foreground UI snapshot ")
            .Append(Id)
            .Append(" signature ")
            .Append(Signature)
            .Append(" generation ")
            .Append(Generation)
            .Append(": process=")
            .Append(ProcessName)
            .Append(", window=\"")
            .Append(Clean(WindowTitle))
            .Append("\", named elements=")
            .Append(Elements.Count)
            .Append(", visited elements=")
            .Append(VisitedElementCount)
            .Append(IsTreeBlind
                ? ". The accessibility tree is currently sparse; use the screenshot or request a refreshed view."
                : ". Element local IDs are valid only for this snapshot; use semantic name, role, and ancestor fields for actions.")
            .Append(coordinateMap is null
                ? " Bounds below are desktop coordinates retained for local diagnostics."
                : $" Bounds below are supplied-image pixels within a {coordinateMap.ImageWidth} by {coordinateMap.ImageHeight} image; desktop coordinates are intentionally omitted.")
            .AppendLine();

        foreach (var element in Elements
                     .Where(element => coordinateMap is null ||
                                       coordinateMap.TryMapBounds(element, out _, out _, out _, out _))
                     .OrderByDescending(element => RelevanceScore(element, relevanceQuery))
                     .ThenBy(element => element.LocalId))
        {
            var line = new StringBuilder()
                .Append('e').Append(element.LocalId)
                .Append(" role=").Append(element.Role)
                .Append(" name=\"").Append(Clean(element.Name)).Append('"');
            if (!string.IsNullOrWhiteSpace(element.ParentName))
                line.Append(" parent=\"").Append(Clean(element.ParentName)).Append('"');
            if (coordinateMap is not null &&
                coordinateMap.TryMapBounds(element, out var imageX, out var imageY, out var imageWidth, out var imageHeight))
            {
                line.Append(" image_bounds=").Append(imageX).Append(',').Append(imageY)
                    .Append(',').Append(imageWidth).Append(',').Append(imageHeight);
            }
            else
            {
                line.Append(" desktop_bounds=").Append(element.X).Append(',').Append(element.Y)
                    .Append(',').Append(element.Width).Append(',').Append(element.Height);
            }
            if (element.HasKeyboardFocus) line.Append(" focused");
            if (element.IsSelected is true) line.Append(" selected");
            if (!string.IsNullOrWhiteSpace(element.ExpandCollapseState))
                line.Append(" expand=").Append(element.ExpandCollapseState);
            if (element.Patterns.Count > 0)
                line.Append(" actions=").Append(string.Join(',', element.Patterns));
            line.AppendLine();

            if (text.Length + line.Length > maximumCharacters)
            {
                text.Append("…additional UI elements omitted from provider context.");
                break;
            }
            text.Append(line);
        }
        return text.ToString().Trim();
    }

    private static int RelevanceScore(DesktopStateElement element, string? query)
    {
        if (element.LocalId == 1) return 20_000;
        var score = 0;
        if (element.HasKeyboardFocus) score += 5_000;
        if (element.IsSelected is true) score += 4_000;
        if (element.Patterns.Count > 0) score += 450;
        if (element.Role is "listitem" or "treeitem" or "dataitem" or "edit" or "document")
            score += 900;
        else if (element.Role is "button" or "menuitem" or "tabitem" or "link")
            score += 550;
        else if (element.Role is "group" or "pane" or "text")
            score -= 250;

        var tokens = QueryTokens(query);
        if (tokens.Count > 0)
        {
            var name = Normalize(element.Name);
            var parent = Normalize(element.ParentName ?? string.Empty);
            score += tokens.Count(token => name.Contains(token, StringComparison.Ordinal)) * 1_600;
            score += tokens.Count(token => parent.Contains(token, StringComparison.Ordinal)) * 1_050;
        }

        var ancestry = Normalize(element.ParentName ?? string.Empty);
        if (Regex.IsMatch(
                ancestry,
                @"\b(message|mail|inbox|list|items?|results?|navigation|nachricht|posteingang|liste|elemente|ergebnisse|navigation)\b",
                RegexOptions.CultureInvariant))
            score += 650;
        return score;
    }

    private static HashSet<string> QueryTokens(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "this", "that", "with", "from", "what", "where", "which",
            "please", "can", "could", "would", "tell", "show", "open", "click",
            "der", "die", "das", "den", "dem", "ein", "eine", "bitte", "kann",
            "kannst", "was", "wo", "welche", "öffne", "oeffne", "zeige",
        };
        return Regex.Matches(Normalize(query), @"[\p{L}\p{N}@._-]{3,}")
            .Select(match => match.Value)
            .Where(token => !ignored.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Normalize(string value) =>
        value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 140 ? normalized : normalized[..139] + "…";
    }
}

/// <summary>
/// Captures a bounded semantic projection of the current foreground UI. It
/// stores no AutomationElement handles: every local ID expires with the
/// snapshot, preventing stale element-addressed actions after a tree rebuild.
/// </summary>
internal sealed class DesktopStateReader
{
    private const int MaximumVisitedElements = 900;
    private const int MaximumNamedElements = 90;
    private long _generation;

    public DesktopStateSnapshot? CaptureForeground()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;
            var root = AutomationElement.FromHandle(window);
            if (root is null) return null;

            var processId = root.Current.ProcessId;
            var processName = "unknown-process";
            try { processName = Process.GetProcessById(processId).ProcessName; }
            catch { }

            var elements = new List<DesktopStateElement>(MaximumNamedElements);
            var queue = new Queue<TraversalNode>();
            queue.Enqueue(new TraversalNode(root, null));
            var visited = 0;
            var nextLocalId = 1;
            var walker = TreeWalker.ControlViewWalker;

            while (queue.Count > 0 &&
                   visited < MaximumVisitedElements &&
                   elements.Count < MaximumNamedElements)
            {
                var node = queue.Dequeue();
                visited++;
                AutomationElement.AutomationElementInformation current;
                try { current = node.Element.Current; }
                catch (ElementNotAvailableException) { continue; }

                var name = current.Name?.Trim();
                var nearestNamedParent = node.NearestNamedParent;
                if (!string.IsNullOrWhiteSpace(name) && !current.IsOffscreen)
                {
                    var rectangle = current.BoundingRectangle;
                    if (!rectangle.IsEmpty && rectangle.Width >= 2 && rectangle.Height >= 2)
                    {
                        var role = RoleName(current.ControlType);
                        elements.Add(new DesktopStateElement(
                            nextLocalId++,
                            name,
                            role,
                            nearestNamedParent,
                            (int)Math.Round(rectangle.Left),
                            (int)Math.Round(rectangle.Top),
                            Math.Max(2, (int)Math.Round(rectangle.Width)),
                            Math.Max(2, (int)Math.Round(rectangle.Height)),
                            current.IsEnabled,
                            current.HasKeyboardFocus,
                            ReadSelected(node.Element),
                            ReadExpandState(node.Element),
                            ReadPatterns(node.Element)));
                        nearestNamedParent = name;
                    }
                }

                AutomationElement? child;
                try { child = walker.GetFirstChild(node.Element); }
                catch (ElementNotAvailableException) { continue; }
                while (child is not null)
                {
                    queue.Enqueue(new TraversalNode(child, nearestNamedParent));
                    try { child = walker.GetNextSibling(child); }
                    catch (ElementNotAvailableException) { child = null; }
                }
            }

            var generation = Interlocked.Increment(ref _generation);
            return new DesktopStateSnapshot(
                $"desktop-state-{generation:D6}-{Guid.NewGuid():N}",
                generation,
                DateTime.UtcNow,
                processName,
                root.Current.Name?.Trim() ?? string.Empty,
                processId,
                elements,
                visited,
                elements.Count < 12);
        }
        catch (Exception error) when (
            error is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static string RoleName(ControlType? controlType)
    {
        var name = controlType?.ProgrammaticName ?? "ControlType.Custom";
        const string prefix = "ControlType.";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            ? name[prefix.Length..].ToLowerInvariant()
            : name.ToLowerInvariant();
    }

    private static bool? ReadSelected(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                ? ((SelectionItemPattern)pattern).Current.IsSelected
                : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static string? ReadExpandState(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern)
                ? ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState.ToString().ToLowerInvariant()
                : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadPatterns(AutomationElement element)
    {
        var patterns = new List<string>(5);
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out _)) patterns.Add("invoke");
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)) patterns.Add("select");
            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _)) patterns.Add("expand");
            if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out _)) patterns.Add("scroll_into_view");
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out _)) patterns.Add("set_value");
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }
        return patterns;
    }

    private sealed record TraversalNode(AutomationElement Element, string? NearestNamedParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
