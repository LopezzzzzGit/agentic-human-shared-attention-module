using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

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
        "control", "application", "app", "outlook",
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
        CancellationToken cancellationToken)
    {
        if (!vision.TryMapImagePoint(hintImageX, hintImageY, out var hintDesktopX, out var hintDesktopY))
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        var accessibility = FindAccessibilityTarget(
            vision,
            targetName,
            requestedRole,
            containerName,
            hintDesktopX,
            hintDesktopY);
        if (accessibility is not null) return accessibility.Target;

        var ocrHintX = hintImageX;
        var ocrHintY = hintImageY;
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            var container = await LocalOcrGrounder.FindNearestAsync(
                vision.Bytes,
                containerName,
                hintImageX,
                hintImageY,
                cancellationToken);
            if (container is null) return null;
            ocrHintX = container.Value.X + (container.Value.Width / 2);
            ocrHintY = container.Value.Y + container.Value.Height + 36;
        }

        var ocr = await LocalOcrGrounder.FindNearestAsync(
            vision.Bytes,
            targetName,
            ocrHintX,
            ocrHintY,
            cancellationToken);
        if (ocr is not { } match ||
            !vision.TryMapImagePoint(match.X, match.Y, out var left, out var top))
            return null;

        var width = Math.Max(2, vision.MapImageWidth(match.Width));
        var height = Math.Max(2, vision.MapImageHeight(match.Height));
        return new GroundedDesktopTarget(
            "local_windows_ocr",
            targetName,
            requestedRole,
            left,
            top,
            width,
            height);
    }

    public static bool TryExecuteAccessibleAction(
        VisionAttachment vision,
        string targetName,
        string? requestedRole,
        string? containerName,
        int hintImageX,
        int hintImageY,
        string action,
        out GroundedDesktopTarget? target,
        out string? patternName)
    {
        target = null;
        patternName = null;
        if (!vision.TryMapImagePoint(hintImageX, hintImageY, out var hintDesktopX, out var hintDesktopY))
            return false;

        var match = FindAccessibilityTarget(
            vision,
            targetName,
            requestedRole,
            containerName,
            hintDesktopX,
            hintDesktopY);
        if (match is null) return false;
        target = match.Target;

        try
        {
            if (string.Equals(action, "click", StringComparison.Ordinal) &&
                string.Equals(Normalize(requestedRole ?? string.Empty), "account", StringComparison.Ordinal) &&
                match.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var accountExpand))
            {
                var expansion = (ExpandCollapsePattern)accountExpand;
                if (expansion.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                {
                    expansion.Expand();
                    patternName = "expand";
                    return true;
                }
            }

            if (string.Equals(action, "click", StringComparison.Ordinal) &&
                match.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            {
                ((SelectionItemPattern)selection).Select();
                patternName = "select";
                return true;
            }

            if (action is "click" or "double_click" &&
                match.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invocation))
            {
                ((InvokePattern)invocation).Invoke();
                patternName = "invoke";
                return true;
            }

            if (string.Equals(action, "click", StringComparison.Ordinal) &&
                match.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expansionPattern))
            {
                var expansion = (ExpandCollapsePattern)expansionPattern;
                if (expansion.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                    expansion.Expand();
                else if (expansion.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                    expansion.Collapse();
                else
                    return false;
                patternName = "expand_collapse";
                return true;
            }
        }
        catch (Exception error) when (
            error is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
        return false;
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
        int hintY)
    {
        try
        {
            var hit = AutomationElement.FromPoint(new Point(hintX, hintY));
            if (hit is null) return null;

            var processId = hit.Current.ProcessId;
            var searchRoot = FindProcessRoot(hit, processId);
            var elements = searchRoot.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            AccessibilityTargetMatch? best = null;
            double bestRank = double.MinValue;

            for (var index = 0; index < elements.Count; index++)
            {
                AutomationElement element;
                AutomationElement.AutomationElementInformation current;
                try
                {
                    element = elements[index];
                    current = element.Current;
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }

                var name = current.Name?.Trim();
                if (string.IsNullOrWhiteSpace(name) || current.IsOffscreen) continue;
                var nameScore = NameMatchScore(targetName, name);
                if (nameScore <= 0) continue;
                if (!string.IsNullOrWhiteSpace(containerName) &&
                    !HasMatchingAncestor(element, containerName, searchRoot))
                    continue;

                var rectangle = current.BoundingRectangle;
                if (rectangle.IsEmpty || rectangle.Width < 2 || rectangle.Height < 2) continue;
                var left = (int)Math.Round(rectangle.Left);
                var top = (int)Math.Round(rectangle.Top);
                var width = Math.Max(2, (int)Math.Round(rectangle.Width));
                var height = Math.Max(2, (int)Math.Round(rectangle.Height));
                if (!IntersectsVision(vision, left, top, width, height)) continue;

                var role = ReadRole(current.ControlType);
                if (!string.IsNullOrWhiteSpace(requestedRole) && !RoleMatches(requestedRole, role))
                    continue;
                var roleBonus = RoleMatches(requestedRole, role) ? 180 : 0;
                var centerX = left + (width / 2d);
                var centerY = top + (height / 2d);
                var distance = Math.Sqrt(Math.Pow(centerX - hintX, 2) + Math.Pow(centerY - hintY, 2));
                var rank = (nameScore * 1_000d) + roleBonus - Math.Min(900d, distance / 3d);
                if (rank <= bestRank) continue;

                bestRank = rank;
                best = new AccessibilityTargetMatch(
                    element,
                    new GroundedDesktopTarget(
                        "windows_ui_automation",
                        name,
                        role,
                        left,
                        top,
                        width,
                        height));
            }

            return best;
        }
        catch (Exception error) when (
            error is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static AutomationElement FindProcessRoot(AutomationElement element, int processId)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var depth = 0; depth < 32; depth++)
        {
            AutomationElement? parent;
            try { parent = walker.GetParent(current); }
            catch (ElementNotAvailableException) { break; }
            if (parent is null || parent == AutomationElement.RootElement) break;

            try
            {
                if (parent.Current.ProcessId != processId) break;
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
            current = parent;
        }
        return current;
    }

    private static bool HasMatchingAncestor(
        AutomationElement element,
        string containerName,
        AutomationElement searchRoot)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var depth = 0; depth < 24; depth++)
        {
            AutomationElement? parent;
            try { parent = walker.GetParent(current); }
            catch (ElementNotAvailableException) { return false; }
            if (parent is null) return false;
            try
            {
                if (NameMatchScore(containerName, parent.Current.Name ?? string.Empty) > 0)
                    return true;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            if (parent == searchRoot) return false;
            current = parent;
        }
        return false;
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

        var overlap = requested.Intersect(candidate, StringComparer.Ordinal).Count();
        return overlap == 0 ? 0 : 40 + (overlap * 10);
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
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? ReadRole(ControlType? controlType)
    {
        var programmaticName = controlType?.ProgrammaticName;
        const string prefix = "ControlType.";
        return !string.IsNullOrWhiteSpace(programmaticName) && programmaticName.StartsWith(prefix, StringComparison.Ordinal)
            ? programmaticName[prefix.Length..].ToLowerInvariant()
            : null;
    }

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

    private sealed record AccessibilityTargetMatch(
        AutomationElement Element,
        GroundedDesktopTarget Target);
}
