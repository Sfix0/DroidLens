using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DroidLens.Client.Network;
using DroidLens.Client.Services;
using DroidLens.Client.ViewModels;

namespace DroidLens.Client.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    // Exposes the same MainViewModel instance MainWindow itself binds to, so
    // App.axaml.cs can hand it straight to TrayIconService/TrayPopupWindow — the
    // tray flyout must observe the exact same connection/streaming state as the
    // main window, not a second independent instance.
    public MainViewModel ViewModel => _vm;

    // Set from App.axaml.cs right after construction (see OnFrameworkInitializationCompleted) —
    // owns the tray icon lifecycle and the TrayPopupWindow flyout. Nullable because the
    // tray icon is entirely optional infrastructure: if TrayIconService fails to construct
    // for any reason, MainWindow must still function normally as a regular window.
    public TrayIconService? Tray { get; set; }

    // Guards the async-safe Closing handler below: the first Closing pass
    // cancels itself (to await StopStreamAsync before the window actually
    // goes away), then closes itself again programmatically — this flag
    // tells that second pass to let itself through instead of cancelling again.
    private bool _closeConfirmed;

    // Last IsStreaming value seen by the PropertyChanged handler below.
    // IsStreaming's setter notifies unconditionally, and the phone
    // re-broadcasts stream_status every ~2s even when nothing changed — so the
    // handler fires on every echo, not just on real start/stop edges.
    private bool _wasStreaming;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        ApplyUiScale(_vm.UiScale);
        SetupViewModel();
        SetupNoticeBanner();
        PreviewAreaControl.StartFpsCounter();
        SetupAutoPreviewPause();
        SidebarControl.Initialize(_vm); // populates combos, sets initial connection tab
        SidebarControl.RotateRequested += Sidebar_RotateRequested;
        SidebarControl.StreamToggleRequested += Sidebar_StreamToggleRequested;
        PreviewAreaControl.Initialize(_vm); // subscribes to frame events, sets initial no-signal state
        // Quick-commands (Flip/Rotate/Black screen/Flashlight) moved from a hover-reveal
        // overlay on the video preview into an always-visible sidebar card — no overlay
        // to initialize hidden anymore.

#if DEBUG
        // Dev-only self-heal: the shipped product is expected to register
        // softcam.dll once during installation (as admin, no UAC prompt at
        // runtime). In dev, the project folder gets moved/renamed/copied a
        // lot without ever re-running that installer step, so the CLSID can
        // end up pointing at a softcam.dll that no longer exists — the app's
        // own P/Invoke calls still work fine (they load the dll next to this
        // exe directly), but every *external* consumer (OBS, Discord,
        // browsers) resolves the CLSID through the registry and silently
        // gets nothing. Re-registering against the current build's dll here
        // fixes that without touching the production (installer-driven) path.
        // Design.IsDesignMode guard: the XAML previewer also constructs this
        // window, and we don't want it popping a UAC regsvr32 prompt.
        //
        // Moved off the UI thread via Task.Run: this used to run synchronously
        // right here in the constructor, and VirtualCameraInstaller.Install
        // shells out to a regsvr32-style registration step that can block for
        // over a second (or pop a UAC prompt) — a full UI-thread freeze before
        // the window even finishes constructing.
        if (!Design.IsDesignMode)
            _ = Task.Run(TryRepairStaleVCamRegistration);
