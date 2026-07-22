using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace AshaLive;

/// <summary>
/// A deliberately light local desktop observer. It samples a small scaled
/// copy of the virtual desktop into a short in-memory ring buffer. It never
/// writes a movie and never contacts a model. A caller must explicitly ask it
/// to preserve a small evidence bundle for an active ASHA session.
/// </summary>
public sealed class ScreenObserver : IDisposable
{
    private const int SampleWidth = 384;
    private const int RingCapacity = 16;
    private const int SourceCopy = 0x00CC0020;
    private readonly object _gate = new();
    private readonly Queue<ScreenSnapshot> _recent = [];
    private Timer? _timer;
    private VisionPreference _mode = VisionPreference.Off;
    private int _captureInProgress;
    private bool _disposed;
    private DateTime _lastChangeNotificationUtc;

    public event Action<LocalScreenChange>? MeaningfulChange;

    public void Start(VisionPreference mode)
    {
        ThrowIfDisposed();
        Stop();
        _mode = mode;
        if (mode == VisionPreference.Off) return;

        var interval = mode == VisionPreference.Live ? 250 : 900;
        _timer = new Timer(_ => SampleIntoMemory(), null, 120, interval);
    }

    public void SetMode(VisionPreference mode)
    {
        if (_disposed) return;
        if (_mode == mode && _timer is not null) return;
        Start(mode);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _mode = VisionPreference.Off;
        _lastChangeNotificationUtc = DateTime.MinValue;
        lock (_gate)
        {
            while (_recent.Count > 0) _recent.Dequeue().Dispose();
        }
    }

    public async Task<VisualEvidenceBundle?> PreserveEvidenceAsync(
        string sessionId,
        string reason,
        int? anchorX,
        int? anchorY,
        DesktopCaptureRegion? requestedRegion = null,
        CancellationToken cancellationToken = default)
    {
        if (_mode == VisionPreference.Off || string.IsNullOrWhiteSpace(sessionId)) return null;

        return await Task.Run(() => PreserveEvidence(sessionId, reason, anchorX, anchorY, requestedRegion, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<VisionAttachment?> CaptureCurrentContextAsync(int anchorX, int anchorY, CancellationToken cancellationToken = default)
    {
        if (_mode == VisionPreference.Off) return null;
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Ambient summaries need scene understanding, not pixel-perfect
            // OCR. Preserve the same 960 by 620 desktop region and visible
            // blue boundary, but send roughly half as many image pixels to the
            // provider. Explicit one-view evidence remains full resolution.
            using var context = CaptureContext(anchorX, anchorY, 672, 434);
            using var stream = new MemoryStream();
            context.Image.Save(stream, ImageFormat.Png);
            return new VisionAttachment(
                $"asha-live-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png",
                stream.ToArray(),
                context.Left,
                context.Top,
                context.Width,
                context.Height,
                context.Image.Width,
                context.Image.Height);
        }, cancellationToken).ConfigureAwait(false);
    }

    private VisualEvidenceBundle PreserveEvidence(
        string sessionId,
        string reason,
        int? anchorX,
        int? anchorY,
        DesktopCaptureRegion? requestedRegion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ScreenSnapshot? before;
        lock (_gate) before = _recent.Count > 0 ? _recent.Last().Copy() : null;

        using var after = CaptureSnapshot();
        var changedScore = before is null ? 0d : Difference(before.Image, after.Image);
        AddToRing(after.Copy());

        var relativeDirectory = Path.Combine("sessions", sessionId, "evidence");
        var absoluteDirectory = Path.Combine(RuntimeDirectory(), relativeDirectory);
        Directory.CreateDirectory(absoluteDirectory);
        var safeReason = string.Concat(reason.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safeReason)) safeReason = "view";
        var stem = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{safeReason}-{Guid.NewGuid():N}";

        string? beforeFile = null;
        if (before is not null)
        {
            using (before)
            {
                cancellationToken.ThrowIfCancellationRequested();
                beforeFile = Path.Combine(relativeDirectory, $"{stem}-before.png");
                before.Image.Save(Path.Combine(RuntimeDirectory(), beforeFile), ImageFormat.Png);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var afterFile = Path.Combine(relativeDirectory, $"{stem}-after.png");
        after.Image.Save(Path.Combine(RuntimeDirectory(), afterFile), ImageFormat.Png);

        string? contextFile = null;
        int? contextX = null;
        int? contextY = null;
        int? contextWidth = null;
        int? contextHeight = null;
        int? contextPixelWidth = null;
        int? contextPixelHeight = null;
        if (requestedRegion is not null || (anchorX.HasValue && anchorY.HasValue))
        {
            using var context = requestedRegion is not null
                ? CaptureRegion(requestedRegion)
                : CaptureContext(anchorX!.Value, anchorY!.Value);
            contextFile = Path.Combine(relativeDirectory, $"{stem}-context.png");
            context.Image.Save(Path.Combine(RuntimeDirectory(), contextFile), ImageFormat.Png);
            contextX = context.Left;
            contextY = context.Top;
            contextWidth = context.Width;
            contextHeight = context.Height;
            contextPixelWidth = context.Image.Width;
            contextPixelHeight = context.Image.Height;
        }

        return new VisualEvidenceBundle(
            ToLedgerPath(beforeFile),
            afterFile.Replace('\\', '/'),
            ToLedgerPath(contextFile),
            Math.Round(changedScore, 4),
            contextX, contextY, contextWidth, contextHeight,
            contextPixelWidth, contextPixelHeight);
    }

    private void SampleIntoMemory()
    {
        if (_disposed || _mode == VisionPreference.Off || Interlocked.Exchange(ref _captureInProgress, 1) == 1) return;
        try
        {
            ScreenSnapshot? previous;
            lock (_gate) previous = _recent.Count > 0 ? _recent.Last().Copy() : null;
            var current = CaptureSnapshot();
            var changedScore = previous is null ? 1d : Difference(previous.Image, current.Image);
            previous?.Dispose();
            AddToRing(current);

            var now = DateTime.UtcNow;
            if (changedScore >= 0.035 && now - _lastChangeNotificationUtc >= TimeSpan.FromSeconds(2.5))
            {
                _lastChangeNotificationUtc = now;
                MeaningfulChange?.Invoke(new LocalScreenChange(now, Math.Round(changedScore, 4)));
            }
        }
        catch
        {
            // Observation is optional. A transient desktop-composition error
            // must never disturb the person's work or ASHA conversation.
        }
        finally
        {
            Interlocked.Exchange(ref _captureInProgress, 0);
        }
    }

    private void AddToRing(ScreenSnapshot snapshot)
    {
        lock (_gate)
        {
            _recent.Enqueue(snapshot);
            while (_recent.Count > RingCapacity) _recent.Dequeue().Dispose();
        }
    }

    private static ScreenSnapshot CaptureSnapshot()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        var height = Math.Max(1, (int)Math.Round(SampleWidth * (bounds.Height / (double)Math.Max(1, bounds.Width))));
        return new ScreenSnapshot(DateTime.UtcNow, CaptureScaled(bounds.Left, bounds.Top, bounds.Width, bounds.Height, SampleWidth, height));
    }

    private static ContextCapture CaptureContext(int anchorX, int anchorY, int? outputWidth = null, int? outputHeight = null)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        const int desiredWidth = 960;
        const int desiredHeight = 620;
        var width = Math.Min(desiredWidth, bounds.Width);
        var height = Math.Min(desiredHeight, bounds.Height);
        var left = Math.Clamp(anchorX - (width / 2), bounds.Left, bounds.Right - width);
        var top = Math.Clamp(anchorY - (height / 2), bounds.Top, bounds.Bottom - height);
        var targetWidth = Math.Clamp(outputWidth ?? width, 1, width);
        var targetHeight = Math.Clamp(outputHeight ?? height, 1, height);
        return new ContextCapture(CaptureScaled(left, top, width, height, targetWidth, targetHeight), left, top, width, height);
    }

