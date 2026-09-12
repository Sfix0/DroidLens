using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DroidLens.Client.Services;
using DroidLens.Client.Views;

namespace DroidLens.Client;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // With the default ShutdownMode.OnLastWindowClose the hidden TrayPopupWindow
            // (created by TrayIconService) counts as a live window, so after its first use
            // closing the main window never quits the app — it stays as a background process
            // with a fresh handle. Tying shutdown to the main window instead is the whole
            // intent of this app (a minimizable-to-tray utility), so jump straight there.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            // TrayIconService needs Application.Current to exist (TrayIcon.SetIcons
            // requires it) and MainWindow's MainViewModel to already be constructed
            // (MainWindow's parameterless ctor builds _vm = new() at field-init time,
            // before its own constructor body runs) — both are true by this point,
            // so it's safe to wire up here rather than inside MainWindow itself.
            var tray = new TrayIconService(mainWindow.ViewModel, mainWindow);

            // "Вийти" from the tray context menu runs through the exact same exit
            // path as the window's own X button: Window.Close() raises Closing,
            // which MainWindow_Closing already handles (StopStreamAsync if
            // configured, Teardown, actual Close) — no logic duplicated here.
            tray.ExitRequested += (_, _) => mainWindow.Close();

            // Clean up the tray icon (if a stale one is somehow still registered) and the
            // popup once the main window is genuinely closed — otherwise the hidden
            // TrayPopupWindow can outlive shutdown and the tray icon keeps a zombie
            // registration in some close paths.
            mainWindow.Closed += (_, _) =>
            {
                SingleInstanceService.StopListener();
                tray.Dispose();
            };

            mainWindow.Tray = tray;

            // Second launch of the exe (Program.Main detected another instance and
            // signalled us via SingleInstanceService) — restore the already-running
            // window instead of letting a duplicate open. StartListener runs on a
            // background thread, so marshal back to the UI thread here.
            SingleInstanceService.StartListener(() =>
                Dispatcher.UIThread.Post(() => BringToFront(mainWindow, tray)));
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ── Single-instance restore ──────────────────────────────────────────
    // Covers all three states the first instance can be in when the user
    // launches the exe again:
    //   - minimized to tray (window hidden, icon visible) → full tray restore
    //     (removes the icon, shows the window);
    //   - plain minimized to taskbar → un-minimize;
    //   - normal/background window → just pull to the front.
    private static void BringToFront(MainWindow mainWindow, TrayIconService tray)
    {
        if (tray.IsMinimizedToTray)
        {
            tray.RestoreMainWindow();
        }
        else
        {
            if (!mainWindow.IsVisible)
                mainWindow.Show();
            if (mainWindow.WindowState == WindowState.Minimized)
                mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }

        ForceForeground(mainWindow);
    }

    // Avalonia's Activate() alone doesn't always steal focus on Windows (the OS
    // may keep the second — now exiting — process's launch flash instead), so
    // nudge the HWND directly. Best-effort: never throw out of a restore path.
    private static void ForceForeground(Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle;
            if (handle is null || handle == IntPtr.Zero)
                return;

            ShowWindow(handle.Value, SW_RESTORE);
            SetForegroundWindow(handle.Value);
        }
        catch
        {
            // restore already happened via Avalonia calls above — skip native step
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}