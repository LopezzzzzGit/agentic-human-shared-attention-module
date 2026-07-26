using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AshaLive;

/// <summary>
/// The only component allowed to call foreign UI Automation providers. The
/// host starts one disposable process per bounded inspection and kills this
/// process if a provider stops responding.
/// </summary>
internal sealed class DesktopStateCaptureCore
{
    private const int MaximumVisitedElements = 900;
    private const int MaximumNamedElements = 240;
    private long _generation;

    public DesktopStateSnapshot? CaptureForeground(int deniedProcessId = 0)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        var root = AutomationElement.FromHandle(window);
        return root is null || IsDeniedRoot(root, deniedProcessId)
            ? null
            : CaptureRoot(root);
    }

    public DesktopStateSnapshot? CaptureAtPoint(int x, int y, int deniedProcessId = 0)
    {
        var hit = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        if (hit is null || IsDeniedRoot(hit, deniedProcessId)) return null;
        var processId = hit.Current.ProcessId;
        var root = hit;
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; depth < 40; depth++)
        {
            var parent = walker.GetParent(root);
            if (parent is null || parent == AutomationElement.RootElement) break;
            if (parent.Current.ProcessId != processId) break;
            root = parent;
        }
        return IsDeniedRoot(root, deniedProcessId) ? null : CaptureRoot(root);
    }

    private static bool IsDeniedRoot(AutomationElement element, int deniedProcessId) =>
        deniedProcessId > 0 && element.Current.ProcessId == deniedProcessId;

    public DesktopAccessibleActionResult TryExecuteAccessibleAction(
        int x,
        int y,
        string action,
        string expectedName,
        string expectedRole,
        string semanticRole,
        int deniedProcessId)
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; element is not null && depth < 12; depth++)
        {
            var current = element.Current;
            if (deniedProcessId > 0 && current.ProcessId == deniedProcessId)
            {
                return new DesktopAccessibleActionResult(
                    Executed: false,
                    Uncertain: false,
                    Pattern: null,
                    Name: expectedName,
                    Role: expectedRole,
                    X: x,
                    Y: y,
                    Width: 0,
                    Height: 0);
            }
            var name = current.Name?.Trim() ?? string.Empty;
            var role = RoleName(current.ControlType);
            if (NamesMatch(name, expectedName) &&
                (string.IsNullOrWhiteSpace(expectedRole) ||
                 string.Equals(role, expectedRole, StringComparison.OrdinalIgnoreCase)))
            {
                var rectangle = current.BoundingRectangle;
                var result = ExecutePattern(element, action, semanticRole);
                return new DesktopAccessibleActionResult(
                    result.Executed,
                    Uncertain: false,
                    result.Pattern,
                    name,
                    role,
                    (int)Math.Round(rectangle.Left),
                    (int)Math.Round(rectangle.Top),
                    Math.Max(0, (int)Math.Round(rectangle.Width)),
                    Math.Max(0, (int)Math.Round(rectangle.Height)));
            }
            element = walker.GetParent(element);
        }
        return new DesktopAccessibleActionResult(
            Executed: false,
            Uncertain: false,
            Pattern: null,
            Name: expectedName,
            Role: expectedRole,
            X: x,
            Y: y,
            Width: 0,
            Height: 0);
    }

    private DesktopStateSnapshot CaptureRoot(AutomationElement root)
    {
        var processId = root.Current.ProcessId;
        var processName = "unknown-process";
        try
        {
            using var process = Process.GetProcessById(processId);
            processName = process.ProcessName;
        }
        catch
        {
            // The window can disappear during inspection.
        }

        var elements = new List<DesktopStateElement>(MaximumNamedElements);
        var queue = new Queue<TraversalNode>();
        queue.Enqueue(new TraversalNode(root, null));
        var visited = 0;
        var nextLocalId = 1;
        var walker = TreeWalker.ControlViewWalker;

        while (queue.Count > 0 && visited < MaximumVisitedElements)
        {
            var node = queue.Dequeue();
            visited++;
            var current = node.Element.Current;
            var name = current.Name?.Trim();
            var nearestNamedParent = node.NearestNamedParent;
            if (elements.Count < MaximumNamedElements &&
                !string.IsNullOrWhiteSpace(name) &&
                !current.IsOffscreen)
            {
                var rectangle = current.BoundingRectangle;
                if (!rectangle.IsEmpty && rectangle.Width >= 2 && rectangle.Height >= 2)
                {
                    elements.Add(new DesktopStateElement(
                        nextLocalId++,
                        name,
                        RoleName(current.ControlType),
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

            var child = walker.GetFirstChild(node.Element);
            while (child is not null)
            {
                queue.Enqueue(new TraversalNode(child, nearestNamedParent));
                child = walker.GetNextSibling(child);
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

    private static string RoleName(ControlType? controlType)
    {
        var name = controlType?.ProgrammaticName ?? "ControlType.Custom";
        const string prefix = "ControlType.";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            ? name[prefix.Length..].ToLowerInvariant()
            : name.ToLowerInvariant();
    }

    private static bool? ReadSelected(AutomationElement element) =>
        element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
            ? ((SelectionItemPattern)pattern).Current.IsSelected
            : null;

    private static string? ReadExpandState(AutomationElement element) =>
        element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern)
            ? ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState.ToString().ToLowerInvariant()
            : null;

    private static IReadOnlyList<string> ReadPatterns(AutomationElement element)
    {
        var patterns = new List<string>(5);
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out _)) patterns.Add("invoke");
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)) patterns.Add("select");
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _)) patterns.Add("expand");
        if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out _)) patterns.Add("scroll_into_view");
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out _)) patterns.Add("set_value");
        return patterns;
    }

    private static (bool Executed, string? Pattern) ExecutePattern(
        AutomationElement element,
        string action,
        string semanticRole)
    {
        if (string.Equals(action, "click", StringComparison.Ordinal) &&
            string.Equals(Normalize(semanticRole), "account", StringComparison.Ordinal) &&
            element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var accountExpand))
        {
            var expansion = (ExpandCollapsePattern)accountExpand;
            if (expansion.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            {
                expansion.Expand();
                return (true, "expand");
            }
        }

        if (string.Equals(action, "click", StringComparison.Ordinal) &&
            element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            return (true, "select");
        }

        if (action is "click" or "double_click" &&
            element.TryGetCurrentPattern(InvokePattern.Pattern, out var invocation))
        {
            ((InvokePattern)invocation).Invoke();
            return (true, "invoke");
        }

        if (string.Equals(action, "click", StringComparison.Ordinal) &&
            element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expansionPattern))
        {
            var expansion = (ExpandCollapsePattern)expansionPattern;
            if (expansion.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                expansion.Expand();
            else if (expansion.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                expansion.Collapse();
            else
                return (false, null);
            return (true, "expand_collapse");
        }
        return (false, null);
    }

    private static bool NamesMatch(string actual, string expected) =>
        string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);

    private static string Normalize(string value) =>
        Regex.Replace(
                value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),
                @"[^\p{L}\p{N}]+",
                string.Empty)
            .Trim();

    private sealed record TraversalNode(AutomationElement Element, string? NearestNamedParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
