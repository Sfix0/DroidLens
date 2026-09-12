using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace DroidLens.Client.Views.Controls;

/// <summary>
/// Battery indicator drawn as an actual battery shape (body + terminal nub),
/// filled from the bottom up to represent charge level — replaces the old
/// "watermark icon behind a number" chip layout with something that reads
/// at a glance without needing a separate numeric label next to it.
///
/// The charge percentage is now drawn directly inside the gauge, centered
/// over the fill (see DrawPercentageLabel) — this is the gauge's own primary
/// readout at this larger size, not a decorative accent needing an external
/// text sibling to carry the number. A soft dark shadow behind the text
/// keeps it legible whether it's sitting over filled or empty track.
///
/// Charging state adds a slow vertical "shimmer" sweep across the fill,
/// distinguishing it from the plain filled look at rest — cheaper and less
/// distracting than a lightning-bolt overlay, and reads correctly even at
/// small chip sizes where a bolt glyph would get muddy.
///
/// Fill color now auto-adjusts to charge level by default (red/amber/green,
/// see UseAutoColor) instead of always being one fixed accent color — a low
/// battery should visibly stand out as a warning without the user having to
/// read the percentage text next to it. Set UseAutoColor false and supply
/// FillBrush explicitly to opt back into a single fixed color everywhere.
/// </summary>
public class BatteryGauge : TemplatedControl
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<BatteryGauge, double>(nameof(Level), 0d);

    public static readonly StyledProperty<bool> IsChargingProperty =
        AvaloniaProperty.Register<BatteryGauge, bool>(nameof(IsCharging), false);

    public static readonly StyledProperty<IBrush> FillBrushProperty =
        AvaloniaProperty.Register<BatteryGauge, IBrush>(nameof(FillBrush), Brushes.LimeGreen);

    public static readonly StyledProperty<IBrush> TrackBrushProperty =
        AvaloniaProperty.Register<BatteryGauge, IBrush>(nameof(TrackBrush), Brushes.Gray);

    /// <summary>When true (default), the fill color is derived from Level instead of
    /// using FillBrush directly — red below 20%, amber 20-40%, green above 40%
    /// (same thresholds Android/iOS use for their own low-battery warnings), so a
    /// glance at the chip communicates urgency without needing to read the number.
    /// Set false to fall back to a caller-supplied FillBrush instead (e.g. if the
    /// app wants every gauge tinted the same accent color regardless of charge).</summary>
    public static readonly StyledProperty<bool> UseAutoColorProperty =
        AvaloniaProperty.Register<BatteryGauge, bool>(nameof(UseAutoColor), true);

    // Auto-color palette — same red/amber/green used elsewhere for status states
    // (kept as plain hardcoded colors rather than DynamicResource lookups since a
    // TemplatedControl's Render() has no easy access to the app's theme resources
    // without extra plumbing, and these three specific hues are semantic — "danger
    // red" — rather than a themeable brand color that should shift with light/dark).
    private static readonly IBrush AutoColorLow = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush AutoColorMedium = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush AutoColorHigh = new SolidColorBrush(Color.Parse("#4ADE80"));

    /// <summary>Whether to draw the charge percentage centered inside the gauge.
    /// Defaults to OFF now — the current chip layout (MainWindow.axaml) shows the
    /// percentage as its own sibling TextBlock next to the gauge (matches the
    /// reference mock: "100%  29.0°  [icon]"), so the gauge itself is back to being
    /// a plain compact indicator icon. Set true if a caller still wants the number
    /// drawn inside the shape instead (e.g. a standalone/larger usage with no
    /// sibling label around it).</summary>
    public static readonly StyledProperty<bool> ShowLabelProperty =
        AvaloniaProperty.Register<BatteryGauge, bool>(nameof(ShowLabel), false);

    /// <summary>0-100 charge percentage. Values outside range are clamped when drawing.</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsCharging
    {
        get => GetValue(IsChargingProperty);
        set => SetValue(IsChargingProperty, value);
    }

    /// <summary>Color of the charge fill and outline.</summary>
    public IBrush FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <summary>Color of the empty portion of the body outline.</summary>
    public IBrush TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public bool ShowLabel
    {
        get => GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    public bool UseAutoColor
    {
        get => GetValue(UseAutoColorProperty);
        set => SetValue(UseAutoColorProperty, value);
    }

    /// <summary>Resolves the actual brush to paint the fill/nub/label with: the
    /// level-derived red/amber/green when UseAutoColor is on, otherwise whatever
    /// FillBrush was set to (falls back to its own default, LimeGreen, if the
    /// caller never set it either).</summary>
    private IBrush ResolvedFillBrush
    {
        get
        {
            if (!UseAutoColor) return FillBrush;

            var level = Math.Clamp(Level, 0, 100);
            return level switch
            {
                < 20 => AutoColorLow,
                < 40 => AutoColorMedium,
                _ => AutoColorHigh,
            };
        }
    }

    private double _shimmerPhase;
    private DispatcherTimer? _shimmerTimer;

    static BatteryGauge()
    {
        AffectsRender<BatteryGauge>(LevelProperty, IsChargingProperty, FillBrushProperty, TrackBrushProperty, ShowLabelProperty, UseAutoColorProperty);
    }

    public BatteryGauge()
    {
        // Charging shimmer: a soft highlight band drifts upward through the fill
        // on a loop. Timer-driven rather than a declarative Transition because
        // the sweep needs to repeat indefinitely, not settle to a value.
        _shimmerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30fps, plenty smooth for a slow sweep
        };
        _shimmerTimer.Tick += (_, _) =>
        {
            if (!IsCharging) return;
            _shimmerPhase += 0.018;
            if (_shimmerPhase > 1.4) _shimmerPhase = -0.4;
            InvalidateVisual();
        };
        _shimmerTimer.Start();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var level = Math.Clamp(Level, 0, 100);

        // Layout: classic horizontal battery — body rectangle + small terminal nub.
        // Tuned for the smaller "icon-only" chip usage now (reference mock shows the
        // battery as a compact glyph, not a wide readout bar): thinner stroke, a
        // smaller/slimmer nub, and rounder corners read cleaner at ~14-18px height
        // than the old wider proportions did.
        var strokeWidth = Math.Max(1.1, h * 0.09);
        var nubWidth = Math.Max(1.2, w * 0.07);
        var nubHeight = h * 0.38;
        var bodyWidth = w - nubWidth;
        var cornerRadius = h * 0.28;

        var bodyRect = new Rect(strokeWidth / 2, strokeWidth / 2,
            bodyWidth - strokeWidth, h - strokeWidth);

        var nubRect = new Rect(bodyWidth - strokeWidth * 0.3, (h - nubHeight) / 2,
            nubWidth, nubHeight);

        var trackPen = new Pen(TrackBrush, strokeWidth);

        // Outline: body outline + nub with subtle opacity
        using (context.PushOpacity(0.45))
        {
            context.DrawRectangle(null, trackPen, bodyRect, cornerRadius, cornerRadius);
            context.DrawRectangle(TrackBrush, null, nubRect, cornerRadius * 0.5, cornerRadius * 0.5);
        }

        // Fill: inset from the outline stroke, width proportional to level
        var inset = strokeWidth + 1.0;
        var fillMaxWidth = Math.Max(0, bodyRect.Width - inset * 2);
        var fillWidth = fillMaxWidth * (level / 100.0);

        if (fillWidth > 0.5)
        {
            var fillRect = new Rect(bodyRect.X + inset, bodyRect.Y + inset,
                fillWidth, bodyRect.Height - inset * 2);

            using (context.PushClip(new RoundedRect(
                new Rect(bodyRect.X + inset, bodyRect.Y + inset, fillMaxWidth, bodyRect.Height - inset * 2),
                Math.Max(0.5, cornerRadius * 0.5))))
            {
                context.DrawRectangle(ResolvedFillBrush, null, fillRect);

                // Charging shimmer: soft diagonal highlight sweeping left-to-right
                if (IsCharging)
                {
                    var bandX = bodyRect.X + inset + fillMaxWidth * _shimmerPhase;
                    var bandWidth = fillMaxWidth * 0.28;
                    var shimmerBrush = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                            new GradientStop(Color.FromArgb(130, 255, 255, 255), 0.5),
                            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                        }
                    };
                    var bandRect = new Rect(bandX, bodyRect.Y + inset, bandWidth, bodyRect.Height - inset * 2);
                    context.DrawRectangle(shimmerBrush, null, bandRect);
                }
            }
        }

        if (ShowLabel)
        {
            DrawPercentageLabel(context, bodyRect, level);
        }
    }

    /// <summary>
    /// Draws "N%" centered over the body rect (fill + track both), on top of
    /// everything else. A soft dark drop-shadow (offset copy at low opacity,
    /// not a real blur — cheap and good enough at this size) keeps the digits
    /// legible over both the light fill color and the darker empty track,
    /// since the label straddles both regions once the gauge is wide enough
    /// to hold real text.
    /// </summary>
    private void DrawPercentageLabel(DrawingContext context, Rect bodyRect, double level)
    {
        var text = $"{Math.Round(level)}%";
        var fontSize = Math.Max(7.0, bodyRect.Height * 0.5);

        var typeface = new Typeface("Inter", weight: FontWeight.SemiBold);

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)));

        var origin = new Point(
            bodyRect.X + (bodyRect.Width - formatted.Width) / 2,
            bodyRect.Y + (bodyRect.Height - formatted.Height) / 2);

        // Shadow: same text offset by ~1px at low opacity, drawn first
        var shadow = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)));

        context.DrawText(shadow, origin + new Vector(0, 0.7));
        context.DrawText(formatted, origin);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _shimmerTimer?.Stop();
        _shimmerTimer = null;
    }
}