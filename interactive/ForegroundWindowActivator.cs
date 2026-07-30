using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AshaLive;

internal sealed record ForegroundWindowIdentity(
    int ProcessId,
    long WindowHandle,
    string ProcessName,
    string WindowTitle);

internal sealed record ForegroundActivationResult(
    string Outcome,
    bool ForegroundVerified,
    bool AlreadyForeground,
    bool TargetWasMinimized,
    int Attempts,
    ForegroundWindowIdentity? Before,
    ForegroundWindowIdentity? After)
{
    public bool InterruptedByAnotherWindow =>
        string.Equals(Outcome, "interrupted_by_foreground_change", StringComparison.Ordinal);
}

/// <summary>
/// Performs one bounded, human-visible Windows foreground transition and
/// verifies the operating-system state by window handle or process identity.
/// Application discovery and language interpretation deliberately live
/// elsewhere; this component accepts only an already-resolved native target.
/// </summary>
internal static class ForegroundWindowActivator
{
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(2);

    public static async Task<ForegroundActivationResult> ActivateAsync(
        IntPtr targetWindow,
        int targetProcessId,
        CancellationToken cancellationToken)
    {
        var before = CaptureForeground();
        var minimized = targetWindow != IntPtr.Zero &&
                        IsWindow(targetWindow) &&
                        IsIconic(targetWindow);

        if (targetWindow == IntPtr.Zero ||
            targetProcessId <= 0 ||
            !IsWindow(targetWindow) ||
            !IsWindowVisible(targetWindow))
        {
            return new ForegroundActivationResult(
                "target_unavailable",
                ForegroundVerified: false,
                AlreadyForeground: false,
                TargetWasMinimized: minimized,
                Attempts: 0,
                Before: before,
                After: CaptureForeground());
        }

        var initialForeground = GetForegroundWindow();
        if (ForegroundMatches(
                targetWindow,
                targetProcessId,
                initialForeground,
                ProcessIdForWindow(initialForeground)))
        {
            return new ForegroundActivationResult(
                "foreground_verified",
                ForegroundVerified: true,
                AlreadyForeground: true,
                TargetWasMinimized: minimized,
                Attempts: 0,
                Before: before,
                After: before);
        }

        var deadline = DateTime.UtcNow + ActivationTimeout;
        var attempts = 0;
        ForegroundWindowIdentity? lastObserved = before;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            TryActivateWindow(targetWindow);
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);

            var foregroundHandle = GetForegroundWindow();
            var foregroundProcessId = ProcessIdForWindow(foregroundHandle);
            lastObserved = CaptureWindow(foregroundHandle, foregroundProcessId);
            if (ForegroundMatches(
                    targetWindow,
                    targetProcessId,
                    foregroundHandle,
                    foregroundProcessId))
            {
                return new ForegroundActivationResult(
                    "foreground_verified",
                    ForegroundVerified: true,
                    AlreadyForeground: false,
                    TargetWasMinimized: minimized,
                    Attempts: attempts,
                    Before: before,
                    After: lastObserved);
            }

            // A different application becoming foreground after the request
            // is treated as human or system interruption. ASHA must not fight
            // the person by repeatedly stealing focus back.
            if (attempts > 1 &&
                before is not null &&
                lastObserved is not null &&
                lastObserved.WindowHandle != before.WindowHandle)
            {
                return new ForegroundActivationResult(
                    "interrupted_by_foreground_change",
                    ForegroundVerified: false,
                    AlreadyForeground: false,
                    TargetWasMinimized: minimized,
                    Attempts: attempts,
                    Before: before,
                    After: lastObserved);
            }
        }

        return new ForegroundActivationResult(
            "activation_rejected",
            ForegroundVerified: false,
            AlreadyForeground: false,
            TargetWasMinimized: minimized,
            Attempts: attempts,
            Before: before,
            After: lastObserved ?? CaptureForeground());
    }

    internal static bool ForegroundMatchesForTesting(
        long targetHandle,
        int targetProcessId,
        long foregroundHandle,
        int foregroundProcessId) =>
        ForegroundMatches(
            new IntPtr(targetHandle),
            targetProcessId,
            new IntPtr(foregroundHandle),
            foregroundProcessId);

    private static bool ForegroundMatches(
        IntPtr targetHandle,
        int targetProcessId,
        IntPtr foregroundHandle,
        int foregroundProcessId) =>
        foregroundHandle != IntPtr.Zero &&
        (foregroundHandle == targetHandle ||
         (targetProcessId > 0 && foregroundProcessId == targetProcessId));

    private static void TryActivateWindow(IntPtr window)
    {
        var currentThread = GetCurrentThreadId();
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(window, out _);
        var attachedForeground =
            foregroundThread != 0 &&
            foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget =
            targetThread != 0 &&
            targetThread != currentThread &&
            targetThread != foregroundThread &&
            AttachThreadInput(currentThread, targetThread, true);

        try
        {
            _ = ShowWindowAsync(window, SwRestore);
            _ = BringWindowToTop(window);
            _ = SetWindowPos(
                window,
                HwndTop,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpShowWindow);
            _ = SetForegroundWindow(window);
            _ = SetActiveWindow(window);
            _ = SetFocus(window);
        }
        finally
        {
            if (attachedTarget)
                _ = AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground)
                _ = AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static ForegroundWindowIdentity? CaptureForeground()
    {
        var handle = GetForegroundWindow();
        return CaptureWindow(handle, ProcessIdForWindow(handle));
    }

    private static ForegroundWindowIdentity? CaptureWindow(IntPtr handle, int processId)
    {
        if (handle == IntPtr.Zero || processId <= 0) return null;
        var title = ReadWindowTitle(handle);
        string processName;
        try
        {
            using var process = Process.GetProcessById(processId);
            processName = process.ProcessName;
        }
        catch
        {
            processName = string.Empty;
        }

        return new ForegroundWindowIdentity(
            processId,
            handle.ToInt64(),
            processName,
            title);
    }

    private static int ProcessIdForWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero ||
            GetWindowThreadProcessId(handle, out var processId) == 0 ||
            processId == 0)
            return 0;
        return unchecked((int)processId);
    }

    private static string ReadWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
}
