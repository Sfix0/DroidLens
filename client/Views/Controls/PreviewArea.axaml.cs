using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DroidLens.Client.ViewModels;
using JetBrains.Annotations;

namespace DroidLens.Client.Views.Controls;

// ── Right column: video preview surface + its overlays (cold-start/paused
//    blur, no-signal/paused state cards, hover capsule, screenshot flash) and
//    the metric row underneath (FPS/RTT/Latency/Resolution).
//
//    Owns the whole frame pipeline (JPEG/BGRA decode hand-off from
//    MainViewModel's network events) and everything that used to live in
//    MainWindow.Preview.cs plus the Preview-related half of
//    MainWindow.axaml.cs. The host (MainWindow) only calls the public
//    methods below — Rotate/StreamToggle requests come in from Sidebar via
//    MainWindow, which forwards them here. ──
public partial class PreviewArea : UserControl
{
    private MainViewModel? _vm;

    public PreviewArea()
    {
        InitializeComponent();
    }

    // ── Hover-capsule visibility helper ─────────────────────────────────────
    // The "hidden" class only fades opacity to 0 (state-fade style); Avalonia
    // controls stay hit-test-visible at Opacity="0" by default, so without
    // this the capsule/pills/scrim were still clickable and still raised
    // ToolTip/PointerEntered while invisible — the reported bug. Toggling
    // IsHitTestVisible in lockstep with the class keeps "hidden" meaning
    // both invisible AND non-interactive.
    private static void SetOverlayHidden(Control control, bool hidden)
    {
        if (hidden) control.Classes.Add("hidden");
        else control.Classes.Remove("hidden");
        control.IsHitTestVisible = !hidden;
    }

    // ── Setup ────────────────────────────────────────────────────────────────
    // Called once by the host right after DataContext is assigned. Subscribes
    // to the frame-arrival events and sets the initial "no signal" state —
    // replaces what used to run directly in MainWindow's constructor.
    public void Initialize(MainViewModel vm)
    {
        _vm = vm;
        _vm.JpegFrameReceivedWithTimestamp += OnJpegFrame;
        _vm.BgraFrameReceived += OnBgraFrame;
        _vm.PropertyChanged += OnVmPropertyChanged;

        NoSignalPanel.IsVisible = true;
        NoSignalPanel.Classes.Add("shown"); // no animated entrance here — window itself
                                             // is still Opacity=0 at this point (see
                                             // MainWindow.axaml's Window.Transitions comment),
                                             // so there's nothing visible yet to animate from

        ApplyPreviewPausedVisualState(); // sets initial icon/label/tooltip text (not paused yet)
        UpdateFpsCapBadge(); // reflects whatever LimitPreviewFps was loaded from settings.json
    }

