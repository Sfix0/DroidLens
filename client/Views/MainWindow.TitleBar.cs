using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JetBrains.Annotations;

namespace DroidLens.Client.Views;

// ── Title bar: drag/minimize/close chrome, plus the Settings and About buttons
//    and everything they trigger (overlay show/hide, theme circular reveal) —
//    all of it lives behind the Info/Cog icons sitting in this same header.
//    Split out from MainWindow.axaml.cs specifically because this is the file
//    intended to grow — planned improvements to the title bar (custom
//    controls, extra buttons, etc.) land here instead of mixing into the
//    residual file. ──
public partial class MainWindow
{
    // ── Window chrome ────────────────────────────────────────────────────
    [UsedImplicitly]
    private void Header_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    [UsedImplicitly]
    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    [UsedImplicitly]
    private void Close_Click(object? sender, RoutedEventArgs e)    => Close();

    // ── Header icon-button hover zoom ─────────────────────────────────────────
    // TransformOperationsTransition on RenderTransform doesn't animate in this
    // Avalonia build (the string scale(1.2) style-setter hover snapped with no
    // visible motion), so each title-bar button carries an explicit ScaleTransform
    // with its own DoubleTransition (declared in MainWindow.axaml — the same
    // pattern the app's theme/vcam thumb transitions use) and these handlers just
    // tweak ScaleX/ScaleY on pointer enter/exit.
    [UsedImplicitly]
    private void HeaderBtn_PointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
        => SetHeaderScale(sender, 1.18);

    [UsedImplicitly]
    private void HeaderBtn_PointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
        => SetHeaderScale(sender, 1.0);

    private static void SetHeaderScale(object? sender, double scale)
    {
        if (sender is Control control && control.RenderTransform is ScaleTransform st)
        {
            st.ScaleX = scale;
            st.ScaleY = scale;
        }
    }

    // ── Settings overlay ────────────────────────────────────────────────────
    [UsedImplicitly]
    private async void SettingsBtn_Click(object? sender, RoutedEventArgs e)
    {
        // Was: new SettingsWindow(_vm) + await ShowDialog(this) — opened a
        // second OS-level Window (its own taskbar entry). Now just attaches
        // the shared MainViewModel to the overlay control and shows it/the
        // dim backdrop in place, all within this same window.
        SettingsOverlay.Attach(_vm);

        DimOverlay.IsVisible = true;
        SettingsOverlay.IsVisible = true;

        // IsVisible alone doesn't animate — it's the Opacity Transition declared
        // in the .axaml that actually fades things in, but it only fires on a
        // genuine property value change. Setting IsVisible=true and Opacity=1
        // in the same tick could still catch both controls at their Opacity=0
        // starting value when the transition starts, so nothing visibly fades.
        // Yielding one frame first guarantees the "hidden" state has actually
        // rendered at least once before flipping to the "shown" value below.
        await Task.Delay(1);

        DimOverlay.Opacity = 1;
        SettingsOverlay.Opacity = 1;
    }

    private async void SettingsOverlay_CloseRequested(object? sender, EventArgs e)
    {
        // Play the reverse fade, then only actually hide (IsVisible=false)
        // once it's finished — hiding immediately would cut the fade-out
        // short since IsVisible removes the control from the visual tree the
        // instant it's set, regardless of any in-flight Opacity transition.
        DimOverlay.Opacity = 0;
        SettingsOverlay.Opacity = 0;

        await Task.Delay(280); // matches the 0.28s DoubleTransition duration in the .axaml

        SettingsOverlay.IsVisible = false;
        DimOverlay.IsVisible = false;
    }

    // ── About overlay ────────────────────────────────────────────────────────
    // Same show/hide pattern as Settings above, just targeting AboutOverlay —
    // see SettingsBtn_Click's comments for the rationale behind the one-frame
    // Task.Delay(1) before fading in, and SettingsOverlay_CloseRequested's for
    // why IsVisible is only flipped off after the fade-out finishes.
    [UsedImplicitly]
    private async void AboutBtn_Click(object? sender, RoutedEventArgs e)
    {
        AboutOverlay.Attach(_vm);

        DimOverlay.IsVisible = true;
        AboutOverlay.IsVisible = true;

        await Task.Delay(1);

        DimOverlay.Opacity = 1;
        AboutOverlay.Opacity = 1;
    }

    private async void AboutOverlay_CloseRequested(object? sender, EventArgs e)
    {
        DimOverlay.Opacity = 0;
        AboutOverlay.Opacity = 0;

        await Task.Delay(280); // matches the 0.28s DoubleTransition duration in the .axaml

        AboutOverlay.IsVisible = false;
        DimOverlay.IsVisible = false;
    }

