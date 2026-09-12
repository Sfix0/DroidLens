using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DroidLens.Client.Views.Controls;

/// <summary>
/// Carries the pointer position of the click that triggered <see cref="ThemeSwitch.Toggled"/>,
/// in this control's own coordinate space (i.e. relative to the ThemeSwitch itself).
/// The host translates it into whatever coordinate space it needs (e.g. MainWindow's root
/// Panel) via TranslatePoint — kept relative here rather than pre-translated, since
/// ThemeSwitch has no idea what root the host will eventually animate against.
/// </summary>
public class ThemeToggledEventArgs : RoutedEventArgs
{
    public Point PositionInSwitch { get; }

    public ThemeToggledEventArgs(RoutedEvent routedEvent, Point positionInSwitch) : base(routedEvent)
    {
        PositionInSwitch = positionInSwitch;
    }
}

/// <summary>
/// Sun/Saturn theme toggle (ported from the Theme_Toggle.html reference).
/// Purely visual/dumb: bind <c>IsDarkTheme</c> one-way to reflect the current
/// theme, and handle <c>Toggled</c> to actually flip it (e.g. call
/// <c>MainViewModel.ToggleTheme()</c>) — this control never assumes it can
/// write IsDarkTheme itself, since the view model may only expose that as a
/// read value driven by ToggleTheme()'s own logic rather than a plain setter.
/// </summary>
public partial class ThemeSwitch : UserControl
{
    public static readonly StyledProperty<bool> IsDarkThemeProperty =
        AvaloniaProperty.Register<ThemeSwitch, bool>(nameof(IsDarkTheme));

    public bool IsDarkTheme
    {
        get => GetValue(IsDarkThemeProperty);
        set => SetValue(IsDarkThemeProperty, value);
    }

    // Fired whenever the user clicks the switch — carries no data, the host is
    // expected to flip its own theme state (e.g. call MainViewModel.ToggleTheme())
    // and then update IsDarkTheme, which is what actually drives this control's
    // visuals. Kept as a plain event (not a TwoWay bound property) because
    // IsDarkTheme on the view model may only be settable through ToggleTheme()'s
    // own logic rather than a public setter — routing through an event avoids
    // silently no-oping if that's the case.
    public static readonly RoutedEvent<ThemeToggledEventArgs> ToggledEvent =
        RoutedEvent.Register<ThemeSwitch, ThemeToggledEventArgs>(nameof(Toggled), RoutingStrategies.Bubble);

    public event EventHandler<ThemeToggledEventArgs>? Toggled
    {
        add => AddHandler(ToggledEvent, value);
        remove => RemoveHandler(ToggledEvent, value);
    }

    // Distance the thumb travels: track width (72) - thumb width (28) - 2*margin (3) = 38.
    private const double ThumbTravel = 38;

    // Button.Click only carries a plain RoutedEventArgs (no pointer position), so the
    // actual click point is captured here from the button's own PointerPressed —
    // fired immediately before Click on the same interaction — and consumed by
    // OnToggleClick right after. Defaults to the control's center so a
    // keyboard/automation-triggered toggle (no prior PointerPressed) still gets a
    // sane origin instead of (0,0).
    private Point _lastPointerDownPosition;

    public ThemeSwitch()
    {
        InitializeComponent();
        ApplyState(IsDarkTheme);
        _lastPointerDownPosition = new Point(Bounds.Width / 2, Bounds.Height / 2);
    }

    private void OnToggleButtonPointerPressed(object? sender, PointerPressedEventArgs e)
        => _lastPointerDownPosition = e.GetPosition(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsDarkThemeProperty)
            ApplyState((bool)(change.NewValue ?? false));
    }

    private void OnToggleClick(object? sender, RoutedEventArgs e)
    {
        // Bug #3: the only rotation previously running was the slow, continuous
        // 12s .sun-spin idle animation, which is far too subtle to register as
        // "something animated" in the brief moment the user looks at the switch
        // right after clicking it. Play a fast, visible one-shot spin on
        // whichever body (sun or Saturn) is about to become active, layered on
        // top of the slide/crossfade that already happens via ApplyState.
        PlayToggleSpin(SunDisc);
        PlayToggleSpin(SaturnBody);

        // Position the circular-reveal theme transition should originate from,
        // captured from the PointerPressed that preceded this Click (see
        // OnToggleButtonPointerPressed) — relative to this ThemeSwitch control.
        RaiseEvent(new ThemeToggledEventArgs(ToggledEvent, _lastPointerDownPosition));
    }

    private static async void PlayToggleSpin(Control element)
    {
        // Re-trigger the animation even if it's already mid-flight from a rapid
        // double-click: remove then re-add on the next frame so Avalonia treats
        // it as a fresh animation start rather than a no-op class toggle.
        element.Classes.Remove("toggle-spin");
        await Task.Delay(1);
        element.Classes.Add("toggle-spin");
        await Task.Delay(650);
        element.Classes.Remove("toggle-spin");
    }

    private void ApplyState(bool isDark)
    {
        Track.Classes.Set("night", isDark);

        if (Thumb.RenderTransform is TranslateTransform translate)
            translate.X = isDark ? ThumbTravel : 0;

        SunLayer.Classes.Set("hidden", isDark);
        SaturnLayer.Classes.Set("hidden", !isDark);

        Cloud1.Classes.Set("hidden", isDark);
        Cloud2.Classes.Set("hidden", isDark);
        Cloud3.Classes.Set("hidden", isDark);

        Star1.Classes.Set("hidden", !isDark);
        Star2.Classes.Set("hidden", !isDark);
        Star3.Classes.Set("hidden", !isDark);
    }
}