    // Cares about LimitPreviewFps and AmbientFillEnabled — both toggled live
    // from SettingsPanel while this window stays open. Everything else
    // PreviewArea needs is either pushed in directly (frame events) or read
    // on demand (_vm.IsStreaming etc).
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LimitPreviewFps))
            UpdateFpsCapBadge();
        else if (e.PropertyName == nameof(MainViewModel.AmbientFillEnabled) && _vm?.AmbientFillEnabled != true)
        {
            // Turned off mid-stream: hide immediately rather than waiting for
            // the next throttled sample tick to notice via ShouldUpdateAmbientFill.
            AmbientTopBar.IsVisible = false;
            AmbientBottomBar.IsVisible = false;
        }
    }

    // Shows the "½" badge next to the FPS label whenever the cap is on, so the
    // (now real, uncapped) FpsLabel number isn't misread as the preview's
    // actual render rate — see the Interlocked.Increment placement comment in
    // OnJpegFrame/OnBgraFrame for why the label itself stays uncapped.
    private void UpdateFpsCapBadge() => FpsCapBadge.IsVisible = _vm is { LimitPreviewFps: true };

    // ── FPS ──────────────────────────────────────────────────────────────────
    public void UpdateFps(int fps) => FpsLabel.Text = fps.ToString();

    // Counts frames that actually made it to the UI hand-off (i.e. survived
    // the PreviewRenderingEnabled gate and, when LimitPreviewFps is on, the
    // skip-every-other-frame check) — see OnJpegFrame/OnBgraFrame, which
    // Interlocked.Increment this right before Dispatcher.Post. Deliberately
    // counts *rendered* frames, not raw decoded ones: with the FPS cap
    // enabled the label should read ~half the incoming rate, not the full
    // decode rate, since that's what's actually reaching the screen.
    private int _frameCount;
    private readonly System.Timers.Timer _fpsTimer = new(1000);

    // Was: MainWindow.SetupFpsTimer — moved here since this control now owns
    // the actual frame handlers (OnJpegFrame/OnBgraFrame) that need to feed
    // the counter; MainWindow no longer subscribes to the frame events itself
    // (see MainWindow.axaml.cs's Initialize comment), so _frameCount there had
    // nothing left incrementing it.
    public void StartFpsCounter()
    {
        _fpsTimer.Elapsed += (_, _) =>
        {
            int fps = Interlocked.Exchange(ref _frameCount, 0);
            Dispatcher.UIThread.Post(() => UpdateFps(fps));
        };
        _fpsTimer.Start();
    }

    public void StopFpsCounter() => _fpsTimer.Stop();

    // ── Rotation ─────────────────────────────────────────────────────────────
    // true = rotated 180°, toggled locally by the Rotate button for a snappy
    // click response, before the device round-trip confirms.
    private bool _isVideoReversed;

    // Was: Rotate_Click's own body (MainWindow.Sidebar.cs) — the immediate
    // local flip. Called by MainWindow in response to Sidebar.RotateRequested.
    public void RotateVideoImmediate()
    {
        _isVideoReversed = !_isVideoReversed;
        if (VideoImage.RenderTransform is RotateTransform rt)
            rt.Angle = _isVideoReversed ? 180 : 0;
    }

    // Reflects rotation reported by the device over the network — called by
    // MainWindow from its MainViewModel.Rotation property-changed handler.
    public void SyncRotationFromDevice(int angle)
    {
        _isVideoReversed = angle == 180;
        if (VideoImage.RenderTransform is RotateTransform rt)
            rt.Angle = angle;
    }

    // ── Stream start/stop coordination ──────────────────────────────────────
    // Was: StreamToggle_Click's "stop" branch body. Called by MainWindow
    // after _vm.StopStreamAsync() completes.
    public void OnStreamStopped()
    {
        Interlocked.Exchange(ref _awaitingFirstFrameFlag, 0);
        ShowStoppedPlaceholder();
    }

    // Was: StreamToggle_Click's "start" branch body (the part before
    // _vm.StartStreamAsync() is awaited). Called by MainWindow before it
    // awaits _vm.StartStreamAsync().
    public void OnStreamStarting()
    {
        Interlocked.Exchange(ref _awaitingFirstFrameFlag, 1);
        HideStoppedPlaceholder(); // clear any leftover "paused" state before the new frame arrives
    }

    // Was: OnDisconnected()'s body (MainWindow.axaml.cs). Called by
    // MainWindow's IsConnected property-changed handler.
    public void OnDeviceDisconnected()
    {
        _isVideoReversed = false;
        if (VideoImage.RenderTransform is RotateTransform rt)
            rt.Angle = 0;
        _lastResW = _lastResH = 0;
        ResolutionLabel.Text = "—";
        Interlocked.Exchange(ref _awaitingFirstFrameFlag, 0);
        ShowStoppedPlaceholder(); // same blurred-last-frame + placeholder treatment as an explicit Stop
    }

    // Re-evaluates the hover-reveal capsule/scrim visibility and the
    // paused-indicator card — called by MainWindow whenever
    // MainViewModel.IsStreaming changes.
    //
    // This is the ONLY path that fires for phone-side start/stop (stream_status
    // arriving over the command channel): OnStreamStarting/OnStreamStopped below
    // only run for PC-side button clicks. So both edges must fully own the
    // placeholder state here — a phone-side stop shows the same blurred
    // last-frame + hint card as a PC-side one, and a phone-side start clears it
    // and re-arms the first-frame flag for the cold-start mask. The explicit
    // PC-path calls stay (all methods idempotent) so feedback there is instant
    // instead of waiting for the phone's ACK round-trip.
    public void OnIsStreamingChanged()
    {
        if (_vm is { IsStreaming: true })
        {
            Interlocked.Exchange(ref _awaitingFirstFrameFlag, 1);
            HideStoppedPlaceholder();
        }
        else
        {
            ShowStoppedPlaceholder();
        }

        ApplyPreviewPausedVisualState();
        // Stream just stopped while the pointer happened to still be over the
        // preview: the hover pills/scrim are still visible from before, but
        // screenshot/pause no longer do anything useful without a frame.
        // Hide them now instead of waiting for the pointer to leave and re-enter.
        if (_vm is { IsStreaming: false })
        {
            SetOverlayHidden(ScreenshotButton, true);
            SetOverlayHidden(PausePreviewButton, true);
            SetOverlayHidden(PreviewBottomScrim, true);
            SetOverlayHidden(PreviewControlsCapsule, true);
        }
    }

    // ── Video frame plumbing ────────────────────────────────────────────────
    private int _mjpegPending;            // drop-frame flag (0 = free, 1 = busy)
    private WriteableBitmap? _mjpegBitmap; // cached MJPEG bitmap for dispose
    private int _bgraPending;              // drop-frame flag for H.264 path (0 = free, 1 = busy)
    private int _lastResW, _lastResH;      // live frame resolution, view-local

    // ── Ambient light fill (letterbox filler) ───────────────────────────────
    // Replaces an earlier attempt that composited a live blurred copy of the
    // full frame (RenderTargetBitmap + BlurEffect on the UI thread every
    // ~200ms) — that caused visible UI stutter and, since it's a real GPU
    // blur pass on a whole-frame bitmap, still wasn't cheap enough to run
    // often, so it also looked choppy. This version follows YouTube's
    // "Ambient Mode" approach instead: sample one averaged color from each
    // edge strip of the raw pixel buffer (cheap integer math, no bitmap, no
    // GPU work) and animate two solid-color bars toward it — the visible
    // "blur" is just each Border's own soft edge (BlurEffect on a solid
    // color is far cheaper than on a bitmap) plus the Ambient bars sitting
    // behind VideoImage. Motion comes from color, not from a live picture.
    private static readonly TimeSpan AmbientMinInterval = TimeSpan.FromMilliseconds(1500);
    private readonly Stopwatch _ambientStopwatch = Stopwatch.StartNew();
    // How many rows from the top/bottom edge to average — a thin strip is
    // enough since these bars only need to feel "roughly the right color",
    // not reproduce any actual image detail.
    private const int AmbientEdgeSampleRows = 6;
    // Skip every Nth pixel horizontally while summing — full-row precision
    // buys nothing for an average, and this keeps the sample O(w/step) per
    // row instead of O(w).
    private const int AmbientSampleStepX = 4;

    // Same gating reasoning as before: no point sampling/animating a layer
    // that's fully covered (placeholder/manual pause), not on screen
    // (minimized/tray), or disabled by the user in Settings.
    private bool ShouldUpdateAmbientFill()
    {
        if (_vm?.AmbientFillEnabled != true) return false;
        if (!PreviewRenderingEnabled) return false;
        if (_placeholderActive || _manualPreviewPaused) return false;
        if (_ambientStopwatch.Elapsed < AmbientMinInterval) return false;
        _ambientStopwatch.Restart();
        return true;
    }

    // Averages the top and bottom edge strips of a BGRA8888 buffer. Pure
    // integer arithmetic over a tiny fraction of the frame's pixels — safe
    // and cheap to call from a background thread (both call sites do,
    // before the UI-thread Dispatcher.Post), so this never competes with
    // decode/VCam work for UI-thread time.
    private static (Color top, Color bottom) SampleEdgeColors(byte[] bgra, int w, int h)
    {
        var top = SampleStrip(bgra, w, h, fromTop: true);
        var bottom = SampleStrip(bgra, w, h, fromTop: false);
        return (top, bottom);
    }

    private static Color SampleStrip(byte[] bgra, int w, int h, bool fromTop)
    {
        int rows = Math.Min(AmbientEdgeSampleRows, h);
        long sumB = 0, sumG = 0, sumR = 0;
        int count = 0;
        int rowStride = w * 4;

        for (int row = 0; row < rows; row++)
        {
            int y = fromTop ? row : h - 1 - row;
            int rowStart = y * rowStride;
            for (int x = 0; x < w; x += AmbientSampleStepX)
            {
                int i = rowStart + x * 4;
                if (i + 2 >= bgra.Length) break;
                sumB += bgra[i];
                sumG += bgra[i + 1];
                sumR += bgra[i + 2];
                count++;
            }
        }

        if (count == 0) return Colors.Black;
        return new Color(255, (byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
    }

    // Pushes freshly-sampled colors onto the two ambient bars. Must run on
    // the UI thread (touches Border.Background). Also sizes each bar to the
    // actual letterbox gap for the current frame's aspect ratio vs the
    // surface's — see ComputeLetterboxHeight — so the bars only cover the
    // black gap itself and never overlap the visible video.
    private void UpdateAmbientFill(Color top, Color bottom, int frameW, int frameH)
    {
        double barHeight = ComputeLetterboxHeight(frameW, frameH);
        if (barHeight < 1)
        {
            // No letterbox at the current window size/aspect — nothing to fill.
            AmbientTopBar.IsVisible = false;
            AmbientBottomBar.IsVisible = false;
            return;
        }

        AmbientTopBar.Height = barHeight;
        AmbientBottomBar.Height = barHeight;
        AmbientTopBar.Background = new SolidColorBrush(top);
        AmbientBottomBar.Background = new SolidColorBrush(bottom);
        AmbientTopBar.IsVisible = true;
        AmbientBottomBar.IsVisible = true;
    }

    // How tall each letterbox bar is for a frameW×frameH image shown with
    // Stretch="Uniform" inside PreviewSurface's current bounds. Mirrors the
    // Uniform-fit math: the frame is scaled to fit width, and whatever
    // vertical space is left over (surface height - fitted frame height) / 2
    // is the bar height on each side. Returns 0 when the frame already fills
    // the surface vertically (e.g. a wide/landscape frame in a portrait-ish
    // window) — there's no gap to fill in that case.
    private double ComputeLetterboxHeight(int frameW, int frameH)
    {
        double surfaceW = PreviewSurface.Bounds.Width;
        double surfaceH = PreviewSurface.Bounds.Height;
        if (surfaceW <= 0 || surfaceH <= 0 || frameW <= 0 || frameH <= 0) return 0;

        double fittedHeight = surfaceW * ((double)frameH / frameW);
        double gap = (surfaceH - fittedHeight) / 2;
        return gap > 0.5 ? gap : 0;
    }

    // ── Cold-start blur mask ────────────────────────────────────────────────
    // Covers the first moment of a fresh stream, where the encoder's rate
    // controller hasn't warmed up yet and emits visibly blocky frames.
    private static readonly TimeSpan ColdStartBlurDuration = TimeSpan.FromMilliseconds(600);
    private CancellationTokenSource? _coldStartBlurCts;
    // 1 from OnStreamStarting() until the first real frame lands in
    // OnJpegFrame/OnBgraFrame — that's the actual moment artifacted frames
    // start appearing, not the moment the button was clicked (there's a
    // command round-trip + encoder start-up in between). Int (not bool) so
    // Interlocked.Exchange can consume it atomically — OnJpegFrame runs on a
    // Task.Run thread and OnBgraFrame on the decoder thread, so both could
    // race to be "the first frame" depending on which codec is active.
    private int _awaitingFirstFrameFlag;
    // The blockiness only shows up on the phone-side encoder's very first
    // start() after its process launches (cold rate-controller) — a second
    // stream start within the same app session doesn't re-trigger it, so
    // there's nothing to mask after that. Once true, ShowColdStartBlur()
    // becomes a no-op for the rest of this window's lifetime.
    private bool _hasStreamedOnceThisSession;

    // True whenever the "paused/disconnected" placeholder (blur + NoSignalPanel)
    // is the intended state — i.e. after an explicit Stop or a disconnect, until
    // a genuinely new stream start happens. Frames that arrive from the network
    // after Stop was clicked (already in flight, or a last buffered frame from
    // the decoder) still land in OnJpegFrame/OnBgraFrame and would otherwise
    // clear NoSignalPanel/blur the instant they show up, making the placeholder
    // flash and vanish. Checking this flag lets those trailing frames update
    // VideoImage.Source (harmless) without touching the placeholder visibility.
    private bool _placeholderActive;

    // MJPEG: decode JPEG on a background thread — single BGRA pass for UI + VCam
    private void OnJpegFrame(byte[] jpeg, long arrivalTicks)
    {
        if (_vm is null) return;

        // Drop frame if the previous one is still being processed
        if (Interlocked.CompareExchange(ref _mjpegPending, 1, 0) != 0)
            return;

        Task.Run(() =>
        {
            try
            {
                // Decode JPEG and extract BGRA — all off the UI thread
                using var ms  = new MemoryStream(jpeg);
                using var bmp = new Bitmap(ms);
                int w = (int)bmp.Size.Width;
                int h = (int)bmp.Size.Height;

                var wb = new WriteableBitmap(
                    new PixelSize(w, h),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Opaque);

                // Single lock: extract BGRA pixels once
                var bgra = new byte[w * h * 4];
                using (var fb = wb.Lock())
                {
                    bmp.CopyPixels(new PixelRect(0, 0, w, h),
                        fb.Address, fb.RowBytes * h, fb.RowBytes);
                    Marshal.Copy(fb.Address, bgra, 0, bgra.Length);
                }

                // Decode-latency diagnostic: network-arrival -> decoded-frame-ready,
                // same measurement point as the H.264 path.
                _vm.ReportMjpegDecodeLatency(arrivalTicks);

                // VCam gets the same BGRA buffer — no second decode. Unaffected by
                // preview-render pausing: VCam must keep receiving frames regardless.
                if (_vm.IsVCamActive)
                    _vm.VCam.PushFrame(bgra, w, h);

                // Ambient fill: sample edge colors here, on this background thread,
                // off the same bgra buffer already extracted above — throttled by
                // ShouldUpdateAmbientFill so the (already cheap) sampling only runs
                // a couple times a second, not once per frame.
                (Color top, Color bottom)? ambient = null;
                if (ShouldUpdateAmbientFill())
                    ambient = SampleEdgeColors(bgra, w, h);

                // Counts the true incoming rate (matches what the phone is actually
                // streaming / what VCam receives) — deliberately placed before the
                // FPS-cap skip below so the plate shows the real number, not the
                // halved preview-render rate. See FpsCapBadge in the .axaml for the
                // "½" indicator that clarifies the preview itself renders slower.
                Interlocked.Increment(ref _frameCount);

                // UI thread: only swap the already-decoded WriteableBitmap. JPEG decode
                // above already happened either way (VCam needs it) — pausing here only
                // skips the WriteableBitmap hand-off + UI-thread hop + Skia composite,
                // not the decode itself.
                if (!PreviewRenderingEnabled)
                {
                    wb.Dispose(); // never handed to VideoImage.Source — nothing else will dispose it
                    Interlocked.Exchange(ref _mjpegPending, 0);
                    return;
                }

                // FPS cap (Settings > Interface > "Limit preview FPS"): skip every
                // other frame's UI hand-off when enabled. Decode + VCam push above
                // already happened regardless — this only trims the preview's own
                // render rate, not the underlying stream.
                if (_vm.LimitPreviewFps)
                {
                    _skipNextMjpegPreviewFrame = !_skipNextMjpegPreviewFrame;
                    if (_skipNextMjpegPreviewFrame)
                    {
                        wb.Dispose();
                        Interlocked.Exchange(ref _mjpegPending, 0);
                        return;
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (Interlocked.Exchange(ref _awaitingFirstFrameFlag, 0) == 1)
                            ShowColdStartBlur();

                        // MJPEG frames arrive already rotated by the phone (Android
                        // bakes the correct orientation into the JPEG bytes for every
                        // angle — 0/90/180/270). Unlike H.264/H.265, where OnBgraFrame
                        // ignores the decoded rotation and relies entirely on this
                        // RenderTransform, applying it here on top of an already-
                        // rotated MJPEG frame double-rotates the image — this is what
                        // caused MJPEG to render upside-down at 270° after the Android
                        // fix. Force it to identity for MJPEG so the preview always
                        // shows the exact same orientation the JPEG bytes carry —
                        // which is also what gets pushed to VCam.PushFrame below,
                        // keeping preview and virtual camera output in sync.
                        if (VideoImage.RenderTransform is RotateTransform mjpegRt && mjpegRt.Angle != 0)
                            mjpegRt.Angle = 0;

                        var old = _mjpegBitmap;
                        _mjpegBitmap      = wb;
                        VideoImage.Source = wb;
                        // Trailing frames from an already-stopped stream can still land
                        // here; don't let them clear the "paused" placeholder — only a
                        // real new stream start (OnStreamStarting) does that.
                        if (!_placeholderActive)
                            HidePreviewStateCard(NoSignalPanel);
                        if (ambient is { } a)
                            UpdateAmbientFill(a.top, a.bottom, w, h);
                        old?.Dispose();
                        UpdateResolutionLabel(w, h);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _mjpegPending, 0);
                    }
                }, DispatcherPriority.Render);
            }
            catch
            {
                Interlocked.Exchange(ref _mjpegPending, 0);
            }
        });
    }

    // ── H.264: receive BGRA32 pixels from FFmpeg decoder thread ────────────
    // Called from decoder thread — bytes are already copied, safe to use
    private void OnBgraFrame(byte[] bgra, int width, int height, int rotation)
    {
        if (_vm is null) return;

        // Drop frame if the previous one hasn't finished reaching the screen yet.
        if (Interlocked.CompareExchange(ref _bgraPending, 1, 0) != 0)
            return;

        // Counts the true incoming rate — see the matching comment in
        // OnJpegFrame above.
        Interlocked.Increment(ref _frameCount);

        // FFmpeg decode already happened upstream of this call (VideoDecoder —
        // VCam needs it regardless), so pausing here skips the *entire* UI tail.
        if (!PreviewRenderingEnabled)
        {
            Interlocked.Exchange(ref _bgraPending, 0);
            return;
        }

        // FPS cap (Settings > Interface > "Limit preview FPS"): skip every other
        // frame's UI hand-off when enabled. VCam already received this frame via
        // VideoDecoder.BgraFrameReceived (see MainViewModel) — that path is
        // untouched, only the preview's own render rate is trimmed.
        if (_vm.LimitPreviewFps)
        {
            _skipNextBgraPreviewFrame = !_skipNextBgraPreviewFrame;
            if (_skipNextBgraPreviewFrame)
            {
                Interlocked.Exchange(ref _bgraPending, 0);
                return;
            }
        }

        // Ambient fill: sample here, still off the UI thread, off the same
        // buffer passed in by the decoder — same reasoning as OnJpegFrame.
        (Color top, Color bottom)? ambient = null;
        if (ShouldUpdateAmbientFill())
            ambient = SampleEdgeColors(bgra, width, height);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (Interlocked.Exchange(ref _awaitingFirstFrameFlag, 0) == 1)
                    ShowColdStartBlur();

                // If the previous frame was a Bitmap (MJPEG) — explicitly dispose it
                if (VideoImage.Source is Bitmap oldBitmap)
                {
                    VideoImage.Source = null;
                    oldBitmap.Dispose();
                }

                var bmp = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Opaque);

                using (var fb = bmp.Lock())
                {
                    int copyLen = Math.Min(bgra.Length, fb.RowBytes * height);
                    Marshal.Copy(bgra, 0, fb.Address, copyLen);
                }

                var old = VideoImage.Source as WriteableBitmap;
                VideoImage.Source = bmp;
                if (!_placeholderActive)
                    HidePreviewStateCard(NoSignalPanel);
                if (ambient is { } a)
                    UpdateAmbientFill(a.top, a.bottom, width, height);
                old?.Dispose();
                UpdateResolutionLabel(width, height);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _bgraPending, 0);
            }
        }, DispatcherPriority.Render);
    }

    // Only touches the TextBlock when dimensions actually change — this runs on
    // every decoded frame (30-60/sec), so skipping redundant Text= writes matters.
    private void UpdateResolutionLabel(int w, int h)
    {
        if (w == _lastResW && h == _lastResH) return;
        _lastResW = w;
        _lastResH = h;
        ResolutionLabel.Text = $"{w}×{h}";
    }

    // ── Preview hover handlers ──────────────────────────────────────────────
    [UsedImplicitly]
    private void PreviewSurface_PointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        _isPointerOverPreview = true;
        if (_vm is not { IsStreaming: true }) return;
        SetOverlayHidden(ScreenshotButton, false);
        SetOverlayHidden(PausePreviewButton, false);
        SetOverlayHidden(PreviewBottomScrim, false);
        SetOverlayHidden(PreviewControlsCapsule, false);
    }

    [UsedImplicitly]
    private void PreviewSurface_PointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        _isPointerOverPreview = false;
        if (_manualPreviewPaused) return;
        SetOverlayHidden(ScreenshotButton, true);
        SetOverlayHidden(PausePreviewButton, true);
        SetOverlayHidden(PreviewBottomScrim, true);
        SetOverlayHidden(PreviewControlsCapsule, true);
    }

    // Same hover-zoom pattern as MainWindow.TitleBar.cs's HeaderBtn_Pointer*
    // (TransformOperationsTransition doesn't animate reliably on this
    // Avalonia build, hence the explicit ScaleTransform + manual set here).
    // Kept as its own small copy rather than shared with TitleBar — these
    // pill buttons live in a different control now and this is only 10 lines.
    [UsedImplicitly]
    private void PillBtn_PointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
        => SetPillScale(sender, 1.18);

    [UsedImplicitly]
    private void PillBtn_PointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
        => SetPillScale(sender, 1.0);

    private static void SetPillScale(object? sender, double scale)
    {
        if (sender is Control control && control.RenderTransform is ScaleTransform st)
        {
            st.ScaleX = scale;
            st.ScaleY = scale;
        }
    }

    // ── Cold-start / stopped placeholder ────────────────────────────────────
    private void ShowColdStartBlur()
    {
        if (_hasStreamedOnceThisSession) return; // artifacts only occur on the very first start
        _hasStreamedOnceThisSession = true;

        _coldStartBlurCts?.Cancel();
        var cts = new CancellationTokenSource();
        _coldStartBlurCts = cts;

        ColdStartBlurOverlay.Opacity = 1;
        ColdStartBlurOverlay.IsVisible = true;
        if (ColdStartBlurOverlay.RenderTransform is ScaleTransform coldStartScale)
        {
            coldStartScale.ScaleX = 1.05;
            coldStartScale.ScaleY = 1.05;
        }
        BlurDimScrim.IsVisible = true;
        BlurDimScrim.Opacity = 1;

        _ = FadeOutColdStartBlurAsync(cts.Token);
    }

    private async Task FadeOutColdStartBlurAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ColdStartBlurDuration, token);
        }
        catch (TaskCanceledException)
        {
            return; // superseded by a newer start/stop — that call owns the overlay now
        }

        ColdStartBlurOverlay.Opacity = 0; // animated by the DoubleTransition in XAML
        if (ColdStartBlurOverlay.RenderTransform is ScaleTransform coldStartScaleOut)
        {
            coldStartScaleOut.ScaleX = 1;
            coldStartScaleOut.ScaleY = 1;
        }
        BlurDimScrim.Opacity = 0;
        await Task.Delay(400, CancellationToken.None); // let the fade finish before unmapping
        if (!token.IsCancellationRequested)
        {
            ColdStartBlurOverlay.IsVisible = false;
            BlurDimScrim.IsVisible = false;
        }
    }

    // "Paused" state: user explicitly stopped the stream while the device is
    // still connected. Unlike the cold-start mask, this has no auto-fade —
    // it stays until a new stream actually starts producing frames again.
    private void ShowStoppedPlaceholder()
    {
        _coldStartBlurCts?.Cancel(); // any pending cold-start fade-out no longer applies
        _placeholderActive = true;

        ColdStartBlurOverlay.Opacity = 1;
        ColdStartBlurOverlay.IsVisible = true;
        if (ColdStartBlurOverlay.RenderTransform is ScaleTransform stoppedScale)
        {
            stoppedScale.ScaleX = 1.05;
            stoppedScale.ScaleY = 1.05;
        }
        BlurDimScrim.IsVisible = true;
        BlurDimScrim.Opacity = 1;
        ShowPreviewStateCard(NoSignalPanel);
        // Fully covered by ColdStartBlurOverlay now — stop compositing it and
        // let the next real stream start re-show it via UpdateAmbientFill.
        AmbientTopBar.IsVisible = false;
        AmbientBottomBar.IsVisible = false;
        // "Stream stopped" and "rendering manually paused" are different states —
        // don't show both center overlays at once.
        ApplyPreviewPausedVisualState();
    }

    private void HideStoppedPlaceholder()
    {
        _placeholderActive = false;
        ColdStartBlurOverlay.IsVisible = false;
        ColdStartBlurOverlay.Opacity = 1;
        if (ColdStartBlurOverlay.RenderTransform is ScaleTransform hideScale)
        {
            hideScale.ScaleX = 1;
            hideScale.ScaleY = 1;
        }
        BlurDimScrim.IsVisible = false;
        BlurDimScrim.Opacity = 0;
        HidePreviewStateCard(NoSignalPanel);
        ApplyPreviewPausedVisualState();
    }

    // ── Preview center-state card entrance/exit ─────────────────────────────
    // Shared by NoSignalPanel and PreviewPausedPanel, both styled via
    // Border.preview-state-card (Cards.axaml). Two-step show (IsVisible=true
    // THEN add "shown" on the next render pass) is required — flipping both
    // in the same call means the card starts already in its final state with
    // nothing for the Transition to animate from.
    private void ShowPreviewStateCard(Border card)
    {
        card.IsVisible = true;
        Dispatcher.UIThread.Post(() => card.Classes.Add("shown"), DispatcherPriority.Loaded);
    }

    private async void HidePreviewStateCard(Border card)
    {
        card.Classes.Remove("shown"); // plays the fade+scale-down in reverse
        await Task.Delay(350);        // matches the card's own Transition durations
        if (!card.Classes.Contains("shown")) // still not re-shown by a fast toggle
            card.IsVisible = false;
    }

    // ── Preview rendering pause ─────────────────────────────────────────────
    // Manual pause, toggled by PausePreviewButton_Click.
    private bool _manualPreviewPaused;

    // True whenever the preview genuinely isn't visible to the user — either
    // minimized to tray or plain-minimized. Set via SetMinimizedToTray, called
    // by the host from its window-state subscription (see MainWindow.axaml.cs).
    private bool _minimizedToTray;

    // Effective render gate checked at the top of OnBgraFrame/OnJpegFrame's UI-tail.
    private bool PreviewRenderingEnabled => !_manualPreviewPaused && !_minimizedToTray;

    // ── Preview FPS cap ──────────────────────────────────────────────────────
    // Artificially halves the *preview render* rate by skipping every other
    // frame right before the Dispatcher.Post hand-off — decode, VCam.PushFrame,
    // and the network/device stream are all upstream of this check and keep
    // running at full rate. Two independent counters (MJPEG vs H.264/H.265)
    // since only one pipeline is ever active at a time, but kept separate so
    // neither path can desync the other if that ever changes.
    private bool _skipNextMjpegPreviewFrame;
    private bool _skipNextBgraPreviewFrame;

    // Called by the host whenever WindowState/IsVisible changes (auto-pause on
    // minimize-to-tray / plain minimize). Was: SetMinimizedToTray in
    // MainWindow.Preview.cs.
    public void SetMinimizedToTray(bool minimized)
    {
        if (_minimizedToTray == minimized) return;
        _minimizedToTray = minimized;

        // Restoring: only resume rendering automatically if the user hadn't
        // ALSO manually paused before minimizing — manual pause takes
        // priority and is never silently cleared by a window-state change.
        ApplyPreviewPausedVisualState();
    }

    [UsedImplicitly]
    private void PausePreviewButton_Click(object? sender, RoutedEventArgs e)
        => SetManualPreviewPaused(!_manualPreviewPaused);

    private void SetManualPreviewPaused(bool paused)
    {
        _manualPreviewPaused = paused;
        TriggerPausePreviewWobble();
        ApplyPreviewPausedVisualState();
    }

    private async void TriggerPausePreviewWobble()
    {
        PausePreviewIcon.Classes.Remove("spin");
        PausePreviewIcon.Classes.Add("spin");
        await Task.Delay(220);
        PausePreviewIcon.Classes.Remove("spin");
    }

    private async Task UnmapBlurOverlayAfterFadeAsync()
    {
        await Task.Delay(400);
        bool stillWantsBlur =
            (_manualPreviewPaused && _vm is { IsStreaming: true } && !_minimizedToTray) || _placeholderActive;
        if (stillWantsBlur) return;

        ColdStartBlurOverlay.IsVisible = false;
        BlurDimScrim.IsVisible = false;
    }

    private void ApplyPreviewPausedVisualState()
    {
        if (_vm is null) return;

        PausePreviewButton.Classes.Set("active", _manualPreviewPaused);

        PausePreviewIcon.Kind = _manualPreviewPaused
            ? Material.Icons.MaterialIconKind.Video
            : Material.Icons.MaterialIconKind.VideoOff;
        Avalonia.Controls.ToolTip.SetTip(PausePreviewButton,
            _manualPreviewPaused ? _vm.Loc["label.show_preview"] : _vm.Loc["label.hide_preview"]);

        if (_manualPreviewPaused)
        {
            SetOverlayHidden(PausePreviewButton, false);
            SetOverlayHidden(PreviewBottomScrim, false);
            SetOverlayHidden(PreviewControlsCapsule, false);
        }
        else if (!_isPointerOverPreview)
        {
            SetOverlayHidden(PausePreviewButton, true);
            SetOverlayHidden(PreviewBottomScrim, true);
            SetOverlayHidden(PreviewControlsCapsule, true);
        }

        bool showPausedIndicator =
            _manualPreviewPaused && _vm.IsStreaming && !_placeholderActive && !_minimizedToTray;
        if (showPausedIndicator && !PreviewPausedPanel.IsVisible)
            ShowPreviewStateCard(PreviewPausedPanel);
        else if (!showPausedIndicator && PreviewPausedPanel.IsVisible)
            HidePreviewStateCard(PreviewPausedPanel);

        // Blur/zoom/dim the frozen frame the same way cold-start and stopped-
        // stream do (ShowColdStartBlur / ShowStoppedPlaceholder) — manual
        // pause is a third path into the same "frame isn't live" visual, so
        // it needs the same treatment. Guarded by !_placeholderActive so this
        // never fights with ShowStoppedPlaceholder, which already owns the
        // overlay whenever the stream itself is stopped.
        if (!_placeholderActive)
        {
            if (showPausedIndicator)
            {
                ColdStartBlurOverlay.Opacity = 1;
                ColdStartBlurOverlay.IsVisible = true;
                if (ColdStartBlurOverlay.RenderTransform is ScaleTransform pausedScale)
                {
                    pausedScale.ScaleX = 1.05;
                    pausedScale.ScaleY = 1.05;
                }
                BlurDimScrim.IsVisible = true;
                BlurDimScrim.Opacity = 1;
                // Same reasoning as ShowStoppedPlaceholder — fully covered now.
                AmbientTopBar.IsVisible = false;
                AmbientBottomBar.IsVisible = false;
            }
            else
            {
                ColdStartBlurOverlay.Opacity = 0;
                if (ColdStartBlurOverlay.RenderTransform is ScaleTransform unpausedScale)
                {
                    unpausedScale.ScaleX = 1;
                    unpausedScale.ScaleY = 1;
                }
                BlurDimScrim.Opacity = 0;
                _ = UnmapBlurOverlayAfterFadeAsync();
            }
        }

        ScreenshotButton.IsEnabled = !_manualPreviewPaused;
    }

    private bool _isPointerOverPreview;

    // ── Screenshot ───────────────────────────────────────────────────────────
    [UsedImplicitly]
    private async void ScreenshotButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (VideoImage.Source is not WriteableBitmap frame) return; // nothing to capture (no signal yet)

        _ = PlayScreenshotFlashAsync(); // fire-and-forget — purely cosmetic, never blocks the save

        try
        {
            string folder = string.IsNullOrWhiteSpace(_vm.ScreenshotPath)
                ? MainViewModel.DefaultScreenshotFolder
                : _vm.ScreenshotPath;
            Directory.CreateDirectory(folder); // no-op if it already exists

            string fileName = $"DL_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            string fullPath = Path.Combine(folder, fileName);

            int w = frame.PixelSize.Width, h = frame.PixelSize.Height;
            using var snapshot = new WriteableBitmap(
                new PixelSize(w, h),
                frame.Dpi,
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Opaque);
            using (var fb = snapshot.Lock())
            {
                frame.CopyPixels(
                    new PixelRect(0, 0, w, h),
                    fb.Address, fb.RowBytes * h, fb.RowBytes);
            }

            string tmpPath = fullPath + ".tmp";
            try
            {
                await Task.Run(() => snapshot.Save(tmpPath));
                File.Move(tmpPath, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
        }
        catch (Exception ex)
        {
            _vm.ShowNotice(_vm.Loc["notice.screenshot_failed"], ViewModels.NoticeKind.Warning, Material.Icons.MaterialIconKind.CameraOutline);
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[Screenshot] Save failed: {ex}");
#endif
        }
    }

    private static readonly TimeSpan ScreenshotFlashHold = TimeSpan.FromMilliseconds(90);

    private async Task PlayScreenshotFlashAsync()
    {
        ScreenshotFlashOverlay.IsVisible = true;
        ScreenshotFlashOverlay.Opacity = 0.85;

        await Task.Delay(ScreenshotFlashHold);
        ScreenshotFlashOverlay.Opacity = 0; // animated by the DoubleTransition in XAML

        await Task.Delay(150); // let the fade-out finish before unmapping
        ScreenshotFlashOverlay.IsVisible = false;
    }
}