    // Clicking the dim backdrop (i.e. anywhere outside the panel itself, since
    // DimOverlay and the overlay panels are separate sibling elements in the
    // same root Panel — a click on a panel never reaches this handler) closes
    // whichever overlay is currently open. DimOverlay is now shared between
    // Settings and About (see MainWindow.axaml), so this checks which one is
    // actually visible rather than assuming it's always Settings. Only one of
    // the two is ever visible at a time in practice — each *Btn_Click only
    // opens its own panel, and neither can open without going through this
    // same backdrop first — but the explicit check is what enforces that
    // rather than silently relying on it.
    [UsedImplicitly]
    private void DimOverlay_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (SettingsOverlay.IsVisible)
            SettingsOverlay_CloseRequested(sender, EventArgs.Empty);
        else if (AboutOverlay.IsVisible)
            AboutOverlay_CloseRequested(sender, EventArgs.Empty);
    }

    // ── Theme circular reveal ────────────────────────────────────────────────
    // SettingsPanel no longer flips the theme itself (see ThemeSwitch_Toggled
    // in SettingsPanel.axaml.cs) — it just relays the click point, already
    // translated into SettingsOverlay's own coordinate space. One more
    // translation (SettingsOverlay -> this window's root Panel) gives us the
    // point every following step needs.
    [UsedImplicitly]
    private void SettingsOverlay_ThemeToggleRequested(object? sender, Point pointInSettingsOverlay)
    {
        Point originInRoot = SettingsOverlay.TranslatePoint(pointInSettingsOverlay, RootPanel)
                              ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        _ = PlayThemeRevealAsync(originInRoot);
    }

    // Runs an Android-style "circular reveal" around the theme flip:
    //   1. Screenshot the window exactly as it looks right now (old theme).
    //   2. Flip the theme immediately underneath (near-instant — this is what
    //      RequestedThemeVariant assignment already does on its own).
    //   3. Show the screenshot as an opaque layer on top, so nothing visibly
    //      changed yet, then clip it down to an ellipse that grows from
    //      `originInRoot` out to a radius that comfortably covers the whole
    //      window's diagonal — as the ellipse grows, the CLIPPED (i.e. no
    //      longer covered) area reveals the new theme underneath.
    //   4. Once the circle covers the full window, the screenshot layer is
    //      fully clipped away — remove it, nothing left to animate.
    // This "peel away the old screenshot" approach (rather than growing a
    // reveal of the *new* theme) is used because Avalonia applies
    // RequestedThemeVariant synchronously and globally — there's no clean way
    // to render "two themes at once" to composite between, but there's always
    // exactly one real frame (the one right before toggling) to screenshot.
    private async Task PlayThemeRevealAsync(Point originInRoot)
    {
        if (ThemeRevealOverlay is null || RootPanel is null) return;

        var rootSize = RootPanel.Bounds.Size;
        if (rootSize.Width <= 0 || rootSize.Height <= 0)
        {
            _vm.ToggleTheme(); // fallback: still flips the theme, just no animation
            return;
        }

        double scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        var pixelSize = new PixelSize(
            (int)Math.Ceiling(rootSize.Width * scaling),
            (int)Math.Ceiling(rootSize.Height * scaling));

        // 1. Screenshot of the current (pre-toggle) frame.
        RenderTargetBitmap snapshot;
        try
        {
            snapshot = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
            snapshot.Render(RootPanel);
        }
        catch (Exception ex)
        {
            // Rendering can fail on some GPU/driver combinations — never let a
            // cosmetic animation take the whole theme toggle down with it.
            AppLog.E("ThemeReveal", ex, () => "Snapshot failed, falling back to instant toggle");
            _vm.ToggleTheme();
            return;
        }

        // 2. Flip the theme now — happens synchronously, and since the
        // screenshot above is about to fully cover the window, the user
        // won't see the instantaneous swap underneath.
        _vm.ToggleTheme();

        // 3. Show the "old" screenshot on top, sized/positioned 1:1 with the
        // root Panel so the clip geometry's coordinates line up exactly with
        // pointer coordinates gathered from that same Panel.
        ThemeRevealOverlay.Source = snapshot;
        ThemeRevealOverlay.Width = rootSize.Width;
        ThemeRevealOverlay.Height = rootSize.Height;
        ThemeRevealOverlay.IsVisible = true;

        // Radius needed for the circle to fully clear every corner of the
        // window from an arbitrary origin point — the farthest corner from
        // originInRoot sets the minimum; a little extra avoids a visible hard
        // edge from anti-aliasing right at the end of the animation.
        double maxRadius = MaxDistanceToCorners(originInRoot, rootSize) + 8;

        // EllipseGeometry has no built-in Transitions support the way a
        // control's properties do, so the radius is driven by hand via
        // successive Rect assignments in the loop below rather than a
        // declarative DoubleTransition — same visual result, just animated
        // frame-by-frame in code instead.

        // The overlay must show the OLD screenshot everywhere EXCEPT inside
        // the growing circle — i.e. the circle is a hole being cut into it,
        // not the shape being kept. Avalonia's Geometry combine support lets
        // us build that directly: (full-rect) minus (growing ellipse).
        var fullRect = new RectangleGeometry(new Rect(0, 0, rootSize.Width, rootSize.Height));

        const int steps = 24;
        const int totalMs = 480;
        var easing = new CubicEaseOut();

        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            double eased = easing.Ease(t);
            double radius = maxRadius * eased;

            var ellipse = new EllipseGeometry(new Rect(
                originInRoot.X - radius, originInRoot.Y - radius, radius * 2, radius * 2));

            ThemeRevealOverlay.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, ellipse);
            // Signature: CombinedGeometry(GeometryCombineMode, Geometry1, Geometry2) —
            // result = fullRect MINUS ellipse (Exclude subtracts Geometry2 from Geometry1),
            // i.e. everything except the growing circle stays covered by the old screenshot.

            await Task.Delay(totalMs / steps);
        }

        // 4. Circle has fully covered the window — the overlay is now clipped
        // to nothing, so it can come down without any visible pop.
        ThemeRevealOverlay.IsVisible = false;
        ThemeRevealOverlay.Clip = null;
        ThemeRevealOverlay.Source = null; // release the bitmap
        snapshot.Dispose();
    }

    private static double MaxDistanceToCorners(Point origin, Size bounds)
    {
        Point[] corners =
        {
            new(0, 0),
            new(bounds.Width, 0),
            new(0, bounds.Height),
            new(bounds.Width, bounds.Height)
        };

        double max = 0;
        foreach (var corner in corners)
        {
            double dx = corner.X - origin.X;
            double dy = corner.Y - origin.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > max) max = dist;
        }
        return max;
    }
}