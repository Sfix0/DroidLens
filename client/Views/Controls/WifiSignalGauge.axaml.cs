using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace DroidLens.Client.Views.Controls;

/// <summary>
/// Modern Wi-Fi signal gauge widget — rendered as smooth, vector-drawn concentric arcs
/// over a center dot. Replaces polygonal line approximations with native vector curves
/// (<see cref="StreamGeometryContext.ArcTo"/>) and rounded line caps, matching modern
/// OS status bar indicators (Windows 11 Fluent / iOS / Android 15).
///
/// Signal levels (0-4):
///   0: Offline / No signal (all 4 bars dimmed)
///   1: Weak signal (1/4 - center dot lit)
///   2: Fair signal (2/4 - dot + inner arc lit)
///   3: Good signal (3/4 - dot + inner + middle arc lit)
///   4: Excellent signal (4/4 - full 4 bars lit)
///
/// When <see cref="IsUsbMode"/> is true, displays the USB glyph.
/// </summary>
public class WifiSignalGauge : TemplatedControl
{
    public static readonly StyledProperty<int> SignalLevelProperty =
        AvaloniaProperty.Register<WifiSignalGauge, int>(nameof(SignalLevel), 0);

    public static readonly StyledProperty<bool> IsUsbModeProperty =
        AvaloniaProperty.Register<WifiSignalGauge, bool>(nameof(IsUsbMode), false);

    public static readonly StyledProperty<IBrush> FillBrushProperty =
        AvaloniaProperty.Register<WifiSignalGauge, IBrush>(nameof(FillBrush), Brushes.LimeGreen);

    public static readonly StyledProperty<IBrush> TrackBrushProperty =
        AvaloniaProperty.Register<WifiSignalGauge, IBrush>(nameof(TrackBrush), Brushes.Gray);

    public static readonly StyledProperty<bool> UseAutoColorProperty =
        AvaloniaProperty.Register<WifiSignalGauge, bool>(nameof(UseAutoColor), false);

    public static readonly StyledProperty<double> UnlitOpacityProperty =
        AvaloniaProperty.Register<WifiSignalGauge, double>(nameof(UnlitOpacity), 0.22);

    // Semantic auto-color brushes for signal strength states
    private static readonly IBrush AutoColorLow = new SolidColorBrush(Color.Parse("#F87171"));    // Level 1: Red/Rose
    private static readonly IBrush AutoColorMedium = new SolidColorBrush(Color.Parse("#FBBF24")); // Level 2: Amber
    private static readonly IBrush AutoColorGood = new SolidColorBrush(Color.Parse("#38BDF8"));   // Level 3: Sky Blue
    private static readonly IBrush AutoColorHigh = new SolidColorBrush(Color.Parse("#4ADE80"));   // Level 4: Emerald Green
    private static readonly IBrush AutoColorOff = new SolidColorBrush(Color.Parse("#9CA3AF"));    // Level 0: Dimmed Gray

    /// <summary>0-4: Wi-Fi signal strength. Ignored when IsUsbMode is true.</summary>
    public int SignalLevel
    {
        get => GetValue(SignalLevelProperty);
        set => SetValue(SignalLevelProperty, value);
    }

    /// <summary>When true, shows the USB glyph instead of a Wi-Fi strength icon.</summary>
    public bool IsUsbMode
    {
        get => GetValue(IsUsbModeProperty);
        set => SetValue(IsUsbModeProperty, value);
    }

    public IBrush FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>When true, automatically tints signal bars based on level (red, amber, cyan, green).</summary>
    public bool UseAutoColor
    {
        get => GetValue(UseAutoColorProperty);
        set => SetValue(UseAutoColorProperty, value);
    }

    /// <summary>Opacity of inactive/unlit signal bars (0.0 to 1.0). Default is 0.22.</summary>
    public double UnlitOpacity
    {
        get => GetValue(UnlitOpacityProperty);
        set => SetValue(UnlitOpacityProperty, value);
    }

    private MaterialIcon? _icon;

    static WifiSignalGauge()
    {
        AffectsRender<WifiSignalGauge>(
            SignalLevelProperty,
            IsUsbModeProperty,
            FillBrushProperty,
            TrackBrushProperty,
            UseAutoColorProperty,
            UnlitOpacityProperty);

        IsUsbModeProperty.Changed.AddClassHandler<WifiSignalGauge>((o, _) => o.UpdateUsbGlyph());
        FillBrushProperty.Changed.AddClassHandler<WifiSignalGauge>((o, _) => o.UpdateUsbGlyph());
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _icon = e.NameScope.Find<MaterialIcon>("PART_Icon");
        UpdateUsbGlyph();
    }