    private static ContextCapture CaptureRegion(DesktopCaptureRegion requested)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        var left = Math.Clamp(requested.X, bounds.Left, bounds.Right - 1);
        var top = Math.Clamp(requested.Y, bounds.Top, bounds.Bottom - 1);
        var right = Math.Clamp((long)requested.X + requested.Width, left + 1L, bounds.Right);
        var bottom = Math.Clamp((long)requested.Y + requested.Height, top + 1L, bounds.Bottom);
        var width = Math.Max(1, (int)(right - left));
        var height = Math.Max(1, (int)(bottom - top));
        // A foreground-window request is commonly used to locate text-sized
        // controls. Preserve a little more detail there, while broad screen
        // scans keep the lower token and bandwidth budget.
        var maxWidth = requested.PreferTextDetail ? 1600d : 1280d;
        var maxHeight = requested.PreferTextDetail ? 1000d : 800d;
        var scale = Math.Min(1d, Math.Min(maxWidth / width, maxHeight / height));
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        return new ContextCapture(CaptureScaled(left, top, width, height, targetWidth, targetHeight), left, top, width, height);
    }

    private static Bitmap CaptureScaled(int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var bitmap = new Bitmap(Math.Max(1, targetWidth), Math.Max(1, targetHeight), PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var destination = graphics.GetHdc();
        var source = GetDC(IntPtr.Zero);
        try
        {
            if (source == IntPtr.Zero || !StretchBlt(destination, 0, 0, targetWidth, targetHeight, source, sourceX, sourceY, sourceWidth, sourceHeight, SourceCopy))
                throw new InvalidOperationException("Windows could not capture the current desktop view.");
        }
        finally
        {
            if (source != IntPtr.Zero) ReleaseDC(IntPtr.Zero, source);
            graphics.ReleaseHdc(destination);
        }
        return bitmap;
    }

    private static double Difference(Bitmap first, Bitmap second)
    {
        using var left = new Bitmap(first);
        using var right = new Bitmap(second);
        var total = 0d;
        var samples = 0;
        for (var y = 0; y < left.Height; y += 8)
        {
            for (var x = 0; x < left.Width; x += 8)
            {
                var a = left.GetPixel(x, y);
                var b = right.GetPixel(x, y);
                total += (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / (255d * 3d);
                samples++;
            }
        }
        return samples == 0 ? 0 : total / samples;
    }

    private static string RuntimeDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "asha");

    private static string? ToLedgerPath(string? path) => path?.Replace('\\', '/');

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScreenObserver));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private sealed record ScreenSnapshot(DateTime At, Bitmap Image) : IDisposable
    {
        public ScreenSnapshot Copy() => new(At, new Bitmap(Image));
        public void Dispose() => Image.Dispose();
    }

    private sealed record ContextCapture(Bitmap Image, int Left, int Top, int Width, int Height) : IDisposable
    {
        public void Dispose() => Image.Dispose();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr destination, int destinationX, int destinationY, int destinationWidth, int destinationHeight, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int rasterOperation);
}

public sealed record VisualEvidenceBundle(
    string? BeforeFile,
    string AfterFile,
    string? ContextFile,
    double ChangedScore,
    int? ContextX,
    int? ContextY,
    int? ContextWidth,
    int? ContextHeight,
    int? ContextPixelWidth,
    int? ContextPixelHeight);

public sealed record DesktopCaptureRegion(int X, int Y, int Width, int Height, bool PreferTextDetail = false);

public sealed record LocalScreenChange(DateTime TimestampUtc, double ChangedScore);
