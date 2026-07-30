using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AshaLive;

internal sealed record RunningWindowInfo(
    int ProcessId,
    IntPtr Handle,
    string ProcessName,
    string WindowTitle)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(WindowTitle) ? ProcessName : WindowTitle;
}

internal sealed record WindowLifecycleResult(
    string Action,
    string RequestedName,
    string ProcessName,
    string WindowTitle,
    bool Verified,
    string Outcome = "state_verified",
    ForegroundActivationResult? Activation = null,
    bool ConfirmationRequired = false,
    string? ConfirmationWindow = null);

internal sealed class WindowResolutionException : InvalidOperationException
{
    public WindowResolutionException(
        string requestedName,
        IReadOnlyList<string> candidates,
        bool noRunningMatch)
        : base(noRunningMatch
            ? $"No currently open window matches {requestedName}."
            : $"More than one currently open window matches {requestedName}.")
    {
        RequestedName = requestedName;
        Candidates = candidates;
        NoRunningMatch = noRunningMatch;
    }

    public string RequestedName { get; }
    public IReadOnlyList<string> Candidates { get; }
    public bool NoRunningMatch { get; }
}

/// <summary>
/// Discovers and manages the top-level windows that are actually open on this
/// Windows desktop. It contains no application recipes: every candidate comes
/// from the live process/window inventory and is resolved by semantic name.
/// </summary>
internal static class WindowLifecycleManager
{
    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;