    private void UpdateUsbGlyph()
    {
        if (_icon is null) return;
        _icon.IsVisible = IsUsbMode;
        if (IsUsbMode)
        {
            _icon.Kind = MaterialIconKind.Usb;
            _icon.Foreground = FillBrush;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (IsUsbMode) return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var litBars = Math.Clamp(SignalLevel, 0, 4);
        var size = Math.Min(w, h);

        // Resolve active fill brush (auto color or FillBrush)
        IBrush activeBrush = UseAutoColor ? litBars switch
        {
            1 => AutoColorLow,
            2 => AutoColorMedium,
            3 => AutoColorGood,
            4 => AutoColorHigh,
            _ => AutoColorOff
        } : FillBrush;

        IBrush trackBrush = TrackBrush;

        // Proportional vector gauge layout
        var thickness = Math.Max(1.5, size * 0.10);
        var dotRadius = Math.Max(1.0, size * 0.08);

        // Compute outer radius & center Y to fit within control bounds with zero clipping
        var maxOuterRadius = Math.Max(dotRadius + 2.0, size * 0.74 - thickness / 2.0);
        var cy = h - thickness / 2.0 - dotRadius - size * 0.02;
        var cx = w / 2.0;

        var innerRadius = dotRadius + Math.Max(2.0, size * 0.16);
        var ringGap = (maxOuterRadius - innerRadius) / 2.0;

        // Bar 0: Center Dot
        bool dotLit = litBars >= 1;
        var dotBrush = dotLit ? activeBrush : trackBrush;
        var dotOpacity = dotLit ? 1.0 : UnlitOpacity;

        using (context.PushOpacity(dotOpacity))
        {
            context.DrawEllipse(dotBrush, null, new Point(cx, cy), dotRadius, dotRadius);
        }

        // Bars 1, 2, 3: Smooth Concentric Vector Arcs
        // 84° total sweep angle (±42° from vertical) for standard modern Wi-Fi glyph look
        const double halfSweepDeg = 42.0;
        const double startAngleDeg = -90.0 - halfSweepDeg;
        const double sweepAngleDeg = halfSweepDeg * 2.0;

        for (var i = 0; i < 3; i++)
        {
            var arcBarIndex = i + 2; // Arc 0 corresponds to bar 2 (level >= 2), Arc 1 to bar 3 (level >= 3), Arc 2 to bar 4 (level >= 4)
            bool isLit = litBars >= arcBarIndex;

            var radius = innerRadius + ringGap * i;
            var brush = isLit ? activeBrush : trackBrush;
            var opacity = isLit ? 1.0 : UnlitOpacity;

            DrawArc(context, cx, cy, radius, startAngleDeg, sweepAngleDeg, thickness, brush, opacity);
        }
    }

    /// <summary>
    /// Draws a smooth, native vector circular arc from startAngleDeg spanning sweepAngleDeg.
    /// Uses <see cref="StreamGeometryContext.ArcTo"/> for Skia/OS smooth anti-aliased curves
    /// with rounded caps (<see cref="PenLineCap.Round"/>).
    /// </summary>
    private static void DrawArc(
        DrawingContext context,
        double cx,
        double cy,
        double radius,
        double startAngleDeg,
        double sweepAngleDeg,
        double thickness,
        IBrush brush,
        double opacity)
    {
        if (radius <= 0) return;

        double startRad = startAngleDeg * Math.PI / 180.0;
        double endRad = (startAngleDeg + sweepAngleDeg) * Math.PI / 180.0;

        Point startPoint = new Point(cx + radius * Math.Cos(startRad), cy + radius * Math.Sin(startRad));
        Point endPoint = new Point(cx + radius * Math.Cos(endRad), cy + radius * Math.Sin(endRad));

        bool isLargeArc = Math.Abs(sweepAngleDeg) >= 180.0;
        SweepDirection sweepDirection = sweepAngleDeg >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(startPoint, false);
            ctx.ArcTo(endPoint, new Size(radius, radius), 0, isLargeArc, sweepDirection);
        }

        var pen = new Pen(brush, thickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        using (context.PushOpacity(opacity))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}