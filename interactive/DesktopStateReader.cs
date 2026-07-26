using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AshaLive;

internal sealed record DesktopStateInspectionFailure(
    DateTime AtUtc,
    int? ProcessId,
    string ProcessName,
    string Reason,
    TimeSpan Cooldown);

/// <summary>
/// Reads Windows UI Automation through a disposable helper process. A broken
/// accessibility provider can therefore stall only that helper, never ASHA's
/// orb, speech loop, or control surface.
/// </summary>
internal sealed class DesktopStateReader
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    private readonly string _workerPath;
    private readonly TimeSpan _timeout;
    private readonly object _cooldownGate = new();
    private readonly Dictionary<int, DateTime> _cooldowns = [];

    public DesktopStateReader(string? workerPath = null, TimeSpan? timeout = null)
    {
        _workerPath = workerPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "uia-worker",
            "asha-uia-worker.exe");
        _timeout = timeout ?? DefaultTimeout;
    }

    public event Action<DesktopStateInspectionFailure>? InspectionFailed;

    public Task<DesktopStateSnapshot?> CaptureForegroundAsync(
        CancellationToken cancellationToken = default) =>
        CaptureForegroundAsync(deniedProcessId: 0, cancellationToken);

    public Task<DesktopStateSnapshot?> CaptureForegroundAsync(
        int deniedProcessId,
        CancellationToken cancellationToken = default)
    {
        var window = GetForegroundWindow();
        var processId = ProcessIdForWindow(window);
        if (IsDeniedProcess(processId, deniedProcessId))
            return Task.FromResult<DesktopStateSnapshot?>(null);
        return RunWorkerAsync<DesktopStateSnapshot>(
            ["foreground", deniedProcessId.ToString()],
            processId,
            cancellationToken);
    }

    public Task<DesktopStateSnapshot?> CaptureAtPointAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default) =>
        CaptureAtPointAsync(x, y, deniedProcessId: 0, cancellationToken);

    public Task<DesktopStateSnapshot?> CaptureAtPointAsync(
        int x,
        int y,
        int deniedProcessId,
        CancellationToken cancellationToken = default)
    {
        var window = WindowFromPoint(new NativePoint(x, y));
        var processId = ProcessIdForWindow(window);
        if (IsDeniedProcess(processId, deniedProcessId))
            return Task.FromResult<DesktopStateSnapshot?>(null);
        return RunWorkerAsync<DesktopStateSnapshot>(
            ["point", x.ToString(), y.ToString(), deniedProcessId.ToString()],
            processId,
            cancellationToken);
    }

    public async Task<DesktopAccessibleActionResult> TryExecuteAccessibleActionAsync(
        int x,
        int y,
        string action,
        string expectedName,
        string? expectedRole,
        string? semanticRole,
        int deniedProcessId,
        CancellationToken cancellationToken = default)
    {
        var processId = ProcessIdForWindow(WindowFromPoint(new NativePoint(x, y)));
        if (IsDeniedProcess(processId, deniedProcessId))
            return UnavailableAction(expectedName, expectedRole, x, y, mayHaveExecuted: false);
        // A cooldown means the helper is deliberately not started. Nothing
        // could have reached the application, so a physical fallback cannot
        // duplicate an action. Reserve Uncertain for a helper that may have
        // reached the accessibility provider before failing or timing out.
        if (processId is { } knownProcessId && IsCoolingDown(knownProcessId))
            return UnavailableAction(expectedName, expectedRole, x, y, mayHaveExecuted: false);
        if (!File.Exists(_workerPath))
        {
            ReportFailure(processId, "worker_unavailable", TimeSpan.Zero);
            return UnavailableAction(expectedName, expectedRole, x, y, mayHaveExecuted: false);
        }

        var result = await RunWorkerAsync<DesktopAccessibleActionResult>(
            [
                "act",
                x.ToString(),
                y.ToString(),
                action,
                expectedName,
                expectedRole ?? string.Empty,
                semanticRole ?? string.Empty,
                deniedProcessId.ToString(),
            ],
            processId,
            cancellationToken);
        return result ?? UnavailableAction(
            expectedName,
            expectedRole,
            x,
            y,
            mayHaveExecuted: true);
    }

    private static DesktopAccessibleActionResult UnavailableAction(
        string expectedName,
        string? expectedRole,
        int x,
        int y,
        bool mayHaveExecuted) =>
        new(
            Executed: false,
            Uncertain: mayHaveExecuted,
            Pattern: null,
            Name: expectedName,
            Role: expectedRole,
            X: x,
            Y: y,
            Width: 0,
            Height: 0);

    internal static DesktopAccessibleActionResult UnavailableActionForTesting(bool mayHaveExecuted) =>
        UnavailableAction("Test target", "button", 10, 20, mayHaveExecuted);

    internal static bool IsDeniedProcessForTesting(int? processId, int deniedProcessId) =>
        IsDeniedProcess(processId, deniedProcessId);

    internal bool IsCoolingDownForTesting(int processId, DateTime nowUtc)
    {
        lock (_cooldownGate)
            return _cooldowns.TryGetValue(processId, out var until) && until > nowUtc;
    }

    private async Task<T?> RunWorkerAsync<T>(
        IReadOnlyList<string> arguments,
        int? processId,
        CancellationToken cancellationToken)
        where T : class
    {
        if (processId is { } knownProcessId && IsCoolingDown(knownProcessId))
            return null;

        if (!File.Exists(_workerPath))
        {
            ReportFailure(processId, "worker_unavailable", TimeSpan.Zero);
            return null;
        }

        var start = new ProcessStartInfo
        {
            FileName = _workerPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process? worker = null;
        try
        {
            worker = Process.Start(start);
            if (worker is null)
            {
                ReportFailure(processId, "worker_did_not_start", FailureCooldown);
                BeginCooldown(processId);
                return null;
            }

            var standardOutput = worker.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = worker.StandardError.ReadToEndAsync(cancellationToken);
            var completion = worker.WaitForExitAsync(CancellationToken.None);
            var timeout = Task.Delay(_timeout, CancellationToken.None);
            var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var finished = await Task.WhenAny(completion, timeout, cancelled).ConfigureAwait(false);

            if (finished == cancelled)
            {
                StopWorker(worker);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (finished == timeout)
            {
                StopWorker(worker);
                BeginCooldown(processId);
                ReportFailure(processId, "accessibility_timeout", FailureCooldown);
                return null;
            }

            await completion.ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            if (worker.ExitCode != 0)
            {
                BeginCooldown(processId);
                ReportFailure(
                    processId,
                    string.IsNullOrWhiteSpace(error) ? "worker_failed" : "accessibility_provider_failed",
                    FailureCooldown);
                return null;
            }

            var result = JsonSerializer.Deserialize<T>(
                output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result is DesktopStateSnapshot snapshot)
                ClearCooldown(snapshot.ProcessId);
            else if (result is not null && processId is { } successfulProcessId)
                ClearCooldown(successfulProcessId);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (worker is not null) StopWorker(worker);
            throw;
        }
        catch (Exception)
        {
            if (worker is not null) StopWorker(worker);
            BeginCooldown(processId);
            ReportFailure(processId, "worker_failed", FailureCooldown);
            return null;
        }
        finally
        {
            worker?.Dispose();
        }
    }

    private bool IsCoolingDown(int processId)
    {
        lock (_cooldownGate)
        {
            if (!_cooldowns.TryGetValue(processId, out var until)) return false;
            if (until > DateTime.UtcNow) return true;
            _cooldowns.Remove(processId);
            return false;
        }
    }

    private void BeginCooldown(int? processId)
    {
        if (processId is not { } value || value <= 0) return;
        lock (_cooldownGate) _cooldowns[value] = DateTime.UtcNow + FailureCooldown;
    }

    private void ClearCooldown(int processId)
    {
        lock (_cooldownGate) _cooldowns.Remove(processId);
    }

    private void ReportFailure(int? processId, string reason, TimeSpan cooldown)
    {
        var processName = "unknown application";
        if (processId is { } value)
        {
            try
            {
                using var process = Process.GetProcessById(value);
                processName = process.ProcessName;
            }
            catch
            {
                processName = $"process {value}";
            }
        }
        InspectionFailed?.Invoke(new DesktopStateInspectionFailure(
            DateTime.UtcNow,
            processId,
            processName,
            reason,
            cooldown));
    }

    private static int? ProcessIdForWindow(IntPtr window)
    {
        if (window == IntPtr.Zero ||
            GetWindowThreadProcessId(window, out var processId) == 0 ||
            processId == 0)
            return null;
        return (int)processId;
    }

    private static bool IsDeniedProcess(int? processId, int deniedProcessId) =>
        deniedProcessId > 0 &&
        processId is { } value &&
        value == deniedProcessId;

    private static void StopWorker(Process worker)
    {
        try
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
        }
        catch
        {
            // The helper may finish between the timeout and termination.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