    public static async Task<WindowLifecycleResult> ExecuteAsync(
        string action,
        string requestedName,
        int deniedProcessId,
        CancellationToken cancellationToken)
    {
        var target = Resolve(requestedName, EnumerateRunningWindows(deniedProcessId));
        if (deniedProcessId > 0 && target.ProcessId == deniedProcessId)
            throw new DesktopActionAuthorizationException(
                "I can't manage my own protected window through general computer control.");
        switch (action)
        {
            case "activate_window":
                return await ActivateAsync(target, requestedName, cancellationToken).ConfigureAwait(false);
            case "close_window":
                return await CloseAsync(target, requestedName, cancellationToken).ConfigureAwait(false);
            case "minimize_window":
                return await ChangePlacementAsync(
                    target,
                    requestedName,
                    action,
                    SwMinimize,
                    () => IsIconic(target.Handle),
                    cancellationToken).ConfigureAwait(false);
            case "maximize_window":
                return await ChangePlacementAsync(
                    target,
                    requestedName,
                    action,
                    SwMaximize,
                    () => IsZoomed(target.Handle),
                    cancellationToken).ConfigureAwait(false);
            case "restore_window":
                return await ChangePlacementAsync(
                    target,
                    requestedName,
                    action,
                    SwRestore,
                    () => !IsIconic(target.Handle),
                    cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("Choose activate, close, minimize, maximize, or restore for a running window.");
        }
    }

    internal static GroundedEntityResolution ResolveForTesting(
        string requestedName,
        IEnumerable<(string ProcessName, string WindowTitle)> windows)
    {
        var candidates = windows
            .Select((window, index) => new RunningWindowInfo(
                index + 1,
                new IntPtr(index + 1),
                window.ProcessName,
                window.WindowTitle))
            .ToArray();
        return ResolveEntity(requestedName, candidates);
    }

    internal static bool IsDeniedWindowForTesting(int processId, int deniedProcessId) =>
        deniedProcessId > 0 && processId == deniedProcessId;

    private static RunningWindowInfo Resolve(
        string requestedName,
        IReadOnlyList<RunningWindowInfo> windows)
    {
        var query = requestedName?.Trim() ?? string.Empty;
        if (query.Length is < 1 or > 160)
            throw new InvalidOperationException("Choose a currently open window by its visible name.");

        if (IsForegroundReference(query))
        {
            var foreground = GetForegroundWindow();
            var foregroundMatch = windows.SingleOrDefault(window => window.Handle == foreground);
            if (foregroundMatch is not null) return foregroundMatch;
        }

        var resolution = ResolveEntity(query, windows);
        if (resolution.Kind == GroundedEntityResolutionKind.None ||
            resolution.Candidate is null)
            throw new WindowResolutionException(query, [], noRunningMatch: true);

        var alternatives = resolution.Alternatives
            .Select(candidate => candidate.Value)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(3)
            .ToArray();
        if (resolution.Kind != GroundedEntityResolutionKind.HighConfidence)
            throw new WindowResolutionException(query, alternatives, noRunningMatch: false);

        var matching = windows
            .Where(window => string.Equals(
                window.DisplayName,
                resolution.Candidate.Value,
                StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        if (matching.Length != 1)
            throw new WindowResolutionException(
                query,
                matching.Select(window => window.DisplayName).Distinct().Take(3).ToArray(),
                noRunningMatch: false);
        return matching[0];
    }

    private static bool IsForegroundReference(string value) =>
        Regex.IsMatch(
            value,
            @"^(?:(?:the\s+)?(?:current|active|foreground|this)\s+(?:application\s+)?window|(?:this|the current|the active)\s+(?:app|application|program)|(?:das\s+)?(?:aktuelle|aktive|dieses)\s+(?:app-?|anwendungs-?|programm-?)?fenster|diese\s+(?:app|anwendung|programm))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static GroundedEntityResolution ResolveEntity(
        string requestedName,
        IReadOnlyList<RunningWindowInfo> windows) =>
        GroundedEntityResolver.Resolve(
            requestedName,
            windows.Select(window => new GroundedEntityCandidate(
                window.DisplayName,
                Role: "window",
                ConfirmedAliases: [window.ProcessName])));

    private static IReadOnlyList<RunningWindowInfo> EnumerateRunningWindows(int deniedProcessId)
    {
        var windows = new List<RunningWindowInfo>();
        _ = EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var title = ReadWindowTitle(handle);
            if (title.Length == 0) return true;
            if (GetWindowThreadProcessId(handle, out var rawProcessId) == 0 ||
                rawProcessId == 0 ||
                rawProcessId == (uint)Environment.ProcessId ||
                (deniedProcessId > 0 && rawProcessId == (uint)deniedProcessId))
                return true;

            try
            {
                using var process = Process.GetProcessById((int)rawProcessId);
                windows.Add(new RunningWindowInfo(
                    (int)rawProcessId,
                    handle,
                    process.ProcessName,
                    title));
            }
            catch
            {
                // Protected and exiting processes are not usable windows.
            }
            return true;
        }, IntPtr.Zero);
        return windows
            .GroupBy(window => window.Handle)
            .Select(group => group.First())
            .ToArray();
    }

    private static async Task<WindowLifecycleResult> ActivateAsync(
        RunningWindowInfo target,
        string requestedName,
        CancellationToken cancellationToken)
    {
        var activation = await ForegroundWindowActivator.ActivateAsync(
            target.Handle,
            target.ProcessId,
            cancellationToken).ConfigureAwait(false);
        return new WindowLifecycleResult(
            "activate_window",
            requestedName,
            target.ProcessName,
            target.WindowTitle,
            activation.ForegroundVerified,
            activation.Outcome,
            activation);
    }

    private static async Task<WindowLifecycleResult> CloseAsync(
        RunningWindowInfo target,
        string requestedName,
        CancellationToken cancellationToken)
    {
        if (!PostMessage(target.Handle, WmClose, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException($"Windows did not accept the request to close {target.DisplayName}.");

        if (await WaitUntilAsync(
                () => !IsWindow(target.Handle),
                TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false))
            return Result("close_window", requestedName, target);

        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero &&
            foreground != target.Handle &&
            GetWindowThreadProcessId(foreground, out var foregroundProcessId) != 0 &&
            foregroundProcessId == (uint)target.ProcessId)
        {
            var confirmationTitle = ReadWindowTitle(foreground);
            return new WindowLifecycleResult(
                "close_window",
                requestedName,
                target.ProcessName,
                target.WindowTitle,
                Verified: false,
                Outcome: "confirmation_required",
                ConfirmationRequired: true,
                ConfirmationWindow: confirmationTitle);
        }

        throw new InvalidOperationException(
            $"{target.DisplayName} remained open, so ASHA has not claimed that it closed.");
    }

    private static async Task<WindowLifecycleResult> ChangePlacementAsync(
        RunningWindowInfo target,
        string requestedName,
        string action,
        int command,
        Func<bool> verify,
        CancellationToken cancellationToken)
    {
        _ = ShowWindowAsync(target.Handle, command);
        if (!await WaitUntilAsync(verify, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"Windows did not verify the requested change to {target.DisplayName}.");
        return Result(action, requestedName, target);
    }

    private static WindowLifecycleResult Result(
        string action,
        string requestedName,
        RunningWindowInfo target) =>
        new(
            action,
            requestedName,
            target.ProcessName,
            target.WindowTitle,
            Verified: true);

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition()) return true;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return condition();
    }

    private static string ReadWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0) return string.Empty;
        var buffer = new System.Text.StringBuilder(length + 1);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
}
