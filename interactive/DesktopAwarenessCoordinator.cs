using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AshaLive;

/// <summary>
/// Maintains a cheap, local description of the person's current desktop
/// context. It does not capture pixels, write telemetry, contact a provider,
/// or control input. Higher layers may consume its latest scene without ever
/// blocking the desktop or ASHA's voice loop.
/// </summary>
public sealed class DesktopAwarenessCoordinator : IDisposable
{
    private const uint GaRoot = 2;
    private readonly object _gate = new();
    private Timer? _timer;
    private VisionPreference _mode = VisionPreference.Off;
    private AwarenessScene? _current;
    private int _sampling;
    private bool _disposed;

    public event Action<AwarenessScene>? SceneChanged;

    public AwarenessScene? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Start(VisionPreference mode)
    {
        ThrowIfDisposed();
        Stop();
        _mode = mode;
        if (mode == VisionPreference.Off) return;

        var interval = mode == VisionPreference.Live ? 250 : 900;
        _timer = new Timer(_ => Sample(), null, 0, interval);
    }

    public void SetMode(VisionPreference mode)
    {
        if (_disposed) return;
        if (_mode == mode && (_timer is not null || mode == VisionPreference.Off)) return;
        Start(mode);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _mode = VisionPreference.Off;
        lock (_gate) _current = null;
    }

    private void Sample()
    {
        if (_disposed || _mode == VisionPreference.Off || Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var previous = Current;
            var pointer = GetPointer();
            var foreground = DescribeRoot(GetForegroundWindow());
            var hovered = pointer is null ? null : DescribeRoot(WindowFromPoint(new NativePoint(pointer.X, pointer.Y)));

            // ASHA's own floating surface should not replace the application
            // context the person was already sharing with her.
            if (foreground?.IsAsha == true) foreground = previous?.Foreground;
            if (hovered?.IsAsha == true) hovered = null;

            var scene = new AwarenessScene(DateTime.UtcNow, _mode, foreground, hovered, pointer);
            var meaningful = IsMeaningfulChange(previous, scene);
            lock (_gate) _current = scene;
            if (meaningful) SceneChanged?.Invoke(scene);
        }
        catch
        {
            // Awareness is auxiliary. A transient Windows API failure must not
            // affect the person's desktop, voice conversation, or control.
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private static bool IsMeaningfulChange(AwarenessScene? previous, AwarenessScene current)
    {
        if (previous is null) return true;
        if (previous.Foreground?.Handle != current.Foreground?.Handle) return true;
        if (!string.Equals(previous.Foreground?.Title, current.Foreground?.Title, StringComparison.Ordinal)) return true;
        if (previous.Hovered?.Handle != current.Hovered?.Handle) return true;
        if (previous.Pointer is null || current.Pointer is null) return previous.Pointer != current.Pointer;

        var dx = previous.Pointer.X - current.Pointer.X;
        var dy = previous.Pointer.Y - current.Pointer.Y;
        return (dx * dx) + (dy * dy) >= 96 * 96;
    }

    private static AwarenessPoint? GetPointer() => GetCursorPos(out var point)
        ? new AwarenessPoint(point.X, point.Y)
        : null;

    private static AwarenessSurface? DescribeRoot(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        var root = GetAncestor(window, GaRoot);
        if (root != IntPtr.Zero) window = root;
        if (!IsWindowVisible(window) || !GetWindowRect(window, out var bounds)) return null;

        GetWindowThreadProcessId(window, out var processId);
        string processName;
        try { processName = processId == 0 ? "unknown" : Process.GetProcessById((int)processId).ProcessName; }
        catch { processName = "unknown"; }

        var title = ReadText(window);
        var windowClass = ReadClass(window);
        return new AwarenessSurface(
            window,
            processName,
            title,
            windowClass,
            bounds.Left,
            bounds.Top,
            Math.Max(0, bounds.Right - bounds.Left),
            Math.Max(0, bounds.Bottom - bounds.Top));
    }

    private static string ReadText(IntPtr window)
    {
        var buffer = new StringBuilder(512);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string ReadClass(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        _ = GetClassName(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DesktopAwarenessCoordinator));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

public sealed record AwarenessScene(
    DateTime TimestampUtc,
    VisionPreference Mode,
    AwarenessSurface? Foreground,
    AwarenessSurface? Hovered,
    AwarenessPoint? Pointer);

public sealed record AwarenessSurface(
    IntPtr Handle,
    string ProcessName,
    string Title,
    string WindowClass,
    int X,
    int Y,
    int Width,
    int Height)
{
    public bool IsAsha => string.Equals(ProcessName, "asha-live", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
}

public sealed record AwarenessPoint(int X, int Y);
