using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DroidLens.Client.Network;
using DroidLens.Client.ViewModels;
using DroidLens.Client.Views;

namespace DroidLens.Client.Services;

/// <summary>
/// Owns the Windows tray icon's full lifecycle: does not appear on startup —
/// the icon is created and registered with the OS only after the user
/// explicitly minimizes to tray, and is removed again when the window is
/// restored (see chat discussion; toggling TrayIcon.IsVisible in this Avalonia
/// build does not reliably hide a registered icon, so registration itself is
/// deferred). Tracks connection/streaming state to swap its color, and
/// opens/closes the TrayPopupWindow flyout on left-click.
///
/// No .ico asset yet (deliberately deferred — see chat notes): the icon is
/// rendered on the fly as a simple colored dot via RenderTargetBitmap, which is
/// trivial to swap for a real logo bitmap later without touching any of the
/// state-tracking logic below.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Window _mainWindow;
    private readonly Application _app;
    private readonly NativeMenu _menu;
    private readonly TrayPopupWindow _popup;
    private TrayIcon? _trayIcon;

    /// <summary>True while the app is minimized to tray (window hidden, icon visible).</summary>
    public bool IsMinimizedToTray { get; private set; }

    public TrayIconService(MainViewModel vm, Window mainWindow)
    {
        _vm = vm;
        _mainWindow = mainWindow;

        // TrayIcon must be registered on the Application via TrayIcon.Icons for the
        // platform backend to actually create the native icon — constructing a bare
        // TrayIcon object alone does not register it with the OS. See:
        // https://github.com/AvaloniaUI/Avalonia/discussions/17764
        //
        // CRITICAL: the icon is NOT registered in the constructor. The Windows backend
        // creates and shows a tray icon the moment it's added to TrayIcon.Icons, and
        // `IsVisible = false` in this Avalonia 11.3.x build does not reliably keep it
        // out of the notification area at startup. Registration is deferred until the
        // user explicitly minimizes to tray (MinimizeToTray) and the icon is removed
        // again on restore — so the app shows no tray icon until that happens.
        _app = Application.Current
            ?? throw new InvalidOperationException("Application.Current not initialized yet.");

        _popup = new TrayPopupWindow(vm) { MainWindowRef = mainWindow };
        _popup.Expanded += (_, _) => IsMinimizedToTray = false;
        // Expand_Click in the popup only needs to Show/Activate the main window,
        // so wiring the raw Window reference above is enough — RestoreMainWindow()
        // below additionally removes the tray icon itself, which stays owned by this
        // service (see OnTrayLeftClicked / NativeMenu "Показати").

        _menu = BuildMenu();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.ConnectionHealth) or nameof(MainViewModel.IsStreaming))
                UpdateIcon();
        };

        // Safety net for the "icon stays in the tray while the window is open" bug:
        // removing the icon must not depend on each individual code path that shows
        // the main window (tray menu "Показати", the tray popup's Expand button, the
        // not-connected left-click, etc.). Instead, whenever the main window becomes
        // visible by ANY path, take the tray icon down. Registration itself still
        // only ever happens from MinimizeToTray() (an explicit user action), so this
        // never resurrects the icon at startup — removing a null icon is a no-op.
        _mainWindow.GetObservable(Visual.IsVisibleProperty).Subscribe(new VisibilityObserver(v =>
        {
            if (v) RemoveTrayIcon();
        }));
    }

    /// <summary>Minimal IObserver adapter — lets us react to window-visibility changes without pulling in System.Reactive.</summary>
    private sealed class VisibilityObserver(Action<bool> onNext) : IObserver<bool>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(bool visible) => onNext(visible);
    }

    private NativeMenu BuildMenu()
    {
        var showItem = new NativeMenuItem("Показати DroidLens");
        showItem.Click += (_, _) => RestoreMainWindow();

        var exitItem = new NativeMenuItem("Вийти");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        return new NativeMenu
        {
            Items =
            {
                showItem,
                new NativeMenuItemSeparator(),
                exitItem,
            },
        };
    }

    /// <summary>
    /// Raised when the user picks "Вийти" from the tray context menu — wired up
    /// in App.axaml.cs to run the exact same close path as the window's own X
    /// button (StopStreamAsync if configured, Teardown, Close).
    /// </summary>
    public event EventHandler? ExitRequested;

    /// <summary>Minimizes MainWindow to tray: hides the window, creates+shows the tray icon.</summary>
    public void MinimizeToTray()
    {
        _popup.Hide();
        EnsureIcon();
        IsMinimizedToTray = true;
        _mainWindow.Hide();
    }

    /// <summary>
    /// Lazily creates and registers the tray icon with the OS. Called from
    /// MinimizeToTray (and nowhere else) so the notification-area icon does not
    /// exist on startup — it only appears once the user minimizes to tray.
    /// </summary>
    private TrayIcon EnsureIcon()
    {
        if (_trayIcon is not null)
            return _trayIcon;

        var trayIcon = new TrayIcon
        {
            ToolTipText = "DroidLens",
            IsVisible = true,
            Menu = _menu,
        };
        trayIcon.Clicked += (_, _) => OnTrayLeftClicked();
        TrayIcon.SetIcons(_app, new TrayIcons { trayIcon });
        _trayIcon = trayIcon;
        UpdateIcon();
        return trayIcon;
    }

    /// <summary>
    /// Restores the main window from tray (or from a plain minimized state):
    /// hides the popup, removes the tray icon, shows and activates the window.
    /// Public so the single-instance handler (second launch) can bring the
    /// already-running app to the front instead of opening a duplicate.
    /// </summary>
    public void RestoreMainWindow()
    {
        _popup.Hide();
        RemoveTrayIcon();
        IsMinimizedToTray = false;
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    /// <summary>Removes the tray icon from the OS by unregistering the whole collection.</summary>
    private void RemoveTrayIcon()
    {
        if (_trayIcon is null)
            return;

        TrayIcon.SetIcons(_app, null); // disposing the removed icon is handled by Avalonia
        _trayIcon = null;
    }

    private void OnTrayLeftClicked()
    {
        // Per the chat discussion: if the phone isn't connected, clicking the
        // tray just restores the full window instead of showing an empty/disabled
        // popup — the popup's whole reason to exist is quick access to live
        // stream controls, which don't apply when there's nothing connected.
        if (!_vm.IsConnected)
        {
            RestoreMainWindow();
            return;
        }

        if (_popup.IsVisible)
        {
            _popup.Hide();
        }
        else
        {
            _popup.ShowNearTray();
        }
    }

    /// <summary>
    /// Looks up a Color resource (e.g. "AccentAmberColor") from the app's actual
    /// theme dictionary — App.axaml defines separate Dark/Light values keyed by
    /// ThemeVariant, so TryGetResource with the app's ActualThemeVariant is what
    /// keeps this in sync with whichever theme is active, rather than always
    /// reading the Dark-variant value regardless of what's actually applied.
    /// Falls back to the given color if the resource isn't found for any reason
    /// (e.g. resource renamed) — the tray icon should never throw or go blank.
    /// </summary>
    private Color ResolveAccentColor(string resourceKey, Color fallback)
    {
        if (_app.TryGetResource(resourceKey, _app.ActualThemeVariant, out var value) && value is Color color)
            return color;

        return fallback;
    }

    /// <summary>
    /// Renders a small flat-colored dot as the tray icon bitmap — Live (accent
    /// amber, matching AccentAmberBrush) vs Idle (accent indigo, matching
    /// AccentIndigoBrush) — mirrors the indigo/amber state pairing used in the
    /// tray popup's own LIVE/IDLE badge. Colors are pulled from Application
    /// resources rather than hardcoded hex: App.axaml defines a different amber
    /// value for the light theme (#B45309) vs dark (#FBBF24), and while
    /// RequestedThemeVariant is currently pinned to Dark, resolving from
    /// resources here means this stays correct automatically if theme switching
    /// is ever added — no second place to remember to update.
    /// Swap this method for a real .ico/bitmap load once a logo asset exists;
    /// nothing else in this class needs to change.
    /// </summary>
    private void UpdateIcon()
    {
        if (_trayIcon is null) return; // not registered (nothing minimized to tray yet)

        bool live = _vm.IsStreaming && _vm.ConnectionHealth == ConnectionHealth.Live;
        Color dotColor = live
            ? ResolveAccentColor("AccentAmberColor", fallback: Color.Parse("#FBBF24"))
            : ResolveAccentColor("AccentIndigoColor", fallback: Color.Parse("#818CF8"));

        const int size = 32;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var brush = new SolidColorBrush(dotColor);
            var rect = new Rect(4, 4, size - 8, size - 8);
            ctx.DrawEllipse(brush, null, rect.Center, rect.Width / 2.0, rect.Height / 2.0);
        }

        // Going through a PNG-encoded stream rather than handing RenderTargetBitmap
        // straight to WindowIcon(IBitmap) — there's a known Avalonia issue where
        // IBitmap-typed icons render solid black on some platforms/backends
        // (github.com/AvaloniaUI/Avalonia/issues/6733). Encode/decode round-trip
        // avoids that path entirely at the cost of one extra allocation per icon swap,
        // which only happens on connection/streaming state changes, not per-frame.
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream);
        bitmap.Dispose();
        pngStream.Position = 0;
        _trayIcon.Icon = new WindowIcon(pngStream);
        _trayIcon.ToolTipText = live
            ? $"DroidLens — LIVE · {_vm.DeviceModel}"
            : $"DroidLens — {_vm.DeviceModel}";
    }

    public void Dispose()
    {
        RemoveTrayIcon();
        _popup.Close();
    }
}