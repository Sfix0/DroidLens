using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DroidLens.Client.Views.Controls;

/// <summary>
/// Attached behavior for the custom ScrollBar's Thumb (see Styles/ScrollBars.axaml).
///
/// Owns a single TransformGroup = [ScaleTransform, TranslateTransform] on the Thumb so two
/// independent effects never fight over RenderTransform:
///   - Scale: driven declaratively from XAML via the ScaleX/ScaleY attached properties
///     below (bind these from :pointerover/:pressed Setters instead of setting
///     Thumb.RenderTransform directly — setting RenderTransform directly would replace the
///     whole TransformGroup and wipe out the bounce's TranslateTransform).
///   - Translate: driven from code here only, for the "edge bounce" — see below.
///
/// EDGE BOUNCE (not a generic settle bounce): fires only when scrolling actually reaches
/// Minimum or Maximum, mimicking "scrolled fast, hit the end, bounced a little" (iOS-style
/// rubber-band). It does NOT fire on an ordinary mid-list stop — only right when Value lands
/// on an edge. Trigger condition, checked on every Track.Value change:
///   - the new Value equals Minimum or Maximum (within a small epsilon for double rounding), and
///   - there was actual motion this tick (Value changed from the previous one) OR the user is
///     still actively pushing past the edge (e.g. continuing to scroll the wheel while already
///     pinned at Minimum/Maximum — ScrollViewer keeps re-delivering wheel deltas even though
///     Value can't move further, which VelocityRepeat below catches).
/// The nudge direction is always "further past the edge" (down at Minimum's top edge feels
/// wrong worded generically — see the sign logic in OnTrackValueChanged), then the existing
/// SpringEasing Transition on TranslateTransform springs it back to 0, giving the little
/// overshoot-and-settle look.
///
/// Does NOT touch ScrollViewer.Offset or Track.Value at all — this never affects where the
/// content actually ends up, only how the Thumb visually reacts.
/// </summary>
public static class ScrollBarBounceBehavior
{
    // How far (DIPs) the thumb overshoots past the edge before springing back.
    private const double BounceDistance = 6.0;

    // Re-arm delay after a bounce fires, so a single "slam into the edge and keep scrolling"
    // gesture doesn't retrigger the animation every single tick while pinned — one bounce per
    // approach, not a buzz. Short enough that a genuine new approach (scroll away, come back,
    // hit the edge again) still gets its own bounce.
    private static readonly TimeSpan RearmDelay = TimeSpan.FromMilliseconds(260);

    private const double Epsilon = 0.5;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Thumb, bool>("IsEnabled", typeof(ScrollBarBounceBehavior));

    public static readonly AttachedProperty<double> ScaleXProperty =
        AvaloniaProperty.RegisterAttached<Thumb, double>("ScaleX", typeof(ScrollBarBounceBehavior), 1.0);

    public static readonly AttachedProperty<double> ScaleYProperty =
        AvaloniaProperty.RegisterAttached<Thumb, double>("ScaleY", typeof(ScrollBarBounceBehavior), 1.0);

