using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DroidLens.Client.Models;
using DroidLens.Client.Network;
using DroidLens.Client.Services;
using DroidLens.Client.VirtualCamera;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DroidLens.Client.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    // ── Services ───────────────────────────────────────────────────────────────
    public readonly SettingsService     Settings = new();
    public LocalizationService Loc { get; } = new();

    // ── mDNS ──────────────────────────────────────────────────────────────────
    private readonly MdnsDiscovery _mdns = new();

    // ── USB (adb) ─────────────────────────────────────────────────────────────
    private readonly UsbDiscovery _usb = new();

    public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = new();

    // ── Network ────────────────────────────────────────────────────────────────
    private readonly CommandClient _cmd   = new();
    private readonly VideoRouter   _video = new();

    // ── Video decoders ────────────────────────────────────────────────────────
    // One per hardware-encoded codec — only the one matching CurrentCodec is
    // ever Start()ed/fed at a time (see StartVideo), mirroring VideoRouter's
    // one-active-client-at-a-time pattern on the network side.
    private readonly VideoDecoder _h264Decoder = new(VideoElementaryCodec.H264);
    private readonly VideoDecoder _h265Decoder = new(VideoElementaryCodec.H265);

    // ── Virtual Camera ────────────────────────────────────────────────────────
    public readonly VirtualCameraService VCam = new();

    // ── Decode-latency averaging (same 1s-window pattern as the view's FPS timer) ──
    private double _latencySumMs;
    private int    _latencySampleCount;
    private readonly object _latencyLock = new();
    private System.Threading.Timer? _latencyTimer;
    private System.Threading.Timer? _streamElapsedTimer;
    private DateTime _streamStartUtc;

    private bool _isVCamActive;
    public bool IsVCamActive
    {
        get => _isVCamActive;
        private set { _isVCamActive = value; OnPropertyChanged(); }
    }

    public bool IsVCamAvailable => VCam.IsAvailable;

    // ── Video frame events ─────────────────────────────────────────────────────
    public event Action<byte[]>?        JpegFrameReceived;
    public event Action<byte[], long>?  JpegFrameReceivedWithTimestamp;
    public event Action<byte[], int, int, int>? BgraFrameReceived; // H.264 decoded BGRA + rotation

    // ── Constructor ────────────────────────────────────────────────────────────
    public MainViewModel()
    {
        // TEMP DIAGNOSTIC — remove once the freeze source is confirmed.
        // Field initializers above (Settings, Loc, _mdns, _usb, _cmd, _video,
        // _h264Decoder, _h265Decoder, VCam — all "= new()") already ran by the
        // time this line executes, since C# runs field initializers before the
        // constructor body. So sw0 here only times the constructor BODY, not
        // those. If the freeze is bigger than what sw1/sw2 below report, it
        // happened in one of those field initializers instead — check those
        // constructors next.
        // First launch (no settings.json yet) — seed Language/Theme from the
        // OS instead of the hardcoded AppSettings record defaults ("en" /
        // "Dark"). SettingsService.Load(...) only applies the override to
        // the in-memory Current when IsFirstRun is true; nothing is written
        // to disk until the user actually changes a setting (see
        // SettingsService.Save), so this never overwrites a value the user
        // picked on a previous run.
        Settings.Load(DetectSystemLanguage, DetectSystemIsDark);

        Loc.Load(Settings.Current.Language);
        Host = Settings.Current.LastHost;
        bool isDark = Settings.Current.Theme != "Light";
        if (Avalonia.Application.Current is not null)
            Avalonia.Application.Current.RequestedThemeVariant =
                isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        IsDarkTheme = isDark;
        UiScale = Settings.Current.UiScale;

        // mDNS discovery
        // Makaretu.Dns raises ServiceInstanceDiscovered/Shutdown from its own
        // internal listener thread, not the UI thread — must marshal here,
        // same as the USB subscriptions below. Without this, touching the
        // ObservableCollection (and TryAutoConnect/ConnectAsync afterwards)
        // was silently failing off the UI thread: an async void handler
        // swallows the exception, so nothing ever showed up in logs, but
        // auto-connect never actually ran past DiscoveredDevices.Add().
        _mdns.DeviceFound += d => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!DiscoveredDevices.Any(x => x.IpAddress == d.IpAddress))
                DiscoveredDevices.Add(d);

            TryAutoConnect(d);
        });
        _mdns.DeviceLost += name => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var item = DiscoveredDevices.FirstOrDefault(x => x.Name == name);
            if (item is not null) DiscoveredDevices.Remove(item);
        });
        _mdns.ManualScanCompleted += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => IsScanning = false);
        _mdns.RequeryStarted += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => AutoDiscoveryPulsed?.Invoke());

        // Start() now begins real mDNS listening (Makaretu.Dns) — devices
        // already on the network announce themselves and show up on their
        // own, no explicit query needed. ScanMdns() below just sends an
        // extra query burst as a nudge for anything that missed the window
        // before we started listening; the scan-button UX is unchanged.
        _mdns.Start();
        IsAutoDiscovering = true; // background listening is now live — drives the auto-discover-pulse animation
        ScanMdns();

        // USB discovery — same DiscoveredDevices list as mDNS for now (UI/UX
        // separation between USB and Wi-Fi devices can come later).
        _usb.DeviceFound += d => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!DiscoveredDevices.Any(x => x.IpAddress == d.IpAddress))
                DiscoveredDevices.Add(d);

            TryAutoConnect(d);
        });
        _usb.DeviceLost += name => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var item = DiscoveredDevices.FirstOrDefault(x => x.Name == name);
            if (item is not null) DiscoveredDevices.Remove(item);

            // Cable pulled (or app closed) while an auto-connect grace-period
            // timer was still waiting on the preferred transport for this
            // device — cancel it so ConnectAsync() never fires against a
            // device that's already gone.
            if (_pendingAutoConnects.TryGetValue(name, out var pendingCts))
            {
                _pendingAutoConnects.Remove(name);
                pendingCts.Cancel();
            }
        });
        // adb missing / server won't start — Warning, once per session via
        // banner dedup. Must subscribe BEFORE Start() below, which is what
        // actually fires the event. Only fires when the bundled adb genuinely
        // can't run.
        _usb.ServerStartFailed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            ShowNotice(Loc["notice.usb_failed"], NoticeKind.Warning, Material.Icons.MaterialIconKind.Usb));
        _usb.Start();

        // Command channel events — all marshaled to UI thread
        _cmd.DeviceInfoReceived   += info   => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnDeviceInfo(info));
        _cmd.BatteryReceived      += bat    => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnBattery(bat));
        _cmd.StreamStatusReceived += status => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnStreamStatus(status));
        _cmd.NetworkStatusReceived += net   => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnNetworkStatus(net));
        _cmd.Disconnected         += ()     => Avalonia.Threading.Dispatcher.UIThread.Post(OnDisconnected);
        _cmd.RttReceived          += rtt    => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            Rtt = (_rtt < 0) ? rtt : (Rtt * 4 + rtt) / 5);
        _cmd.ConnectionHealthChanged += health => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ConnectionHealth = health;
            // Stale is transitional (watchdog flaps Live↔Stale) — banner dedup
            // in ShowNotice keeps flapping from spamming; only the first
            // transition in a window actually replays.
            if (health == ConnectionHealth.Stale)
                ShowNotice(Loc["notice.unstable"], NoticeKind.Warning, Material.Icons.MaterialIconKind.WifiStrengthAlertOutline);
        });

        // MJPEG: pass raw JPEG to UI — UI decodes once and forwards BGRA to VCam
        _video.JpegFrameReceived += bytes =>
        {
            JpegFrameReceived?.Invoke(bytes);
        };
        _video.JpegFrameReceivedWithTimestamp += (bytes, ticks) =>
            JpegFrameReceivedWithTimestamp?.Invoke(bytes, ticks);

        // H.264/H.265: push NAL units into the matching decoder — timestamp variant
        // carries the network-arrival tick count through so decode latency can be
        // measured. Only one of the two decoders is ever Start()ed at a time (see
        // StartVideo), so routing by CurrentCodec here is equivalent to "push to
        // whichever one is actually running."
        _video.NalUnitReceivedWithTimestamp += (nal, ticks) =>
        {
            if (CurrentCodec == VideoCodec.H265)
                _h265Decoder.PushNal(nal, ticks);
            else
                _h264Decoder.PushNal(nal, ticks);
        };
        _video.ThroughputUpdated += mbps => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            NetworkSpeedMbps = mbps);

        // Diagnostic: network-arrival -> decoded-frame-ready latency (queue + decode
        // + hwframe transfer + scale). Sampled on every frame, but only the 1s
        // average is shown (see SetupLatencyTimer) — same throttling pattern as
        // the view's FPS counter, so the UI number doesn't jitter per-frame.
        // Wired to both decoders — only one is ever running at a time, so this
        // is equivalent to wiring "whichever decoder is active" without needing
        // to re-wire on every codec switch.
        _h264Decoder.DecodeLatencyMeasured += ms =>
        {
            lock (_latencyLock)
            {
                _latencySumMs += ms;
                _latencySampleCount++;
            }
        };
        _h265Decoder.DecodeLatencyMeasured += ms =>
        {
            lock (_latencyLock)
            {
                _latencySumMs += ms;
                _latencySampleCount++;
            }
        };

        // Decoder outputs BGRA frames → UI + VCam
        _h264Decoder.BgraFrameReceived += (bgra, w, h) =>
        {
            BgraFrameReceived?.Invoke(bgra, w, h, Rotation);
            if (IsVCamActive)
                VCam.PushFrame(bgra, w, h, Rotation);
        };
        _h265Decoder.BgraFrameReceived += (bgra, w, h) =>
        {
            BgraFrameReceived?.Invoke(bgra, w, h, Rotation);
            //Console.WriteLine($"[DEBUG] BGRA frame {w}x{h}, IsVCamActive={IsVCamActive}");
            if (IsVCamActive)
                VCam.PushFrame(bgra, w, h, Rotation);
        };

        // Decoder init failures (e.g. avcodec_open2 fails for H.264/H.265) —
        // only the active codec's decoder ever runs, so this can't double-fire.
        // Per-frame decode hiccups stay silent on purpose (would spam).
        _h264Decoder.DecodeFailed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            ShowNotice(Loc["notice.decoder_failed"], NoticeKind.Error, Material.Icons.MaterialIconKind.Monitor));
        _h265Decoder.DecodeFailed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            ShowNotice(Loc["notice.decoder_failed"], NoticeKind.Error, Material.Icons.MaterialIconKind.Monitor));

        SetupLatencyTimer();
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── First-run OS-default detection ──────────────────────────────────────
    // Both are best-effort: any failure (no PlatformSettings available yet,
    // e.g. headless/test contexts) just falls back to the same hardcoded
    // defaults AppSettings already had ("en" / dark), so first-run behavior
    // never throws or regresses to something worse than before this existed.

    // Only "en"/"uk" are supported today (see LocalizationService) — anything
    // else (system in French, German, etc.) falls back to "en" rather than
    // silently loading a language file that doesn't exist.
    private static string DetectSystemLanguage()
    {
        try
        {
            string twoLetter = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return twoLetter.Equals("uk", StringComparison.OrdinalIgnoreCase) ? "uk" : "en";
        }
        catch
        {
            return "en";
        }
    }

    // Reads the Windows system theme via Avalonia's PlatformSettings, the
    // same API FluentTheme itself uses to follow the OS (see
    // Avalonia.Platform.PlatformThemeVariant). Falls back to dark — the
    // original hardcoded default — if PlatformSettings isn't available.
    private static bool DetectSystemIsDark()
    {
        try
        {
            var platformSettings = Avalonia.Application.Current?.PlatformSettings;
            var colors = platformSettings?.GetColorValues();
            if (colors is null) return true;
            return colors.ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;
        }
        catch
        {
            return true;
        }
    }

    public void Dispose()
    {
        DisposeNotice();
        _mdns.Dispose();
        _usb.Dispose();
        _cmd.Dispose();
        _video.Dispose();
        _h264Decoder.Dispose();
        _h265Decoder.Dispose();
        _latencyTimer?.Dispose();
        _streamElapsedTimer?.Dispose();
        VCam.Dispose();
    }
}