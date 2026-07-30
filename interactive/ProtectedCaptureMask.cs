using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AshaLive;

/// <summary>
/// Removes every visible top-level window owned by ASHA from a desktop capture.
/// Bounds are collected both before and after acquisition by the caller so a
/// moving protected window cannot expose the old or new location.
/// </summary>
internal sealed class ProtectedCaptureMask
{
    private static readonly Color RedactionColor = Color.FromArgb(255, 17, 24, 39);
    private readonly int _protectedProcessId;

    public ProtectedCaptureMask(int? protectedProcessId = null)
    {
        _protectedProcessId = protectedProcessId ?? Environment.ProcessId;
    }

    public IReadOnlyList<Rectangle> CaptureVisibleProtectedBounds()
    {
        var bounds = new List<Rectangle>();
        var completed = EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId != _protectedProcessId ||
                !GetWindowRect(window, out var native) ||
                native.Right <= native.Left ||
                native.Bottom <= native.Top)
                return true;

            bounds.Add(Rectangle.FromLTRB(
                native.Left,
                native.Top,
                native.Right,
                native.Bottom));
            return true;
        }, IntPtr.Zero);

        if (!completed)
            throw new InvalidOperationException(
                "Windows could not enumerate ASHA's protected windows, so the desktop image was not captured.");
        return bounds;
    }

    public static void Apply(
        Bitmap image,
        Rectangle sourceBounds,
        IEnumerable<Rectangle> protectedDesktopBounds)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceBounds));

        var mapped = protectedDesktopBounds
            .Select(bounds => MapToCapture(sourceBounds, image.Size, bounds))
            .Where(bounds => !bounds.IsEmpty)
            .Distinct()
            .ToArray();
        if (mapped.Length == 0) return;

        using var graphics = Graphics.FromImage(image);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using var brush = new SolidBrush(RedactionColor);
        foreach (var bounds in mapped)
            graphics.FillRectangle(brush, bounds);
    }

    internal static Rectangle MapToCaptureForTesting(
        Rectangle sourceBounds,
        Size outputSize,
        Rectangle protectedDesktopBounds) =>
        MapToCapture(sourceBounds, outputSize, protectedDesktopBounds);

    internal static Color RedactionColorForTesting => RedactionColor;

    private static Rectangle MapToCapture(
        Rectangle sourceBounds,
        Size outputSize,
        Rectangle protectedDesktopBounds)
    {
        if (outputSize.Width <= 0 || outputSize.Height <= 0) return Rectangle.Empty;
        var intersection = Rectangle.Intersect(sourceBounds, protectedDesktopBounds);
        if (intersection.IsEmpty) return Rectangle.Empty;

        var scaleX = outputSize.Width / (double)sourceBounds.Width;
        var scaleY = outputSize.Height / (double)sourceBounds.Height;
        // Floor the leading edge and ceil the trailing edge. The one-pixel
        // padding covers scaling/filtering at a protected window boundary.
        var left = (int)Math.Floor((intersection.Left - sourceBounds.Left) * scaleX) - 1;
        var top = (int)Math.Floor((intersection.Top - sourceBounds.Top) * scaleY) - 1;
        var right = (int)Math.Ceiling((intersection.Right - sourceBounds.Left) * scaleX) + 1;
        var bottom = (int)Math.Ceiling((intersection.Bottom - sourceBounds.Top) * scaleY) + 1;

        left = Math.Clamp(left, 0, outputSize.Width);
        top = Math.Clamp(top, 0, outputSize.Height);
        right = Math.Clamp(right, left, outputSize.Width);
        bottom = Math.Clamp(bottom, top, outputSize.Height);
        return right > left && bottom > top
            ? Rectangle.FromLTRB(left, top, right, bottom)
            : Rectangle.Empty;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
}
