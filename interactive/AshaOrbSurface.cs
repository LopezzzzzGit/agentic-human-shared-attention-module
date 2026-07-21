using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AshaLive;

public enum OrbPresenceState
{
    Idle,
    Listening,
    Thinking,
    Speaking,
}

/// <summary>
/// A procedural, state-driven glass-and-cloud surface. Its signal API is
/// intentionally separate from rendering so a future voice bridge can supply
/// real microphone and playback energy instead of a cosmetic animation.
/// </summary>
public sealed class AshaOrbSurface : FrameworkElement
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastFrame;
    private double _phase;
    private double _energy = 0.025;
    private double _targetEnergy = 0.025;
    private double _listeningGlow;
    private double _thinkingGlow;
    private double _speakingGlow;
    private OrbPresenceState _state = OrbPresenceState.Idle;
    private WriteableBitmap? _cloudTexture;
    private byte[]? _cloudPixels;
    private TimeSpan _lastTextureFrame;

    public AshaOrbSurface()
    {
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
        SnapsToDevicePixels = true;
    }

    public void SetPresenceState(OrbPresenceState state)
    {
        _state = state;
        _targetEnergy = Math.Max(_targetEnergy, StateFloor(state));
    }

    public void SetAudioEnergy(double rms)
    {
        _targetEnergy = Math.Clamp(rms, 0, 1);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var seconds = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
        _lastFrame = now;
        var floor = StateFloor(_state);
        _targetEnergy = Math.Max(floor, _targetEnergy * Math.Exp(-seconds * 6));
        _energy += (_targetEnergy - _energy) * Math.Min(1, seconds * 8);
        // The inner presence arrives deliberately: it feels like a cloud
        // gathering behind the glass, rather than a state light switching on.
        _listeningGlow = Ease(_listeningGlow, _state == OrbPresenceState.Listening ? 1 : 0, seconds, 1.48, 1.35);
        _thinkingGlow = Ease(_thinkingGlow, _state == OrbPresenceState.Thinking ? 1 : 0, seconds, 1.26, 1.18);
        _speakingGlow = Ease(_speakingGlow, _state == OrbPresenceState.Speaking ? 1 : 0, seconds, 1.72, 1.30);
        _phase += seconds * StateSpeed(_state) * (1 + _energy * 2.4);
        UpdateCloudTexture(now);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = side * 0.438;
        var outerRadius = radius * 1.09;

        context.PushClip(new EllipseGeometry(center, outerRadius, outerRadius));
        context.DrawEllipse(OuterGlow(), null, center, outerRadius, outerRadius);
        context.DrawEllipse(BaseGlass(), null, center, radius, radius);

        context.PushClip(new EllipseGeometry(center, radius, radius));
        if (_cloudTexture is not null)
        {
            context.PushOpacity(0.96);
            context.DrawImage(_cloudTexture, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
            context.Pop();
        }
        DrawStateCore(context, center, radius);
        DrawWisp(context, center, radius, 0);
        DrawWisp(context, center, radius, 1);
        DrawWisp(context, center, radius, 2);
        DrawWisp(context, center, radius, 3);
        DrawWisp(context, center, radius, 4);
        DrawWisp(context, center, radius, 5);

        DrawGlassOverlay(context, center, radius);
        context.Pop();
        context.Pop();
    }

    private void UpdateCloudTexture(TimeSpan now)
    {
        if ((now - _lastTextureFrame).TotalMilliseconds < 50) return;
        _lastTextureFrame = now;
        const int size = 192;
        _cloudTexture ??= new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        _cloudPixels ??= new byte[size * size * 4];

        var drift = _phase * 0.085;
        for (var y = 0; y < size; y++)
        {
            var py = y / (double)(size - 1);
            for (var x = 0; x < size; x++)
            {
                var px = x / (double)(size - 1);
                var swirlX = FractalNoise(px * 2.1 + drift, py * 2.1 - drift * 0.34, 4) - 0.5;
                var swirlY = FractalNoise(px * 2.1 - drift * 0.23, py * 2.1 + drift, 4) - 0.5;
                var mass = FractalNoise(
                    (px + swirlX * 0.24) * 3.15 - drift * 0.20,
                    (py + swirlY * 0.24) * 3.15 + drift * 0.15,
                    5);
                var filament = FractalNoise(px * 7.4 + swirlY * 0.9 + drift * 0.36, py * 7.4 + swirlX * 0.9 - drift * 0.24, 3);
                var cloud = Math.Clamp((mass - 0.29) * 1.68 + (filament - 0.50) * 0.22, 0, 1);
                var light = SmoothStep(0.18, 0.82, cloud);
                var highMist = SmoothStep(0.61, 0.93, cloud);
                var blueDepth = Math.Clamp(0.42 + (1 - cloud) * 0.48 + (py - 0.5) * 0.10, 0, 1);

                // Fine rose filaments are mixed into the central cloud field,
                // not painted above it. Their intensity follows the smoothed
                // listening/thinking channels, so they bloom and dissolve.
                var dx = px - 0.50;
                var dy = py - 0.51;
                var coreDistance = Math.Sqrt(dx * dx + dy * dy);
                var coreMask = 1 - SmoothStep(0.05, 0.44, coreDistance);
                var lace = FractalNoise(px * 15.8 + drift * 0.58, py * 15.8 - drift * 0.42, 6);
                var veins = FractalNoise(px * 27.4 - drift * 0.31, py * 27.4 + drift * 0.47, 4);
                var roseActivity = Math.Clamp(_listeningGlow * 0.78 + _thinkingGlow, 0, 1);
                var roseFilament = coreMask * roseActivity * Math.Clamp((lace - 0.38) * 1.52 + (veins - 0.59) * 0.48, 0, 1);

                var red = (byte)Math.Clamp(Lerp(Lerp(82, 255, light), 255, roseFilament * 0.74), 0, 255);
                var green = (byte)Math.Clamp(Lerp(Lerp(145, 255, light) + highMist * 2, 104, roseFilament * 0.38), 0, 255);
                var blue = (byte)Math.Clamp(Lerp(Lerp(228, 255, light), 205, roseFilament * 0.28), 0, 255);
                var alpha = (byte)Math.Clamp(210 + blueDepth * 38 + highMist * 7, 0, 255);
                var offset = (y * size + x) * 4;
                _cloudPixels[offset] = blue;
                _cloudPixels[offset + 1] = green;
                _cloudPixels[offset + 2] = red;
                _cloudPixels[offset + 3] = alpha;
            }
        }
        _cloudTexture.WritePixels(new Int32Rect(0, 0, size, size), _cloudPixels, size * 4, 0);
    }

    private void DrawCloud(DrawingContext context, Point center, double radius, int layer, Color[] colors)
    {
        var rotation = _phase * (layer % 2 == 0 ? 0.63 : -0.46) + layer * 1.75;
        var scale = 0.84 + layer * 0.075 + _energy * (0.12 + layer * 0.025);
        var drift = radius * (0.14 + layer * 0.025);
        var cloudCenter = new Point(
            center.X + Math.Cos(rotation * 0.77 + layer) * drift,
            center.Y + Math.Sin(rotation * 0.61 - layer) * drift * 0.76);
        var blob = CreateBlob(cloudCenter, radius * scale, rotation, 0.72 + layer * 0.12, 4 + layer, 0.095 + _energy * 0.13);
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.45 + Math.Cos(rotation * 0.52) * 0.18, 0.43 + Math.Sin(rotation * 0.46) * 0.16),
            GradientOrigin = new Point(0.37 + Math.Cos(rotation * 0.52) * 0.20, 0.33 + Math.Sin(rotation * 0.46) * 0.18),
            RadiusX = 0.76,
            RadiusY = 0.76,
            GradientStops =
            {
                new GradientStop(colors[0], 0),
                new GradientStop(colors[1], 0.47),
                new GradientStop(colors[2], 1),
            },
        };
        context.DrawGeometry(brush, null, blob);
    }

    private void DrawWisp(DrawingContext context, Point center, double radius, int index)
    {
        var direction = index % 2 == 0 ? 1.0 : -1.0;
        var phase = (_phase * direction * (0.78 + index * 0.15)) + index * 2.1;
        var drift = radius * (0.24 + index * 0.035);
        var wispCenter = new Point(
            center.X + Math.Cos(phase) * drift,
            center.Y + Math.Sin(phase * 1.27) * drift * 0.48);
        var wisp = CreateBlob(wispCenter, radius * (0.43 + index * 0.06), phase, 0.82, 7 + index, 0.035 + _energy * 0.045);
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.34, 0.28),
            GradientOrigin = new Point(0.24, 0.20),
            RadiusX = 0.82,
            RadiusY = 0.64,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(146, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(72, 224, 246, 255), 0.40),
                new GradientStop(Color.FromArgb(0, 175, 224, 255), 1),
            },
        };
        context.DrawGeometry(brush, null, wisp);
    }

    // Listening and thinking keep the outer glass blue. Their activity happens
    // inside: a gentle rose cloud while listening, a denser pink cloud as ASHA
    // forms a reply. Independent glow channels cross-fade those states instead
    // of switching them abruptly.
    private void DrawStateCore(DrawingContext context, Point center, double radius)
    {
        var listenPulse = 0.84 + Math.Sin(_phase * 5.8) * 0.10 + _energy * 0.15;
        var thinkPulse = 0.86 + Math.Sin(_phase * 3.65 + 0.9) * 0.14;
        if (_listeningGlow > 0.008)
        {
            var intensity = Math.Clamp(_listeningGlow * listenPulse, 0, 1);
            DrawInnerAura(context, center, radius * (0.54 + _energy * 0.07), intensity * 0.72,
                Color.FromRgb(255, 113, 202), Color.FromRgb(255, 218, 246));
            DrawInnerMist(context, center, radius * (0.54 + _energy * 0.09), _phase * 0.52, intensity, 13,
                Color.FromRgb(255, 237, 252), Color.FromRgb(255, 94, 192));
            DrawCenterCloud(
                context, center, radius * (0.30 + _energy * 0.07), _phase * 0.72, intensity * 0.52, 13, 0.15,
                Color.FromArgb(166, 255, 236, 253),
                Color.FromArgb(142, 255, 112, 200),
                Color.FromArgb(0, 248, 57, 168));
            DrawCenterCloud(
                context, new Point(center.X + radius * 0.13, center.Y - radius * 0.08), radius * 0.22, -_phase * 0.63, intensity * 0.48, 17, 0.18,
                Color.FromArgb(132, 255, 248, 255),
                Color.FromArgb(118, 255, 143, 214),
                Color.FromArgb(0, 255, 78, 179));
            DrawCenterCloud(
                context, new Point(center.X - radius * 0.16, center.Y + radius * 0.09), radius * 0.15, _phase * 1.07, intensity * 0.39, 20, 0.20,
                Color.FromArgb(116, 255, 235, 250),
                Color.FromArgb(104, 245, 91, 183),
                Color.FromArgb(0, 235, 45, 151));
            DrawCenterCloud(
                context, new Point(center.X - radius * 0.03, center.Y - radius * 0.19), radius * 0.16, -_phase * 1.36, intensity * 0.54, 19, 0.20,
                Color.FromArgb(146, 255, 249, 255),
                Color.FromArgb(126, 255, 126, 209),
                Color.FromArgb(0, 249, 53, 166));
            DrawCenterCloud(
                context, new Point(center.X + radius * 0.21, center.Y + radius * 0.17), radius * 0.13, _phase * 1.62, intensity * 0.42, 23, 0.23,
                Color.FromArgb(128, 255, 244, 253),
                Color.FromArgb(111, 255, 95, 194),
                Color.FromArgb(0, 240, 36, 158));
            DrawCenterCloud(
                context, new Point(center.X - radius * 0.24, center.Y - radius * 0.04), radius * 0.105, -_phase * 1.93, intensity * 0.33, 29, 0.27,
                Color.FromArgb(117, 255, 250, 255),
                Color.FromArgb(100, 255, 143, 219),
                Color.FromArgb(0, 235, 48, 171));
        }

        if (_thinkingGlow > 0.008)
        {
            var intensity = Math.Clamp(_thinkingGlow * thinkPulse, 0, 1);
            DrawInnerAura(context, center, radius * 0.62, intensity * 0.78,
                Color.FromRgb(247, 62, 171), Color.FromRgb(255, 203, 241));
            DrawInnerMist(context, center, radius * 0.64, -_phase * 0.41, intensity, 18,
                Color.FromRgb(255, 228, 249), Color.FromRgb(244, 56, 166));
            DrawCenterCloud(
                context, center, radius * 0.36, _phase * 0.36, intensity * 0.56, 16, 0.18,
                Color.FromArgb(181, 255, 228, 251),
                Color.FromArgb(164, 249, 71, 180),
                Color.FromArgb(0, 212, 31, 137));
            DrawCenterCloud(
                context, new Point(center.X - radius * 0.14, center.Y + radius * 0.07), radius * 0.24, -_phase * 0.54, intensity * 0.51, 20, 0.21,
                Color.FromArgb(148, 255, 241, 255),
                Color.FromArgb(132, 255, 105, 202),
                Color.FromArgb(0, 238, 45, 153));
            DrawCenterCloud(
                context, new Point(center.X + radius * 0.16, center.Y - radius * 0.14), radius * 0.16, _phase * 0.83, intensity * 0.42, 24, 0.23,
                Color.FromArgb(112, 255, 232, 250),
                Color.FromArgb(101, 246, 74, 179),
                Color.FromArgb(0, 224, 33, 139));
            DrawCenterCloud(
                context, new Point(center.X + radius * 0.02, center.Y + radius * 0.22), radius * 0.17, -_phase * 1.18, intensity * 0.62, 21, 0.22,
                Color.FromArgb(150, 255, 244, 255),
                Color.FromArgb(136, 255, 91, 190),
                Color.FromArgb(0, 232, 35, 143));
            DrawCenterCloud(
                context, new Point(center.X - radius * 0.23, center.Y - radius * 0.16), radius * 0.13, _phase * 1.52, intensity * 0.46, 26, 0.25,
                Color.FromArgb(129, 255, 239, 254),
                Color.FromArgb(114, 251, 64, 174),
                Color.FromArgb(0, 220, 29, 135));
            DrawCenterCloud(
                context, new Point(center.X + radius * 0.24, center.Y + radius * 0.05), radius * 0.10, -_phase * 1.86, intensity * 0.35, 31, 0.29,
                Color.FromArgb(114, 255, 244, 255),
                Color.FromArgb(99, 255, 109, 200),
                Color.FromArgb(0, 226, 35, 145));
        }

        // A reply is not an absence of presence. While TTS is playing, ASHA
        // keeps a cooler, quietly breathing mist at the centre. Later, direct
        // playback energy can drive this same layer syllable by syllable.
        if (_speakingGlow > 0.008)
        {
            var speechPulse = 0.84
                + Math.Sin(_phase * 6.4 + 0.4) * 0.10
                + Math.Sin(_phase * 12.7) * 0.045;
            var intensity = Math.Clamp(_speakingGlow * speechPulse, 0, 1);
            DrawInnerAura(context, center, radius * 0.57, intensity * 0.62,
                Color.FromRgb(139, 211, 255), Color.FromRgb(238, 249, 255));
            DrawInnerMist(context, center, radius * 0.58, _phase * 0.78, intensity, 15,
                Color.FromRgb(238, 249, 255), Color.FromRgb(113, 189, 255));
            DrawCenterCloud(
                context, new Point(center.X - radius * 0.07, center.Y + radius * 0.03), radius * 0.28, -_phase * 0.82, intensity * 0.45, 18, 0.19,
                Color.FromArgb(143, 247, 253, 255),
                Color.FromArgb(120, 129, 208, 255),
                Color.FromArgb(0, 85, 156, 241));
            DrawCenterCloud(
                context, new Point(center.X + radius * 0.19, center.Y - radius * 0.15), radius * 0.15, _phase * 1.35, intensity * 0.31, 25, 0.24,
                Color.FromArgb(104, 251, 255, 255),
                Color.FromArgb(90, 163, 221, 255),
                Color.FromArgb(0, 96, 164, 238));
        }
    }

    // This is a foreground surface, not part of the cloud simulation. It uses
    // restrained, soft optical highlights—the cloudy volume stays dominant.
    private static void DrawGlassOverlay(DrawingContext context, Point center, double radius)
    {
        // Broad softbox reflection across the upper surface: present, but with
        // no hard decorative stroke cutting across the cloud.
        var topSheen = new RadialGradientBrush
        {
            Center = new Point(0.46, 0.34),
            GradientOrigin = new Point(0.42, 0.20),
            RadiusX = 0.72,
            RadiusY = 0.68,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(158, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(73, 246, 252, 255), 0.33),
                new GradientStop(Color.FromArgb(13, 218, 239, 255), 0.62),
                new GradientStop(Color.FromArgb(0, 205, 231, 255), 1),
            },
        };
        context.DrawEllipse(topSheen, null,
            new Point(center.X - radius * 0.08, center.Y - radius * 0.39), radius * 0.76, radius * 0.42);

        // The large, diffuse left reflection and the tiny upper-right lights
        // echo the way a real glass sphere reflects a room or softbox.
        var leftReflection = new RadialGradientBrush
        {
            Center = new Point(0.42, 0.40),
            GradientOrigin = new Point(0.34, 0.29),
            RadiusX = 0.72,
            RadiusY = 0.86,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(118, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(42, 244, 251, 255), 0.46),
                new GradientStop(Color.FromArgb(0, 235, 247, 255), 1),
            },
        };
        context.DrawEllipse(leftReflection, null,
            new Point(center.X - radius * 0.43, center.Y - radius * 0.25), radius * 0.31, radius * 0.48);

        var glint = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(216, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(44, 255, 255, 255), 0.48),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        context.DrawEllipse(glint, null,
            new Point(center.X + radius * 0.31, center.Y - radius * 0.35), radius * 0.085, radius * 0.085);
        context.DrawEllipse(glint, null,
            new Point(center.X + radius * 0.53, center.Y - radius * 0.07), radius * 0.040, radius * 0.040);

        var lowerBounce = new RadialGradientBrush
        {
            Center = new Point(0.58, 0.78),
            GradientOrigin = new Point(0.62, 0.82),
            RadiusX = 0.64,
            RadiusY = 0.38,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(46, 211, 237, 255), 0),
                new GradientStop(Color.FromArgb(13, 170, 209, 255), 0.42),
                new GradientStop(Color.FromArgb(0, 120, 175, 255), 1),
            },
        };
        context.DrawEllipse(lowerBounce, null, center, radius, radius);

    }

    private static void DrawInnerAura(DrawingContext context, Point center, double radius, double intensity, Color hot, Color pale)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(WithOpacity(hot, intensity * 0.52), 0),
                new GradientStop(WithOpacity(pale, intensity * 0.24), 0.36),
                new GradientStop(WithOpacity(hot, 0), 1),
            },
        };
        context.DrawEllipse(brush, null, center, radius, radius);
    }

    // Loose, semi-transparent pockets create volume without a hard outer
    // silhouette. Each pocket drifts independently, giving the centre the
    // character of smoke or cloud seen through the glass.
    private static void DrawInnerMist(DrawingContext context, Point center, double radius, double phase, double intensity, int count, Color pale, Color saturated)
    {
        if (intensity <= 0.002) return;
        for (var index = 0; index < count; index++)
        {
            var seed = index * 2.399963229728653;
            var orbit = radius * (0.10 + (index % 6) * 0.075);
            var angle = seed + phase * (0.52 + (index % 4) * 0.12);
            var puffCenter = new Point(
                center.X + Math.Cos(angle) * orbit + Math.Sin(phase * 0.71 + index) * radius * 0.045,
                center.Y + Math.Sin(angle * 1.19) * orbit * 0.67 + Math.Cos(phase * 0.61 - index) * radius * 0.04);
            var puffRadius = radius * (0.13 + (index % 5) * 0.024) * (0.92 + Math.Sin(phase * 1.18 + index) * 0.10);
            var opacity = intensity * (0.15 + (index % 4) * 0.026);
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.43, 0.38),
                GradientOrigin = new Point(0.32, 0.25),
                RadiusX = 0.82,
                RadiusY = 0.76,
                GradientStops =
                {
                    new GradientStop(WithOpacity(pale, opacity), 0),
                    new GradientStop(WithOpacity(saturated, opacity * 0.62), 0.38),
                    new GradientStop(WithOpacity(saturated, 0), 1),
                },
            };
            context.DrawEllipse(brush, null, puffCenter, puffRadius, puffRadius * (0.72 + (index % 3) * 0.08));
        }
    }

    private static void DrawCenterCloud(DrawingContext context, Point center, double radius, double phase, double intensity, int lobes, double strength, Color inner, Color middle, Color outer)
    {
        if (intensity <= 0.002) return;
        var drift = radius * 0.09;
        var cloudCenter = new Point(
            center.X + Math.Cos(phase * 0.88) * drift,
            center.Y + Math.Sin(phase * 1.17) * drift * 0.7);
        var blob = CreateBlob(cloudCenter, radius, phase, 0.45, lobes, strength);
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.42, 0.35),
            GradientOrigin = new Point(0.32, 0.24),
            RadiusX = 0.78,
            RadiusY = 0.74,
            GradientStops =
            {
                new GradientStop(WithOpacity(inner, intensity), 0),
                new GradientStop(WithOpacity(middle, intensity), 0.52),
                new GradientStop(WithOpacity(outer, intensity), 1),
            },
        };
        context.DrawGeometry(brush, null, blob);
    }

    private static StreamGeometry CreateBlob(Point center, double radius, double rotation, double wander, int lobes, double strength)
    {
        const int points = 88;
        var geometry = new StreamGeometry();
        using var writer = geometry.Open();
        for (var index = 0; index <= points; index++)
        {
            var angle = (Math.PI * 2 * index / points) + rotation;
            var ripple = Math.Sin(angle * lobes + rotation * 1.8) * strength
                + Math.Sin(angle * (lobes + 3) - rotation * 1.2) * strength * 0.44;
            var distance = radius * (1 + ripple);
            var offsetX = Math.Cos(rotation * 0.55) * radius * wander * 0.18;
            var offsetY = Math.Sin(rotation * 0.73) * radius * wander * 0.14;
            var point = new Point(center.X + offsetX + Math.Cos(angle) * distance, center.Y + offsetY + Math.Sin(angle) * distance);
            if (index == 0) writer.BeginFigure(point, true, true);
            else writer.LineTo(point, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static double FractalNoise(double x, double y, int octaves)
    {
        var value = 0.0;
        var amplitude = 0.5;
        var frequency = 1.0;
        var total = 0.0;
        for (var index = 0; index < octaves; index++)
        {
            value += ValueNoise(x * frequency, y * frequency) * amplitude;
            total += amplitude;
            amplitude *= 0.5;
            frequency *= 2.03;
        }
        return value / total;
    }

    private static double ValueNoise(double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var sx = fx * fx * (3 - 2 * fx);
        var sy = fy * fy * (3 - 2 * fy);
        var a = Hash(x0, y0);
        var b = Hash(x0 + 1, y0);
        var c = Hash(x0, y0 + 1);
        var d = Hash(x0 + 1, y0 + 1);
        return Lerp(Lerp(a, b, sx), Lerp(c, d, sx), sy);
    }

    private static double Hash(int x, int y)
    {
        unchecked
        {
            uint value = (uint)(x * 374761393 + y * 668265263);
            value = (value ^ (value >> 13)) * 1274126177;
            return ((value ^ (value >> 16)) & 0x00ffffff) / 16777215.0;
        }
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var amount = Math.Clamp((value - edge0) / (edge1 - edge0), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static double Lerp(double a, double b, double amount) => a + (b - a) * amount;

    private static double Ease(double current, double target, double seconds, double easeInRate, double easeOutRate)
    {
        var rate = target > current ? easeInRate : easeOutRate;
        return current + (target - current) * (1 - Math.Exp(-seconds * rate));
    }

    private static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(color.A * opacity, 0, 255), color.R, color.G, color.B);

    private static Brush OuterGlow() => new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(148, 118, 183, 255), 0),
            new GradientStop(Color.FromArgb(58, 90, 155, 255), 0.58),
            new GradientStop(Color.FromArgb(0, 70, 128, 255), 1),
        },
    };

    private static Brush BaseGlass() => new RadialGradientBrush
    {
        Center = new Point(0.34, 0.24),
        GradientOrigin = new Point(0.27, 0.17),
        RadiusX = 0.82,
        RadiusY = 0.82,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(255, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(255, 220, 243, 255), 0.29),
            new GradientStop(Color.FromArgb(255, 132, 191, 250), 0.68),
            new GradientStop(Color.FromArgb(255, 63, 126, 222), 1),
        },
    };

    private static double StateSpeed(OrbPresenceState state) => state switch
    {
        OrbPresenceState.Listening => 0.58,
        OrbPresenceState.Thinking => 0.34,
        OrbPresenceState.Speaking => 1.15,
        _ => 0.42,
    };

    private static double StateFloor(OrbPresenceState state) => state switch
    {
        OrbPresenceState.Listening => 0.06,
        OrbPresenceState.Thinking => 0.045,
        OrbPresenceState.Speaking => 0.15,
        _ => 0.022,
    };
}
