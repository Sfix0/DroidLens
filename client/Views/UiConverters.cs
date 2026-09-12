using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using DroidLens.Client.Network;
using Material.Icons;

namespace DroidLens.Client.Views;

/// <summary>Dark/Light -> icon shown on the theme-toggle button.</summary>
public class ThemeIconConverter : IValueConverter
{
    public static readonly ThemeIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool isDark && isDark) ? MaterialIconKind.WeatherNight : MaterialIconKind.WeatherSunny;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>IsStreaming -> Play/Stop icon on the session toggle button.</summary>
public class StreamIconConverter : IValueConverter
{
    public static readonly StreamIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool streaming && streaming) ? MaterialIconKind.Stop : MaterialIconKind.Play;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// IsStreaming -> text/icon color for the session toggle button.
/// Not-streaming (Start, indigo fill) -> white, matching the Android Start
/// button. Streaming (Stop, amber fill) -> OnAccentBrush (dark), matching
/// Android's Stop button — white-on-amber contrasts far worse (~1.9:1)
/// than dark-on-amber (~9.6:1), so this state intentionally differs from
/// the Start state rather than always following the same white.
/// </summary>
public class StreamButtonTextColorConverter : IValueConverter
{
    public static readonly StreamButtonTextColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool streaming = value is bool b && b;
        if (!streaming) return Brushes.White;

        var app = Avalonia.Application.Current;
        if (app is not null && app.Resources.TryGetResource("OnAccentBrush", app.ActualThemeVariant, out var brush) && brush is IBrush br)
            return br;
        return Brushes.Black;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>ConnectionHealth -> status brush (Live=success, Stale=warning, Disconnected=error).</summary>
public class ConnectionHealthToBrushConverter : IValueConverter
{
    public static readonly ConnectionHealthToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var app = Avalonia.Application.Current;
        string key = value switch
        {
            ConnectionHealth.Live  => "SuccessBrush",
            ConnectionHealth.Stale => "WarningBrush",
            _                      => "ErrorBrush"
        };
        if (app is not null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush b)
            return b;
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// (IsFlashlightOn, IsFrontCamera) -> icon, mirroring Android's flashIcon logic:
/// rear camera uses Flash/FlashOff (real LED torch), front camera uses
/// WeatherSunny/Brightness6 (screen-brightness "flashlight") since there's no LED to drive.
/// </summary>
public class FlashlightIconConverter : IMultiValueConverter
{
    public static readonly FlashlightIconConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on    = values.Count > 0 && values[0] is bool b0 && b0;
        bool front = values.Count > 1 && values[1] is bool b1 && b1;

        return front
            ? (on ? MaterialIconKind.WbSunny      : MaterialIconKind.BrightnessLow)
            : (on ? MaterialIconKind.Flash        : MaterialIconKind.FlashOff);
    }
}

/// <summary>IsBlackScreen -> Eye/EyeOff icon on the black-screen toggle button.</summary>
public class BlackScreenIconConverter : IValueConverter
{
    public static readonly BlackScreenIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool on && on) ? MaterialIconKind.EyeOff : MaterialIconKind.Eye;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// (IsActive, IsStreaming, [IsDarkTheme]) -> accent brush for switch tracks (Virtual Camera,
/// StopOnDisconnect, StopOnAppClose).
/// On/off is independent of streaming — only the *color* of the "on" state follows IsStreaming:
/// amber (yellow) while a stream is live, indigo otherwise. Off is always the neutral surface fill.
/// Accepts an optional 3rd binding (e.g. IsDarkTheme) so theme switches trigger instant re-evaluation.
/// </summary>
public class VCamAccentConverter : IMultiValueConverter
{
    public static readonly VCamAccentConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isOn      = values.Count > 0 && values[0] is bool b0 && b0;
        bool streaming = values.Count > 1 && values[1] is bool b1 && b1;

        var app = Avalonia.Application.Current;
        // Off-state was "SurfaceContainerBrush" — nearly identical to the card
        // background behind it, so the track barely read as its own shape.
        // SwitchTrackBrush (App.axaml) is a dedicated, genuinely lighter tone
        // meant specifically for this. Note this converter's return value is
        // what actually reaches Border.Background here (bound via MultiBinding
        // directly in the view) — a style Setter on Border.switch-track in
        // Controls.axaml can NEVER win against this, since a local/binding
        // value always takes precedence over a style value. That's why
        // changing the style's Background alone previously had zero effect.
        string key = !isOn
            ? "SwitchTrackBrush"
            : (streaming ? "AccentAmberBrush" : "AccentIndigoBrush");