    public static bool GetIsEnabled(Thumb thumb) => thumb.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Thumb thumb, bool value) => thumb.SetValue(IsEnabledProperty, value);

    public static double GetScaleX(Thumb thumb) => thumb.GetValue(ScaleXProperty);
    public static void SetScaleX(Thumb thumb, double value) => thumb.SetValue(ScaleXProperty, value);

    public static double GetScaleY(Thumb thumb) => thumb.GetValue(ScaleYProperty);
    public static void SetScaleY(Thumb thumb, double value) => thumb.SetValue(ScaleYProperty, value);

    private sealed class State
    {
        public required ScaleTransform Scale;
        public required TranslateTransform Translate;
        public double LastValue;
        public bool HasLastValue;
        public bool ArmedForMin = true;
        public bool ArmedForMax = true;
        public DispatcherTimer? RearmTimer;
    }

    // Keyed by Thumb instance so multiple ScrollBars (main window, settings panel, ...)
    // never share state/transforms.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Thumb, State> States = new();

    static ScrollBarBounceBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Thumb>(OnIsEnabledChanged);
        ScaleXProperty.Changed.AddClassHandler<Thumb>((t, e) => ApplyScale(t, e.GetNewValue<double>(), null));
        ScaleYProperty.Changed.AddClassHandler<Thumb>((t, e) => ApplyScale(t, null, e.GetNewValue<double>()));
    }

    private static void OnIsEnabledChanged(Thumb thumb, AvaloniaPropertyChangedEventArgs e)
    {
        if (!e.GetNewValue<bool>()) return;

        if (thumb.GetVisualParent() is Track)
            Hook(thumb);
        else
            thumb.AttachedToVisualTree += OnAttached;
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Thumb thumb) return;
        thumb.AttachedToVisualTree -= OnAttached;
        Hook(thumb);
    }

    private static void Hook(Thumb thumb)
    {
        if (thumb.GetVisualParent() is not Track track) return;
        if (States.TryGetValue(thumb, out _)) return; // already hooked

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform
        {
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = TimeSpan.FromMilliseconds(420),
                    Easing = new Avalonia.Animation.Easings.SpringEasing(mass: 1, stiffness: 210, damping: 13),
                },
                new Avalonia.Animation.DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = TimeSpan.FromMilliseconds(420),
                    Easing = new Avalonia.Animation.Easings.SpringEasing(mass: 1, stiffness: 210, damping: 13),
                },
            },
        };

        thumb.RenderTransform = new TransformGroup { Children = { scale, translate } };

        var state = new State { Scale = scale, Translate = translate };
        States.AddOrUpdate(thumb, state);

        // Apply whatever ScaleX/ScaleY the current pseudo-class state already set via XAML
        // Setters (in case IsEnabled attached after those, e.g. control re-templated).
        scale.ScaleX = GetScaleX(thumb);
        scale.ScaleY = GetScaleY(thumb);

        bool vertical = thumb.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right;

        track.PropertyChanged += (_, args) =>
        {
            if (args.Property != RangeBase.ValueProperty) return;
            OnTrackValueChanged(thumb, state, track, vertical);
        };
    }

    private static void ApplyScale(Thumb thumb, double? x, double? y)
    {
        if (!States.TryGetValue(thumb, out var state)) return;
        if (x is { } newX) state.Scale.ScaleX = newX;
        if (y is { } newY) state.Scale.ScaleY = newY;
    }

    private static void OnTrackValueChanged(Thumb thumb, State state, Track track, bool vertical)
    {
        double value = track.Value;
        double previous = state.HasLastValue ? state.LastValue : value;
        state.LastValue = value;
        state.HasLastValue = true;

        bool atMin = value <= track.Minimum + Epsilon;
        bool atMax = value >= track.Maximum - Epsilon;

        // Re-arm as soon as we leave an edge, so scrolling away and slamming back into the
        // same edge later fires a fresh bounce.
        if (!atMin) state.ArmedForMin = true;
        if (!atMax) state.ArmedForMax = true;

        if (atMin && state.ArmedForMin && previous > track.Minimum + Epsilon)
        {
            state.ArmedForMin = false;
            Fire(state, vertical, towardMin: true);
        }
        else if (atMax && state.ArmedForMax && previous < track.Maximum - Epsilon)
        {
            state.ArmedForMax = false;
            Fire(state, vertical, towardMin: false);
        }
    }

    private static void Fire(State state, bool vertical, bool towardMin)
    {
        // "Past the edge" direction: at the top/left edge (Minimum) the overshoot nudges
        // further up/left (negative); at the bottom/right edge (Maximum) it nudges further
        // down/right (positive) — a vertical Track here is direction-reversed (see
        // ScrollBars.axaml's IsDirectionReversed="True"), so Minimum visually sits at the
        // TOP and the "past the edge" nudge for it is still negative screen-Y (up).
        double nudge = (towardMin ? -1 : 1) * BounceDistance;

        if (vertical) state.Translate.Y = nudge;
        else state.Translate.X = nudge;

        // Snap back on the next dispatcher pass so the outbound and return hops are two
        // distinct value changes for the Transition to animate between — setting both in
        // the same tick would collapse to a no-op.
        Dispatcher.UIThread.Post(() =>
        {
            if (vertical) state.Translate.Y = 0;
            else state.Translate.X = 0;
        }, DispatcherPriority.Background);

        // Re-arm this edge after a short delay even if the user never scrolls away — covers
        // "keeps nudging the wheel while already pinned at the edge" giving occasional
        // repeat bounces instead of one then silence forever.
        state.RearmTimer?.Stop();
        state.RearmTimer = new DispatcherTimer { Interval = RearmDelay };
        state.RearmTimer.Tick += (_, _) =>
        {
            state.RearmTimer!.Stop();
            if (towardMin) state.ArmedForMin = true;
            else state.ArmedForMax = true;
        };
        state.RearmTimer.Start();
    }
}