#endif

        // Default the virtual camera to "on" at launch. ToggleVirtualCamera() already
        // handles the not-installed case gracefully (logs and returns false without
        // throwing), so this is safe even on machines without softcam.dll registered.
        // Fire-and-forget with retries: scCreateCamera() can spuriously return null
        // on the very first call right after process start (the DirectShow/COM-side
        // softcam server isn't always fully warmed up at this exact moment), so a
        // single synchronous attempt here was flaky — see TryAutoStartVCamAsync.
        //
        // CRITICAL: never auto-start in the XAML designer host (Rider preview). The
        // designer instantiates this window at design time, and softcam supports only
        // ONE live sender system-wide: the designer process would create (and keep
        // alive) the virtual camera and its named shared-memory object, after which
        // every real app launch gets scCreateCamera() == NULL (ERROR_ALREADY_EXISTS)
        // and the VCam toggle appears dead. Guarding with Design.IsDesignMode keeps
        // the preview process from ever touching the camera.
        if (!Design.IsDesignMode)
            _ = TryAutoStartVCamAsync();

        Closing += MainWindow_Closing;
        Opened += MainWindow_Opened;

        // Scrollbar accent + Help nav highlight follow the stream state (indigo at
        // idle, amber while streaming) via the app-wide "ScrollbarAccentBrush" /
        // "HelpNavAccentBrush"+"HelpNavAccentSoftBrush" resources that ScrollBars.axaml
        // and Cards.axaml consume with DynamicResource — see UpdateScrollbarAccent.
        // Also listen for theme flips so the amber/indigo brush is re-resolved
        // against the active theme (light amber #FFA700 differs from dark #FBBF24).
        if (Avalonia.Application.Current is { } app)
        {
            UpdateScrollbarAccent();
        }
        ActualThemeVariantChanged += (_, _) => UpdateScrollbarAccent();

        // DEBUG-only: press F9 to hide all window content and see AppBackgroundTexture
        // (glow + grain) on its own, with nothing drawn on top of it. Commented out —
        // uncomment (and the #if DEBUG block) if the background texture needs
        // debugging again in the future.
        //
        // #if DEBUG
        //         KeyDown += (_, e) =>
        //         {
        //             if (e.Key == Avalonia.Input.Key.F9)
        //                 MainContentGrid.IsVisible = !MainContentGrid.IsVisible;
        //         };
        // #endif
    }

    // ── Cascade enter animation (sidebar cards) ────────────────────────────
    // The four sidebar cards (Connection / Session / VCam / Quick commands)
    // start in XAML with Classes="card enter enter-N" but WITHOUT "shown" —
    // Animations.axaml's :is(Control).enter style sets Opacity=0 and a
    // translateY(16px) offset as their resting state. If "shown" were already
    // present at construction time, the Border would be created directly at
    // Opacity=1 with no property change for the Transitions to animate — the
    // fade/slide would just never play. The staggered Delay baked into each
    // .enter-1..enter-4 style (Animations.axaml) does the rest — no per-card
    // Task.Delay needed here, unlike Android's cascadeEnter() which times
    // each element itself.
    //
    // Getting "shown" applied at the right moment took a few iterations —
    // see the comments in MainWindow_Opened below for why a plain event
    // handler wasn't enough on this window.
    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        Opened -= MainWindow_Opened; // one-shot — never replay on reactivation/refocus

        // The window itself starts at Opacity=0 (see MainWindow.axaml) specifically
        // so nothing is ever visible before this point — an undecorated window has
        // no reliable single "now it's on screen" moment the way a normal decorated
        // one does, so waiting a frame or two (the old InvokeAsync approach) could
        // still land after the OS had already presented something.
        //
        // With SystemDecorations="None" + ExtendClientAreaToDecorationsHint, the
        // Opened event firing does NOT guarantee the OS compositor has actually
        // presented a real rendered frame yet — for an undecorated/custom-chrome
        // window, the platform can show the window before Avalonia's own render
        // loop has caught up, and nothing then forces a repaint until some external
        // event does (resize, a click anywhere including the custom title bar
        // buttons, or scroll). That was the actual bug: the sidebar cards' hidden
        // .enter state (Opacity=0) never got measured/rendered by the time "shown"
        // was applied, so the DoubleTransition/TransformOperationsTransition had no
        // observed "before" value to interpolate from and just snapped straight to
        // the end state — no visible fade/slide/cascade at all, until some external
        // event forced the missing repaint.
        //
        // Fix: force a layout + render pass ourselves via Dispatcher instead of
        // assuming Opened already means "a frame was drawn". InvalidateMeasure
        // forces Avalonia to re-layout, and posting the rest of the sequence at
        // Background priority guarantees it runs only after that layout pass has
        // actually been processed by the render loop. OnFirstLayout then double-
        // checks the cards report a real, non-zero size (they live inside a
        // ScrollViewer, which can defer arranging off-screen children) before
        // flipping "shown".
        InvalidateMeasure();
        InvalidateVisual();

        Dispatcher.UIThread.Post(() =>
        {
            Opacity = 1;

            void OnFirstLayout(object? s, EventArgs args)
            {
                if (!SidebarControl.TryPlayEntranceCascade())
                    return; // cards not actually measured/arranged yet — keep waiting

                LayoutUpdated -= OnFirstLayout;
            }
            LayoutUpdated += OnFirstLayout;

            // Belt-and-braces: in case LayoutUpdated never fires again (e.g. the cards
            // were already measured before we attached, so no further layout pass is
            // pending), also check once immediately.
            OnFirstLayout(null, EventArgs.Empty);
        }, DispatcherPriority.Background);
    }

    // Retries ToggleVirtualCamera() a few times with short delays if the first
    // attempt fails — covers the case where softcam.dll's scCreateCamera returns
    // null purely because it's called too soon after process start, before the
    // COM-side server is fully ready. Never touches the toggle if the user has
    // already interacted with it in the meantime (checked via IsVCamActive right
    // before each retry — if it's already true, VCamToggle_Click won the race).
    private async Task TryAutoStartVCamAsync()
    {
        const int maxAttempts = 3;
        TimeSpan[] delays = { TimeSpan.Zero, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800) };

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (delays[attempt] > TimeSpan.Zero)
                await Task.Delay(delays[attempt]);

            if (_vm.IsVCamActive)
                return; // already on — either a previous attempt succeeded or the user toggled it manually

            // Silent: this is a background auto-attempt at startup, not an
            // explicit user action — a banner here would greet every user whose
            // driver isn't registered. The manual toggle still notifies.
            if (_vm.ToggleVirtualCamera(showNotice: false))
                return; // success
        }
    }