        if (app is not null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush br)
            return br;
        return Brushes.Gray;
    }
}

/// <summary>
/// IsStreaming -> accent brush, used to recolor the bottom metric-row icons
/// (FPS / RTT / Latency / Resolution): amber while streaming, indigo otherwise —
/// same accent language as the Start/Stop stream button.
/// </summary>
public class IsStreamingToAccentConverter : IValueConverter
{
    public static readonly IsStreamingToAccentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var app = Avalonia.Application.Current;
        bool streaming = value is bool b && b;
        string key = streaming ? "AccentAmberBrush" : "AccentIndigoBrush";
        if (app is not null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush br)
            return br;
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool (is-on) -> thumb alignment for the Virtual Camera switch (right when on, left when off).</summary>
public class BoolToAlignConverter : IValueConverter
{
    public static readonly BoolToAlignConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool (is-on) -> thumb travel offset (px) for the Virtual Camera switch, driving a
/// TranslateTransform.X instead of an instant HorizontalAlignment snap. Track is 46px
/// wide, thumb is 21px with 2px margin each side, so full travel = 46 - 21 - 2*2 = 21px.
/// Paired with the (TranslateTransform.X) transition on Ellipse.switch-thumb in
/// Animations.axaml, this makes the toggle actually slide instead of jumping.
/// </summary>
public class BoolToThumbOffsetConverter : IValueConverter
{
    public static readonly BoolToThumbOffsetConverter Instance = new();
    private const double TravelDistance = 21;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? TravelDistance : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// IsFrontCamera -> lens icon, mirroring Android's CameraToggleCard:
/// front = Account (selfie/person), back = Image (landscape/scenery).
/// </summary>
public class CameraLensIconConverter : IValueConverter
{
    public static readonly CameraLensIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool front && front) ? MaterialIconKind.Account : MaterialIconKind.Image;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Rotation (0/180, the only two orientations the device reports) -> RenderTransform
/// angle for the PhoneRotateLandscape icon in PreviewArea's top status capsule. There's
/// no separate "flipped" glyph in Material Icons for phone-rotate-landscape, so instead
/// of two icon assets this reuses the single glyph and spins it 180° — same trick as
/// VideoImage's own RotateTransform.
/// </summary>
public class RotationToAngleConverter : IValueConverter
{
    public static readonly RotationToAngleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int rotation && rotation == 180 ? 180d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// (IsUsbConnection, NetworkSpeedText) -> display text for the network chip,
/// replacing the old two-TextBlock IsVisible/!IsVisible pair now that the
/// icon side is handled by WifiSignalGauge.IsUsbMode: "USB" over a wired
/// connection, otherwise the live network-speed readout.
/// </summary>
public class UsbOrNetworkTextConverter : IMultiValueConverter
{
    public static readonly UsbOrNetworkTextConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isUsb = values.Count > 0 && values[0] is bool b && b;
        string speedText = values.Count > 1 && values[1] is string s ? s : string.Empty;
        return isUsb ? "USB" : speedText;
    }
}

/// <summary>
/// bool (is-on) -> accent brush for plain settings switches (SettingsWindow's
/// "stop stream on disconnect/app close"), where on/off is not tied to
/// IsStreaming at all — on = indigo, off = neutral surface. Same lookup
/// pattern as VCamAccentConverter/IsStreamingToAccentConverter, just without
/// a second (IsStreaming) input.
/// </summary>
public class BoolToAccentBrushConverter : IValueConverter
{
    public static readonly BoolToAccentBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var app = Avalonia.Application.Current;
        bool on = value is bool b && b;
        // Off-state was "SurfaceContainerBrush" — same fix as VCamAccentConverter
        // above: this converter's return value is bound directly to
        // Border.Background in the view, so it always wins over any style
        // Setter targeting Border.switch-track.
        string key = on ? "AccentIndigoBrush" : "SwitchTrackBrush";
        if (app is not null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush br)
            return br;
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}