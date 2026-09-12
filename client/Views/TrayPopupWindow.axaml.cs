using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using DroidLens.Client.ViewModels;

namespace DroidLens.Client.Views;

/// <summary>
/// Minimal flyout shown next to the tray icon on left-click. Shares the same
/// MainViewModel instance as MainWindow (passed in via the constructor from
/// TrayIconService — see App.axaml.cs), so every button here calls straight
/// into the exact same command methods the sidebar uses. No state of its own.
/// </summary>
public partial class TrayPopupWindow : Window
{
    private readonly MainViewModel _vm;

    // Set by TrayIconService right after construction (see App.axaml.cs's own
    // Tray/mainWindow wiring) — needed so Expand_Click can restore the same
    // MainWindow instance the tray icon itself hides.
    public Window? MainWindowRef { get; set; }

    // Designer/XAML-loader parameterless ctor — never used at runtime (TrayIconService
    // always supplies the shared MainViewModel), but Avalonia's previewer wants one.
    public TrayPopupWindow() : this(new MainViewModel()) { }

    public TrayPopupWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;

        // Click-away-to-dismiss, same as the Windows volume/clock flyouts this is
        // modeled on. Deactivated fires when focus moves to any other window
        // (including clicking the tray icon again, which is handled separately
        // by TrayIconService to avoid an immediate re-show/hide fight).
        Deactivated += (_, _) => Hide();
    }

    /// <summary>
    /// Positions the window's bottom edge just above the taskbar, right-aligned
    /// near the tray icon area. Avalonia's TrayIcon click events don't report a
    /// precise icon-relative point on Windows, so this anchors to the working-area
    /// corner instead — the same approach most Windows tray flyouts fall back to
    /// when the exact icon rect isn't available.
    ///
    /// On the very first call, Width/Height still reflect the unmeasured window
    /// (SizeToContent="Height" hasn't run a layout pass yet, since the window has
    /// never actually rendered) — reading Bounds.Height at this point returns 0/NaN,
    /// which is what caused the large first-launch-only bottom/side gap. Fix:
    /// position once immediately (so the window doesn't flash at 0,0 or offscreen),
    /// then re-anchor after the first LayoutUpdated fires, by which point Bounds
    /// reflects the real measured size. Subsequent calls skip straight to the
    /// correct position since Bounds is already accurate from then on.
    /// </summary>
    public void ShowNearTray()
    {
        Show();
        PositionNearTrayBottomRight();
        Activate();

        if (!_hasLaidOutOnce)
        {
            void OnFirstLayout(object? _, EventArgs __)
            {
                LayoutUpdated -= OnFirstLayout;
                _hasLaidOutOnce = true;
                PositionNearTrayBottomRight();
            }
            LayoutUpdated += OnFirstLayout;
        }
    }

    private bool _hasLaidOutOnce;

    private void PositionNearTrayBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        PixelRect area = screen.WorkingArea;
        double h = Bounds.Height > 0 ? Bounds.Height : Height;
        Position = new PixelPoint(
            area.X + area.Width - (int)Width - 12,
            area.Y + area.Height - (int)h - 12);
    }

    // ── Quick commands — thin pass-throughs to MainViewModel, fire-and-forget
    // the same way the sidebar's own click handlers do (each Task method already
    // no-ops internally when ConnectionHealth isn't Live). ──────────────────────
    private void Flip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _vm.FlipCameraAsync();

    private void Rotate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _vm.RotateAsync();

    private void BlackScreen_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _vm.ToggleBlackScreenAsync();

    private void Flashlight_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _vm.ToggleFlashlightAsync();

    private void VCam_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _vm.ToggleVirtualCamera();

    private void StreamToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm.IsStreamBusy) return;
        _ = _vm.IsStreaming ? _vm.StopStreamAsync() : _vm.StartStreamAsync();
    }

    // Hides the popup and restores MainWindow — same "expand" behavior agreed
    // in chat: popup closes, main window comes back, tray icon itself is left
    // alone (TrayIconService keeps it visible; the user can minimize again).
    private void Expand_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Hide();
        MainWindowRef?.Show();
        MainWindowRef?.Activate();
        Expanded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised after the user hits the expand button, so TrayIconService can keep
    /// its own IsMinimizedToTray flag in sync (the window is visible again even
    /// though the tray icon itself stays up).
    /// </summary>
    public event EventHandler? Expanded;
}

/// <summary>
/// Dims the codec/quality line to a quiet 55% when the stream isn't live, instead
/// of hiding it outright — keeps "last used settings" visible at all times per
/// the chat discussion, just visually deprioritized while idle.
/// </summary>
public class BoolToDimOpacityConverter : IValueConverter
{
    public static readonly BoolToDimOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.55;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}