#if DEBUG
    // See the call site in the constructor for why this exists and why it's
    // DEBUG-only. Requires admin (regsvr32 with runas) — if the user declines
    // the UAC prompt, this just logs and leaves VCam registration as-is, same
    // as any other Install() failure path.
    private static void TryRepairStaleVCamRegistration()
    {
        try
        {
            string currentDllPath = Path.Combine(AppContext.BaseDirectory, "softcam.dll");
            if (!File.Exists(currentDllPath))
                return; // nothing to register from here — leave IsInstalled()'s normal messaging to explain it

            if (!VirtualCamera.VirtualCameraInstaller.IsRegistrationStale(currentDllPath))
                return; // registration already points at this exact file — nothing to do

            VirtualCamera.VirtualCameraInstaller.Install(currentDllPath);
        }
        catch
        {
            // best-effort dev convenience — a failure here just means the user falls
            // back to manually re-running the installer, same as any other Install() failure path
        }
    }
#endif

    // ── ViewModel wiring ────────────────────────────────────────────────────
    private void SetupViewModel()
    {
        // Frame events (JpegFrameReceivedWithTimestamp/BgraFrameReceived) are
        // now subscribed inside PreviewAreaControl.Initialize() — this window
        // no longer touches the frame pipeline directly (see Controls/PreviewArea.axaml.cs).

        // Note: the one-shot mDNS-requery pulse on AutoWifiIcon is now wired
        // up entirely inside Sidebar.Initialize() — this window no longer
        // touches AutoWifiIcon or tab state directly (see Controls/Sidebar.axaml.cs).

        _vm.PropertyChanged += (_, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (e.PropertyName)
                {

                    case nameof(MainViewModel.IsConnected):
                        SidebarControl.SetConnectionState(_vm.IsConnected);
                        if (!_vm.IsConnected) PreviewAreaControl.OnDeviceDisconnected();
                        break;

                    case nameof(MainViewModel.IsStreaming):
                        // Edge only: the phone echoes stream_status every ~2s with
                        // unchanged values, and each echo re-fires this handler.
                        // Running the start/stop visuals on echoes wipes the
                        // paused blur via OnStreamStarting/HideStoppedPlaceholder
                        // and immediately restores it via OnIsStreamingChanged/
                        // ApplyPreviewPausedVisualState — a brightness "breathing"
                        // every 2s while paused. Real edges (phone- or PC-side)
                        // still flow through: true clears the placeholder, false
                        // shows it (see PreviewArea.OnIsStreamingChanged).
                        bool nowStreaming = _vm.IsStreaming;
                        if (nowStreaming == _wasStreaming)
                            break;
                        _wasStreaming = nowStreaming;
                        // Clear placeholder when stream starts (e.g., initiated from mobile side)
                        if (nowStreaming)
                            PreviewAreaControl.OnStreamStarting();

                        // Re-evaluate paused panel state on streaming state changes
                        PreviewAreaControl.OnIsStreamingChanged();
                        UpdateScrollbarAccent();
                        break;

                    case nameof(MainViewModel.Rotation):
                        // Reflects rotation reported by the device over the network.
                        PreviewAreaControl.SyncRotationFromDevice(_vm.Rotation);
                        break;

                    case nameof(MainViewModel.CurrentQuality):
                        SidebarControl.SyncQualityCombo();
                        break;

                    case nameof(MainViewModel.CurrentCodec):
                        SidebarControl.SyncCodecCombo();
                        break;

                    case nameof(MainViewModel.CurrentResolution):
                        SidebarControl.SyncResolutionCombo();
                        break;

                    case nameof(MainViewModel.UiScale):
                        ApplyUiScale(_vm.UiScale);
                        break;

                    case "":
                    case null:
                        SidebarControl.PopulateCombos();
                        break;

                    case nameof(MainViewModel.IsNoticeVisible):
                    case nameof(MainViewModel.NoticeKind):
                    case nameof(MainViewModel.NoticeIconKind):
                    case nameof(MainViewModel.NoticeText):
                        RefreshNoticeBanner();
                        break;
                }
            });
        };
    }

    // Swaps the app-wide "ScrollbarAccentBrush" resource (consumed by
    // ScrollBars.axaml) plus the "HelpNavAccentBrush"/"HelpNavAccentSoftBrush"
    // pair (consumed by Cards.axaml's help-nav.active styles) between
    // accent-indigo (idle) and accent-amber (streaming).
    // Mirror of the VCam switch / metric-row converters' state logic, implemented
    // as a resource swap so every ScrollBar — including ones inside their own
    // control template — and every style-selector-driven nav highlight updates
    // through DynamicResource without needing a DataContext binding inside the
    // theme. Re-resolves against the active theme so a theme flip while streaming
    // picks up that theme's amber value (dark #FBBF24 vs light #FFA700 soft fill,
    // with the live solid text/border resolving via AccentAmberOnLightBrush so Light
    // gets its darker #D97706 instead of yellow-on-yellow), and the
    // help-nav idle solid resolves via AccentIndigoOnLightBrush so Light keeps
    // its darker #4F46E5 contrast fix.
    private void UpdateScrollbarAccent()
    {
        var app = Avalonia.Application.Current;
        if (app is null) return;

        string key = _vm.IsStreaming ? "AccentAmberBrush" : "AccentIndigoBrush";
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush b)
            app.Resources["ScrollbarAccentBrush"] = b;

        string navSolidKey = _vm.IsStreaming ? "AccentAmberOnLightBrush" : "AccentIndigoOnLightBrush";
        if (app.Resources.TryGetResource(navSolidKey, app.ActualThemeVariant, out var navSolid) && navSolid is IBrush nsb)
            app.Resources["HelpNavAccentBrush"] = nsb;

        string navSoftKey = _vm.IsStreaming ? "AccentAmberSoftBrush" : "AccentIndigoSoftBrush";
        if (app.Resources.TryGetResource(navSoftKey, app.ActualThemeVariant, out var navSoft) && navSoft is IBrush nfb)
            app.Resources["HelpNavAccentSoftBrush"] = nfb;
    }

    // ── FPS counter ─────────────────────────────────────────────────────────
    // Moved to PreviewArea (owns the actual frame handlers post-refactor) —
    // see PreviewArea.axaml.cs's own SetupFpsTimer/_frameCount. Left this
    // call site as PreviewAreaControl.StartFpsCounter() so the window's own
    // lifecycle (StopFpsCounter on Closing, same as the old _fpsTimer.Stop())
    // still drives start/stop, without MainWindow owning the timer itself.

    // ── Notice banner (top-right) ────────────────────────────────────────────
    // The only error feedback in release builds. VM owns text/kind/icon/
    // visibility + auto-hide timer (see MainViewModel.Notices.cs) — this only
    // mirrors that state. Quiet by design: no border, severity reads through
    // the tinted icon alone.
    private void SetupNoticeBanner()
    {
        NoticeBanner.Classes.Remove("shown");
        NoticeBanner.IsVisible = false;
        RefreshNoticeBanner();
    }

    // Guards the delayed hide below: every Refresh bumps the sequence, so a
    // stale hide scheduled by an earlier state can't unmap a newer banner.
    private int _noticeSeq;

    private void RefreshNoticeBanner()
    {
        int seq = ++_noticeSeq;

        if (_vm.IsNoticeVisible)
        {
            NoticeText.Text = _vm.NoticeText;
            NoticeIcon.Kind = _vm.NoticeIconKind;

            string brushKey = _vm.NoticeKind switch
            {
                NoticeKind.Warning => "AccentAmberBrush",
                NoticeKind.Info    => "AccentIndigoBrush",
                _                => "ErrorBrush"
            };

            var app = Avalonia.Application.Current;
            if (app is not null &&
                app.Resources.TryGetResource(brushKey, app.ActualThemeVariant, out var brush) &&
                brush is Avalonia.Media.IBrush b)
            {
                NoticeIcon.Foreground = b;
            }

            // Two-step show (same as PreviewArea's state cards): IsVisible first,
            // "shown" on the next render pass — flipping both in one call leaves
            // the Transitions with no "before" value and the spring never plays.
            if (!NoticeBanner.IsVisible)
            {
                NoticeBanner.IsVisible = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (seq == _noticeSeq)
                        NoticeBanner.Classes.Add("shown");
                }, DispatcherPriority.Loaded);
            }
            else if (!NoticeBanner.Classes.Contains("shown"))
            {
                NoticeBanner.Classes.Add("shown");
            }
            return;
        }

        // Hide: play the spring back first, unmap once the fade (~250ms) is done.
        if (!NoticeBanner.IsVisible) return;
        NoticeBanner.Classes.Remove("shown");
        _ = Task.Delay(300).ContinueWith(_ =>
        {
            if (seq == _noticeSeq)
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_vm.IsNoticeVisible)
                        NoticeBanner.IsVisible = false;
                });
        }, TaskScheduler.Default);
    }

    private void NoticeDismiss_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _vm.ClearNotice();

    // ── Sidebar-raised requests ──────────────────────────────────────────────
    // Sidebar owns Rotate/StreamToggle's *buttons*, but VideoImage and the
    // cold-start-blur/placeholder machinery they need to coordinate with live
    // in PreviewArea, so Sidebar just raises these events and this window
    // forwards them to PreviewAreaControl's public API — same split as the
    // click handlers used to have when everything lived in one partial class.

    // Was: Rotate_Click's own body (MainWindow.Sidebar.cs) — flips the video
    // locally right away for a snappy click response, then requests the
    // actual sensor flip from the device.
    private async void Sidebar_RotateRequested(object? sender, EventArgs e)
    {
        PreviewAreaControl.RotateVideoImmediate();
        await _vm.RotateAsync();
    }

    // Was: StreamToggle_Click's own body (MainWindow.Sidebar.cs).
    // Start/StopStreamAsync already surface failures via the notice banner
    // themselves — this just must not let them escape as async-void crashes.
    private async void Sidebar_StreamToggleRequested(object? sender, EventArgs e)
    {
        if (_vm.IsStreamBusy) return;
        try
        {
            if (_vm.IsStreaming)
            {
                await _vm.StopStreamAsync();
                PreviewAreaControl.OnStreamStopped();
            }
            else
            {
                PreviewAreaControl.OnStreamStarting();
                await _vm.StartStreamAsync();
            }
        }
        catch
        {
            PreviewAreaControl.OnStreamStopped();
        }
    }

    // Window.Closing is synchronous, but sending "stop" to the phone before
    // the app disappears is an async network call — so the first pass
    // cancels the close, awaits the command, then closes the window itself
    // a second time (which this same handler lets through via _closeConfirmed).
    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
            return; // second pass — let it through, don't cancel again

        e.Cancel = true;

        if (_vm.IsStreaming && _vm.ConnectionHealth == ConnectionHealth.Live &&
            _vm.Settings.Current.StopStreamOnAppClose)
        {
            try { await _vm.StopStreamAsync(); }
            catch { /* best-effort — close regardless */ }
        }

        Teardown();
        _closeConfirmed = true;
        Close();
    }

    private void Teardown()
    {
        PreviewAreaControl.StopFpsCounter();
        _vm.Dispose();
    }

    // ── Auto-pause on minimize-to-tray / plain minimize ────────────────────
    // Two independent triggers feed PreviewArea's _minimizedToTray flag:
    //   - Hide()/Show() (tray)      → IsVisibleProperty
    //   - WindowState.Minimized     → WindowStateProperty (Hide() does NOT
    //     change WindowState, and minimizing does NOT change IsVisible — the
    //     two are orthogonal in Avalonia, hence two separate subscriptions).
    // Lives here (not in PreviewArea) because WindowStateProperty/IsVisibleProperty
    // belong to this Window, not to the PreviewArea UserControl — this just
    // forwards the resolved "minimized" bool to PreviewArea's public setter.
    private void SetupAutoPreviewPause()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty)
            {
                bool minimized = WindowState == WindowState.Minimized || !IsVisible;
                PreviewAreaControl.SetMinimizedToTray(minimized);
            }
        };
    }

    // ── Click-away to stop editing (Host field) ─────────────────────────────
    // HostField (the manual-IP AppTextField, now inside Sidebar) has no
    // "commit" button of its own — same situation SettingsPanel's
    // ScreenshotPathBox is in — so without this the field stays focused/
    // editable until something else steals focus explicitly. Handled at the
    // root Panel (covers the whole window) rather than per-card, so any
    // "click into empty space" anywhere in the window ends editing the same
    // way. Delegates the actual field-check to Sidebar's public API since
    // HostField itself now lives in that control.
    private void RootPanel_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.Source is Avalonia.Visual hit && SidebarControl.ShouldClearHostFieldFocus(hit))
        {
            SidebarControl.ClearHostFieldFocus();
        }
    }

    private void MinimizeToTray_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Tray?.MinimizeToTray();

    // ── UI Scale ─────────────────────────────────────────────────────────────
    private void ApplyUiScale(double scale)
    {
        if (scale < 0.5 || scale > 2.0) return;
        double newWidth = Math.Round(980 * scale);
        double newHeight = Math.Round(640 * scale);

        Width = newWidth;
        Height = newHeight;

        // Keep window fully on-screen if scaling up pushed edges past work area
        if (Screens.ScreenFromVisual(this) is { WorkingArea: var workArea })
        {
            double scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
            var workRight = workArea.X + workArea.Width;
            var workBottom = workArea.Y + workArea.Height;

            int winRight = Position.X + (int)(newWidth * scaling);
            int winBottom = Position.Y + (int)(newHeight * scaling);

            int newX = Position.X;
            int newY = Position.Y;

            if (winRight > workRight)
                newX = Math.Max(workArea.X, workRight - (int)(newWidth * scaling));
            if (winBottom > workBottom)
                newY = Math.Max(workArea.Y, workBottom - (int)(newHeight * scaling));

            if (newX != Position.X || newY != Position.Y)
                Position = new PixelPoint(newX, newY);
        }
    }
}