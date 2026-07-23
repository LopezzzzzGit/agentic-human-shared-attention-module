using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AshaOverlay;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        OverlayMark? mark;
        try { mark = JsonSerializer.Deserialize<OverlayMark>(args[0], JsonOptions); }
        catch { return 2; }
        if (mark is null) return 2;

        var application = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
        application.Run(new OverlayWindow(mark));
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

internal sealed record OverlayMark(
    string Id,
    string Kind,
    double X,
    double Y,
    double? W,
    double? H,
    string? Label,
    string Color,
    bool Editable = false,
    string? EventDirectory = null);

internal sealed class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private readonly OverlayMark _mark;
    private readonly double _shapeWidth;
    private readonly double _shapeHeight;
    private FrameworkElement _shape = null!;
    private bool _dragging;
    private NativePoint _dragOriginScreen;
    private NativePoint _dragLastScreen;
    private NativePoint _dragWindowOrigin;

    public OverlayWindow(OverlayMark mark)
    {
        _mark = mark;
        (_shapeWidth, _shapeHeight) = Dimensions(mark);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = mark.Editable;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = BuildContent();
        if (mark.Editable)
        {
            AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(Overlay_MouseLeftButtonDown), true);
            AddHandler(MouseMoveEvent, new MouseEventHandler(Overlay_MouseMove), true);
            AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(Overlay_MouseLeftButtonUp), true);
            AddHandler(MouseRightButtonDownEvent, new MouseButtonEventHandler(Overlay_MouseRightButtonDown), true);
        }
        Loaded += (_, _) =>
        {
            if (!mark.Editable) MakeClickThrough();
            PositionAtPhysicalScreenPoint();
            // A window can receive its final per-monitor DPI scale only after
            // its first layout pass. Correct once more when that happens.
            Dispatcher.BeginInvoke((Action)PositionAtPhysicalScreenPoint);
        };
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = _mark.Editable };
        _shape = Shape();
        root.Children.Add(_shape);
        if (!string.IsNullOrWhiteSpace(_mark.Label))
        {
            root.Children.Add(new Border
            {
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(7, 3, 7, 3),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(220, 20, 24, 32)),
                Child = new TextBlock
                {
                    Text = _mark.Label,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                },
            });
        }
        return root;
    }

    private FrameworkElement Shape()
    {
        var color = Brush(_mark.Color);
        if (_mark.Kind == "box")
        {
            return new Border
            {
                Width = _shapeWidth,
                Height = _shapeHeight,
                BorderBrush = color,
                BorderThickness = new Thickness(5),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(28, color.Color.R, color.Color.G, color.Color.B)),
            };
        }
        if (_mark.Kind == "frame")
        {
            return new Border
            {
                Width = _shapeWidth,
                Height = _shapeHeight,
                BorderBrush = color,
                BorderThickness = new Thickness(7),
                CornerRadius = new CornerRadius(18),
                Background = Brushes.Transparent,
            };
        }
        if (_mark.Kind == "arrow")
        {
            var vectorX = _mark.W ?? 160;
            var vectorY = _mark.H ?? 72;
            var padding = 16d;
            var width = Math.Abs(vectorX) + padding * 2;
            var height = Math.Abs(vectorY) + padding * 2;
            var startX = vectorX < 0 ? width - padding : padding;
            var startY = vectorY < 0 ? height - padding : padding;
            var endX = startX + vectorX;
            var endY = startY + vectorY;
            var angle = Math.Atan2(endY - startY, endX - startX);
            const double wingLength = 19;
            var line = new Line
            {
                X1 = startX,
                Y1 = startY,
                X2 = endX,
                Y2 = endY,
                Stroke = color,
                StrokeThickness = 6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            var head = new Polyline
            {
                Stroke = color,
                StrokeThickness = 6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Points =
                [
                    new Point(endX + wingLength * Math.Cos(angle + Math.PI * .82), endY + wingLength * Math.Sin(angle + Math.PI * .82)),
                    new Point(endX, endY),
                    new Point(endX + wingLength * Math.Cos(angle - Math.PI * .82), endY + wingLength * Math.Sin(angle - Math.PI * .82)),
                ],
            };
            var canvas = new Canvas { Width = width, Height = height, ClipToBounds = false };
            canvas.Children.Add(line);
            canvas.Children.Add(head);
            return canvas;
        }
        if (_mark.Kind is "dot" or "label")
        {
            return new Ellipse
            {
                Width = _shapeWidth,
                Height = _shapeHeight,
                Fill = color,
                Stroke = Brushes.White,
                StrokeThickness = 3,
            };
        }
        return new Ellipse
        {
            Width = _shapeWidth,
            Height = _shapeHeight,
            Stroke = color,
            StrokeThickness = 6,
            Fill = new SolidColorBrush(Color.FromArgb(20, color.Color.R, color.Color.G, color.Color.B)),
        };
    }

    private static (double Width, double Height) Dimensions(OverlayMark mark) => mark.Kind switch
    {
        "box" => (Math.Max(12, mark.W ?? 220), Math.Max(12, mark.H ?? 100)),
        "frame" => (Math.Max(64, mark.W ?? 1920), Math.Max(64, mark.H ?? 1080)),
        "arrow" => (Math.Max(28, Math.Abs(mark.W ?? 160) + 32), Math.Max(28, Math.Abs(mark.H ?? 72) + 32)),
        "dot" or "label" => (28, 28),
        _ => (Math.Max(24, mark.W ?? 96), Math.Max(24, mark.H ?? 96)),
    };

    private static SolidColorBrush Brush(string color)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!); }
        catch { return new SolidColorBrush(Colors.Orange); }
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mark.Editable || !GetCursorPos(out _dragOriginScreen)) return;
        _dragLastScreen = _dragOriginScreen;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rectangle))
            _dragWindowOrigin = new NativePoint { X = rectangle.Left, Y = rectangle.Top };
        _dragging = true;
        CaptureMouse();
        WriteEditEvent("select");
        e.Handled = true;
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var current)) return;
        _dragLastScreen = current;
        var dx = current.X - _dragOriginScreen.X;
        var dy = current.Y - _dragOriginScreen.Y;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
            _ = SetWindowPos(handle, IntPtr.Zero, rect.Left + dx, rect.Top + dy, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        _dragOriginScreen = current;
        e.Handled = true;
    }

    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (GetCursorPos(out var current)) _dragLastScreen = current;

        var x = (int)Math.Round(_mark.X);
        var y = (int)Math.Round(_mark.Y);
        // The move handler advances the local mouse origin as it repositions
        // the native window, so persist from the total native-window delta.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
        {
            x = (int)Math.Round(_mark.X + rect.Left - _dragWindowOrigin.X);
            y = (int)Math.Round(_mark.Y + rect.Top - _dragWindowOrigin.Y);
        }
        WriteEditEvent("move", x, y);
        e.Handled = true;
    }

    private void Overlay_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mark.Editable) return;
        WriteEditEvent("delete");
        Close();
        e.Handled = true;
    }

    private void WriteEditEvent(string type, int? x = null, int? y = null)
    {
        if (string.IsNullOrWhiteSpace(_mark.EventDirectory)) return;
        try
        {
            Directory.CreateDirectory(_mark.EventDirectory);
            var path = System.IO.Path.Combine(_mark.EventDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { type, id = _mark.Id, x, y }));
            File.Move(temporary, path);
        }
        catch
        {
            // Editing an overlay must never destabilize a user's desktop. The
            // current visual drag still succeeds if its bookkeeping is busy.
        }
    }

    /// <summary>
    /// Cursor telemetry is in physical Windows pixels. WPF's Left/Top are
    /// device-independent units, so using them directly shifts marks on a
    /// scaled monitor. Move the native overlay window by the physical delta
    /// between the rendered shape centre and the requested screen point.
    /// </summary>
    private void PositionAtPhysicalScreenPoint()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;

        var renderedCenter = _shape.PointToScreen(new Point(_shape.ActualWidth / 2, _shape.ActualHeight / 2));
        var visualCenterX = _mark.Kind switch
        {
            "arrow" => _mark.X + (_mark.W ?? 160) / 2,
            "frame" => _mark.X + (_mark.W ?? 1920) / 2,
            _ => _mark.X,
        };
        var visualCenterY = _mark.Kind switch
        {
            "arrow" => _mark.Y + (_mark.H ?? 72) / 2,
            "frame" => _mark.Y + (_mark.H ?? 1080) / 2,
            _ => _mark.Y,
        };
        var x = rect.Left + (int)Math.Round(visualCenterX - renderedCenter.X);
        var y = rect.Top + (int)Math.Round(visualCenterY - renderedCenter.Y);
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
}
