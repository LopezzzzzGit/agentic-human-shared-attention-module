using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AshaLive;

/// <summary>
/// Runtime availability of the optional CUA desktop driver. Permission to use
/// a capability and availability of its executor are deliberately separate.
/// </summary>
public sealed record ComputerControlRuntimeStatus(
    bool CuaExecutableAvailable,
    bool CuaDaemonRunning,
    string? CuaVersion = null,
    string? Diagnostic = null)
{
    public bool CuaConnected => CuaExecutableAvailable && CuaDaemonRunning;

    public static ComputerControlRuntimeStatus Unavailable(string reason) =>
        new(false, false, Diagnostic: reason);
}

internal sealed record CuaActionTarget(
    int ProcessId,
    long WindowId,
    int WindowX,
    int WindowY,
    int WindowWidth,
    int WindowHeight);

internal sealed record CuaExecutionResult(
    bool Executed,
    bool Uncertain,
    bool BackgroundUnavailable,
    string Executor,
    string? Error = null);

/// <summary>
/// Dependency-free adapter around cua-driver's stable CLI surface. It never
/// selects policy and never escalates to foreground delivery: the caller must
/// make any physical-input decision separately and visibly.
/// </summary>
internal sealed class CuaDriverClient
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VisibleCursorArrivalDelay = TimeSpan.FromMilliseconds(420);
    private readonly string? _executablePath;
    private readonly ProtectedSurfacePolicy _protectedSurfaces;

    public CuaDriverClient(ProtectedSurfacePolicy protectedSurfaces)
    {
        _protectedSurfaces = protectedSurfaces;
        _executablePath = ResolveExecutablePath();
        Status = _executablePath is null
            ? ComputerControlRuntimeStatus.Unavailable(
                "CUA Driver is not installed. Virtual-cursor demonstration remains available, but background interaction does not.")
            : new ComputerControlRuntimeStatus(
                CuaExecutableAvailable: true,
                CuaDaemonRunning: false,
                Diagnostic: "CUA Driver has not been checked yet.");
    }

    public ComputerControlRuntimeStatus Status { get; private set; }

    internal bool IsProtectedTargetForTesting(int processId) =>
        _protectedSurfaces.IsProtectedProcess(processId);

    public async Task<ComputerControlRuntimeStatus> RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_executablePath is null)
            return Status;

        var probe = await RunProcessAsync(["status"], StatusTimeout, cancellationToken).ConfigureAwait(false);
        if (probe.TimedOut)
        {
            Status = new ComputerControlRuntimeStatus(
                true,
                false,
                Diagnostic: "CUA Driver did not answer its local status check.");
            return Status;
        }

        var running = probe.ExitCode == 0 &&
                      probe.StandardOutput.Contains("running", StringComparison.OrdinalIgnoreCase);
        string? version = null;
        if (running)
        {
            var config = await CallAsync("get_config", new { }, StatusTimeout, cancellationToken).ConfigureAwait(false);
            if (config.ExitCode == 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(config.StandardOutput);
                    if (document.RootElement.TryGetProperty("version", out var value))
                        version = value.GetString();
                }
                catch
                {
                    // Version is useful diagnostics, never a connectivity gate.
                }
            }
        }

        Status = new ComputerControlRuntimeStatus(
            CuaExecutableAvailable: true,
            CuaDaemonRunning: running,
            CuaVersion: version,
            Diagnostic: running
                ? "CUA Driver is connected for background virtual-pointer actions."
                : CleanError(probe) ?? "CUA Driver is installed but its local daemon is not running.");
        return Status;
    }

    public async Task StartSessionAsync(
        string sessionId,
        bool cursorVisible,
        CancellationToken cancellationToken = default)
    {
        if (!Status.CuaConnected) return;
        var started = await CallAsync(
            "start_session",
            new { session = sessionId },
            ActionTimeout,
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(started, "CUA Driver could not start its virtual-cursor session.");

        var visibility = await CallAsync(
            "set_agent_cursor_enabled",
            new { enabled = cursorVisible, cursor_id = sessionId },
            ActionTimeout,
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(visibility, "CUA Driver could not apply the virtual-cursor visibility setting.");
    }

    public async Task EndSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_executablePath is null) return;
        var ended = await CallAsync(
            "end_session",
            new { session = sessionId },
            StatusTimeout,
            cancellationToken).ConfigureAwait(false);
        if (ended.TimedOut)
            return;
    }

    /// <summary>
    /// Moves only the human-facing agent cursor, then allows its glide to
    /// become legible before a separate semantic or background interaction.
    /// The overlay is presentation and never counts as proof of input.
    /// </summary>
    public async Task<CuaExecutionResult> PresentCursorAsync(
        int x,
        int y,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!Status.CuaConnected)
            return new CuaExecutionResult(
                false,
                false,
                false,
                "cua_virtual_cursor",
                Status.Diagnostic ?? "CUA Driver is not connected.");

        var moved = await CallAsync(
            "move_cursor",
            new { x, y, session = sessionId, cursor_id = sessionId },
            ActionTimeout,
            cancellationToken).ConfigureAwait(false);
        var result = Classify(moved, "cua_virtual_cursor");
        if (result.Executed)
            await Task.Delay(VisibleCursorArrivalDelay, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal static int VisibleCursorArrivalDelayMillisecondsForTesting =>
        (int)VisibleCursorArrivalDelay.TotalMilliseconds;

    public async Task<CuaExecutionResult> ExecuteAsync(
        DesktopAction action,
        ProtectedSurfacePolicy.DesktopActionPermit permit,
        CuaActionTarget? target,
        string sessionId,
        bool cursorVisible,
        CancellationToken cancellationToken)
    {
        if (!_protectedSurfaces.TryValidatePermit(permit, action, out var permitError))
            return Invalid(permitError);
        if (!Status.CuaConnected)
            return new CuaExecutionResult(
                false,
                false,
                false,
                "cua_background",
                Status.Diagnostic ?? "CUA Driver is not connected.");

        if (action.Kind == "move")
        {
            if (!action.X.HasValue || !action.Y.HasValue)
                return Invalid("A virtual cursor move needs a mapped desktop point.");
            if (!cursorVisible)
                return Invalid("The virtual cursor is hidden, so a demonstration-only move would have no visible result.");

            var moved = await CallAsync(
                "move_cursor",
                new { x = action.X.Value, y = action.Y.Value, session = sessionId, cursor_id = sessionId },
                ActionTimeout,
                cancellationToken).ConfigureAwait(false);
            return Classify(moved, "cua_virtual_cursor");
        }

        if (target is null || target.ProcessId <= 0 || target.WindowId == 0)
            return Invalid("CUA Driver needs the current top-layer window identity before background interaction.");
        if (!_protectedSurfaces.TryValidateCurrentSurface(
                permit,
                action,
                new DesktopSurfaceIdentity(
                    target.ProcessId,
                    target.WindowId,
                    string.Empty,
                    string.Empty,
                    string.Empty),
                out var targetError))
            return Invalid(targetError);

        if (cursorVisible && action.X.HasValue && action.Y.HasValue)
        {
            var cursorResult = await PresentCursorAsync(
                action.X.Value,
                action.Y.Value,
                sessionId,
                cancellationToken).ConfigureAwait(false);
            if (cursorResult.Uncertain)
                return cursorResult;
            // The cursor is a human-facing indicator. A rendering failure does
            // not silently change which interaction executor is used.
        }

        ProcessResult result;
        switch (action.Kind)
        {
            case "click":
            case "double_click":
            case "right_click":
                if (!action.X.HasValue || !action.Y.HasValue)
                    return Invalid("A background click needs a mapped desktop point.");
                result = await CallAsync(
                    "click",
                    new
                    {
                        pid = target.ProcessId,
                        window_id = target.WindowId,
                        x = LocalX(action.X.Value, target),
                        y = LocalY(action.Y.Value, target),
                        button = action.Kind == "right_click" ? "right" : "left",
                        count = action.Kind == "double_click" ? 2 : 1,
                        delivery_mode = "background",
                        session = sessionId,
                    },
                    ActionTimeout,
                    cancellationToken).ConfigureAwait(false);
                break;

            case "drag":
                if (!action.X.HasValue || !action.Y.HasValue ||
                    !action.EndX.HasValue || !action.EndY.HasValue)
                    return Invalid("A background drag needs two mapped desktop points.");
                result = await CallAsync(
                    "drag",
                    new
                    {
                        pid = target.ProcessId,
                        window_id = target.WindowId,
                        from_x = LocalX(action.X.Value, target),
                        from_y = LocalY(action.Y.Value, target),
                        to_x = LocalX(action.EndX.Value, target),
                        to_y = LocalY(action.EndY.Value, target),
                        duration_ms = 500,
                        steps = 20,
                        delivery_mode = "background",
                        session = sessionId,
                    },
                    ActionTimeout,
                    cancellationToken).ConfigureAwait(false);
                break;

            case "scroll":
                if (!action.Delta.HasValue || action.Delta.Value == 0)
                    return Invalid("A background scroll needs a non-zero delta.");
                result = await CallAsync(
                    "scroll",
                    new
                    {
                        pid = target.ProcessId,
                        window_id = target.WindowId,
                        direction = action.Delta.Value > 0 ? "up" : "down",
                        amount = Math.Clamp((int)Math.Ceiling(Math.Abs(action.Delta.Value) / 120d), 1, 50),
                        by = "line",
                        delivery_mode = "background",
                        session = sessionId,
                    },
                    ActionTimeout,
                    cancellationToken).ConfigureAwait(false);
                break;

            default:
                return Invalid($"CUA Driver does not expose '{action.Kind}' through ASHA's virtual-pointer path.");
        }

        return Classify(result, "cua_background");
    }

    private static int LocalX(int screenX, CuaActionTarget target) =>
        Math.Clamp(screenX - target.WindowX, 0, Math.Max(0, target.WindowWidth - 1));

    private static int LocalY(int screenY, CuaActionTarget target) =>
        Math.Clamp(screenY - target.WindowY, 0, Math.Max(0, target.WindowHeight - 1));

    private static CuaExecutionResult Invalid(string error) =>
        new(false, false, false, "cua_background", error);

    private static CuaExecutionResult Classify(ProcessResult result, string executor)
    {
        if (result.TimedOut)
            return new CuaExecutionResult(
                false,
                true,
                false,
                executor,
                "CUA Driver timed out. The action might have reached the target, so ASHA stopped before sending any fallback input.");

        var combined = $"{result.StandardOutput}\n{result.StandardError}";
        var backgroundUnavailable = combined.Contains("background_unavailable", StringComparison.OrdinalIgnoreCase);
        var stale = combined.Contains("\"stale\"", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("stale element", StringComparison.OrdinalIgnoreCase);
        var explicitError = result.ExitCode != 0 || HasStructuredError(result.StandardOutput);
        if (!explicitError)
            return new CuaExecutionResult(true, false, false, executor);

        return new CuaExecutionResult(
            false,
            false,
            backgroundUnavailable,
            executor,
            stale
                ? "CUA Driver rejected stale target evidence. ASHA must inspect the current window before retrying."
                : CleanError(result) ?? "CUA Driver did not complete the background interaction.");
    }

    private static bool HasStructuredError(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("ok", out var ok) &&
                ok.ValueKind == JsonValueKind.False)
                return true;
            if (!root.TryGetProperty("error", out var error) ||
                error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return false;
            return error.ValueKind != JsonValueKind.String ||
                   !string.IsNullOrWhiteSpace(error.GetString());
        }
        catch
        {
            return false;
        }
    }

    private async Task<ProcessResult> CallAsync(
        string tool,
        object arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(arguments);
        return await RunProcessAsync(["call", tool, json], timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessResult> RunProcessAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_executablePath is null)
            return new ProcessResult(-1, string.Empty, "CUA Driver is not installed.", false);

        var start = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            return new ProcessResult(-1, string.Empty, "Windows could not start CUA Driver.", false);

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new ProcessResult(
                -1,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false),
                true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ProcessResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false),
            false);
    }

    private static void ThrowIfFailed(ProcessResult result, string fallback)
    {
        if (result.ExitCode == 0 && !result.TimedOut) return;
        throw new InvalidOperationException(CleanError(result) ?? fallback);
    }

    private static string? CleanError(ProcessResult result)
    {
        var value = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        value = value.Trim();
        if (value.Length == 0) return null;
        return value.Length <= 320 ? value : value[..319] + "…";
    }

    private static string? ResolveExecutablePath()
    {
        var configured = Environment.GetEnvironmentVariable("ASHA_CUA_DRIVER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var standard = Path.Combine(local, "Programs", "Cua", "cua-driver", "bin", "cua-driver.exe");
        return File.Exists(standard) ? standard : null;
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut);
}
