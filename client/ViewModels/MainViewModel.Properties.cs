using Material.Icons;
using DroidLens.Client.Models;
using DroidLens.Client.Network;

namespace DroidLens.Client.ViewModels;

public partial class MainViewModel
{
    // ── Observable properties ──────────────────────────────────────────────────

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { _isConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsAutoConnectEnabled)); }
    }

    // Drives the scan-icon-button's spin animation — set on ScanMdns(), cleared
    // when MdnsDiscovery reports the manual scan finished (ManualScanCompleted).
    // Manual-button-only; do not use this for any passive/background indicator.
    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set { _isScanning = value; OnPropertyChanged(); }
    }

    // Drives the AutoTabButton Wifi icon's passive "breathe" animation — true
    // whenever mDNS is listening in the background (i.e. not yet connected to
    // a device), independent of IsScanning. Unlike IsScanning this does NOT
    // toggle off after a single scan burst; it stays true for as long as
    // discovery is passively running, since there's no discrete "done" moment
    // for background listening the way there is for a manual scan.
    private bool _isAutoDiscovering;
    public bool IsAutoDiscovering
    {
        get => _isAutoDiscovering;
        private set { _isAutoDiscovering = value; OnPropertyChanged(); }
    }

    // Fires on each actual background mDNS requery tick (see MdnsDiscovery.
    // RequeryStarted) — lets the view play a one-shot pulse animation on the
    // Wifi icon synced to real background activity, rather than running a
    // continuous animation for the entire IsAutoDiscovering=true duration.
    public event Action? AutoDiscoveryPulsed;

    private ConnectionHealth _connectionHealth = ConnectionHealth.Disconnected;
    public ConnectionHealth ConnectionHealth
    {
        get => _connectionHealth;
        private set { _connectionHealth = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    private string _host = "192.168.1.100";
    public string Host
    {
        get => _host;
        set { _host = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsUsbConnection)); }
    }

    // True when the active connection is via the USB adb forward (always
    // 127.0.0.1 — see UsbDiscovery). Used to swap the Wi-Fi signal indicator
    // for a static USB icon, since RSSI/signal level is meaningless over USB.
    public bool IsUsbConnection => Host == "127.0.0.1";

    private string _deviceModel = "—";
    public string DeviceModel
    {
        get => _deviceModel;
        private set { _deviceModel = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAutoConnectEnabled)); }
    }

    // ── Auto-connect ─────────────────────────────────────────────────────────
    // Key used to remember "auto-connect this device" across rediscovery and
    // across app restarts. The phone currently only ever reports its model
    // name over Wi-Fi (see CommandServer.sendDeviceInfo — no serial/UUID),
    // so that's what we key on. Two identical phone models on the same
    // network would be treated as the same entry — acceptable for the
    // common single-personal-device case; revisit if the Android side ever
    // starts sending a stable per-device UUID.
    private string DeviceAutoConnectKey => DeviceModel;

    // True when a Disconnect() was initiated by the user clicking the
    // Disconnect button, as opposed to the connection dropping on its own
    // (network loss, phone app killed, etc). Auto-connect must NOT fire
    // after a manual disconnect — otherwise clicking "Disconnect" while the
    // phone is still reachable would just reconnect a moment later, which
    // would make the button appear broken. Cleared on the next explicit
    // ConnectAsync() so auto-connect resumes protecting future drops.
    private bool _manualDisconnect;

    // Bindable for the toggle button in the Connected-state panel. Reflects
    // whether auto-connect is currently enabled for whichever device we're
    // connected to right now.
    public bool IsAutoConnectEnabled => Settings.IsAutoConnectEnabled(DeviceAutoConnectKey);

    public void ToggleAutoConnectForCurrentDevice()
    {
        if (string.IsNullOrWhiteSpace(DeviceAutoConnectKey) || DeviceAutoConnectKey == "—")
            return; // no device info yet — nothing to key the setting on

        bool newValue = !Settings.IsAutoConnectEnabled(DeviceAutoConnectKey);
        // Record whichever transport is active right now — that's the
        // signal TryAutoConnect later uses to decide which of USB/Wi-Fi
        // should win when both are available on rediscovery.
        Settings.SetAutoConnectEnabled(DeviceAutoConnectKey, newValue, isUsb: IsUsbConnection);
        OnPropertyChanged(nameof(IsAutoConnectEnabled));
    }

    // ── Stream-behavior settings (SettingsWindow) ───────────────────────────────
    // Thin notify-wrappers over Settings.Current's two flags, so SettingsWindow
    // can bind directly to these (color, thumb position, and persistence all
    // follow from one property assignment) instead of keeping its own local
    // copy and syncing switch color by hand on every click.
    public bool StopStreamOnDisconnect
    {
        get => Settings.Current.StopStreamOnDisconnect;
        set
        {
            if (value == Settings.Current.StopStreamOnDisconnect) return;
            Settings.UpdateStopStreamOnDisconnect(value);
            OnPropertyChanged();
        }
    }

    public bool StopStreamOnAppClose
    {
        get => Settings.Current.StopStreamOnAppClose;
        set
        {
            if (value == Settings.Current.StopStreamOnAppClose) return;
            Settings.UpdateStopStreamOnAppClose(value);
            OnPropertyChanged();
        }
    }

    // ── Preview FPS cap (Interface section, not Stream behavior — this only
    // affects how PreviewArea renders locally, not the phone's stream or
    // the virtual camera; see PreviewArea.axaml.cs's _skipNextMjpegPreviewFrame
    // / _skipNextBgraPreviewFrame gates, which check this flag). ─────────────
    public bool LimitPreviewFps
    {
        get => Settings.Current.LimitPreviewFps;
        set
        {
            if (value == Settings.Current.LimitPreviewFps) return;
            Settings.UpdateLimitPreviewFps(value);
            OnPropertyChanged();
        }
    }

    // ── Ambient light fill (Interface section) — soft color bars behind the
    // letterbox gap on portrait streams; see PreviewArea.axaml.cs's
    // ShouldUpdateAmbientFill, which checks this flag before sampling or
    // animating anything. ─────────────────────────────────────────────────
    public bool AmbientFillEnabled
    {
        get => Settings.Current.AmbientFillEnabled;
        set
        {
            if (value == Settings.Current.AmbientFillEnabled) return;
            Settings.UpdateAmbientFillEnabled(value);
            OnPropertyChanged();
        }
    }

    // ── Screenshot save location ─────────────────────────────────────────────
    // Empty string in the getter (never null) so SettingsPanel's plain TextBox
    // binding never has to null-guard. Setting an empty/whitespace value clears
    // back to the default rather than persisting a blank path — same "null
    // means default" contract as Settings.Current.ScreenshotPath itself.
    public string ScreenshotPath
    {
        get => Settings.Current.ScreenshotPath ?? DefaultScreenshotFolder;
        set
        {
            string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (normalized == Settings.Current.ScreenshotPath) return;
            Settings.UpdateScreenshotPath(normalized);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomScreenshotPath));
        }
    }

    // True once the user has actually picked/typed a folder of their own —
    // i.e. Settings.Current.ScreenshotPath is no longer null. Drives the
    // "reset to default" action in SettingsPanel: showing it when the field
    // is still just displaying DefaultScreenshotFolder would offer a reset
    // that has nothing to reset.
    public bool HasCustomScreenshotPath => Settings.Current.ScreenshotPath is not null;

    // Clears back to DefaultScreenshotFolder — same "set null" path the
    // ScreenshotPath setter above already takes for an empty/whitespace
    // value, exposed here so SettingsPanel's reset button doesn't need to
    // know that contract itself (it just calls this).
    public void ResetScreenshotPath()
    {
        if (Settings.Current.ScreenshotPath is null) return;
        Settings.UpdateScreenshotPath(null);
        OnPropertyChanged(nameof(ScreenshotPath));
        OnPropertyChanged(nameof(HasCustomScreenshotPath));
    }

    // Pictures\DroidLens — chosen over %LOCALAPPDATA% (plan's original fallback
    // suggestion) because it's where people actually expect to find screenshots
    // (Snipping Tool, Steam, etc. all default here too), and over the app's own
    // install directory (the plan's original default) because that can be
    // Program Files, which silently fails to write without admin rights.
    public static string DefaultScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DroidLens");

    private string _androidVersion = "";
    public string AndroidVersion
    {
        get => _androidVersion;
        private set { _androidVersion = value; OnPropertyChanged(); }
    }

    private int _batteryLevel = -1;
    public int BatteryLevel
    {
        get => _batteryLevel;
        private set { _batteryLevel = value; OnPropertyChanged(); OnPropertyChanged(nameof(BatteryText)); }
    }

    private bool _batteryCharging;
    public bool BatteryCharging
    {
        get => _batteryCharging;
        private set { _batteryCharging = value; OnPropertyChanged(); OnPropertyChanged(nameof(BatteryText)); }
    }

    private double _batteryTemp;
    public double BatteryTemp
    {
        get => _batteryTemp;
        private set { _batteryTemp = value; OnPropertyChanged(); OnPropertyChanged(nameof(BatteryText)); }
    }

    private bool _isStreamStarting;
    public bool IsStreamStarting
    {
        get => _isStreamStarting;
        private set
        {
            if (_isStreamStarting == value) return;
            _isStreamStarting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StreamButtonText));
            OnPropertyChanged(nameof(IsStreamBusy));
        }
    }

    private bool _isStreamStopping;
    public bool IsStreamStopping
    {
        get => _isStreamStopping;
        private set
        {
            if (_isStreamStopping == value) return;
            _isStreamStopping = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStreamBusy));
        }
    }

    // Either handshake in flight (startup OR teardown) — drives the button's
    // btn-pulse busy animation, so the breathing/loading state reads on both
    // Start and Stop instead of only while the stream is spinning up.
    public bool IsStreamBusy => _isStreamStarting || _isStreamStopping;

    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            bool wasStreaming = _isStreaming;
            _isStreaming = value;
            if (value) _isStreamStarting = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStreamStarting));
            OnPropertyChanged(nameof(StreamStatusText));
            OnPropertyChanged(nameof(StreamButtonText));

            // Local-only timer: starts counting the moment the PC itself confirms
            // an active stream (not synced against the phone's clock — see
            // StartStreamElapsedTimer for rationale), resets on stop.
            if (value && !wasStreaming)
                StartStreamElapsedTimer();
            else if (!value && wasStreaming)
                StopStreamElapsedTimer();
        }
    }

    private int _streamElapsedSeconds;
    public int StreamElapsedSeconds
    {
        get => _streamElapsedSeconds;
        private set { _streamElapsedSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(StreamElapsedText)); }
    }

    public string StreamElapsedText => TimeSpan.FromSeconds(_streamElapsedSeconds).ToString(@"mm\:ss");

    private int _rotation;
    public int Rotation
    {
        get => _rotation;
        private set { _rotation = value; OnPropertyChanged(); OnPropertyChanged(nameof(OrientationText)); }
    }

    private VideoCodec _currentCodec = VideoCodec.MJPEG;
    public VideoCodec CurrentCodec
    {
        get => _currentCodec;
        private set { _currentCodec = value; OnPropertyChanged(); }
    }

    private StreamQuality _currentQuality = StreamQuality.MEDIUM;
    public StreamQuality CurrentQuality
    {
        get => _currentQuality;
        private set { _currentQuality = value; OnPropertyChanged(); OnPropertyChanged(nameof(QualityDisplayText)); }
    }

    // Selectable only from the PC client — Android has no on-device UI for it,
    // it just persists whatever was last set (see StreamViewModel.Resolution).
    private StreamResolution _currentResolution = StreamResolution.HD;
    public StreamResolution CurrentResolution
    {
        get => _currentResolution;
        private set { _currentResolution = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResolutionDisplayText)); OnPropertyChanged(nameof(ResolutionIconKind)); }
    }

    private bool _isFrontCamera;
    public bool IsFrontCamera
    {
        get => _isFrontCamera;
        private set { _isFrontCamera = value; OnPropertyChanged(); }
    }

    private bool _isFlashlightOn;
    public bool IsFlashlightOn
    {
        get => _isFlashlightOn;
        private set { _isFlashlightOn = value; OnPropertyChanged(); }
    }

    private bool _isBlackScreen;
    public bool IsBlackScreen
    {
        get => _isBlackScreen;
        private set { _isBlackScreen = value; OnPropertyChanged(); }
    }

    private double _decodeLatencyMs = -1;
    public double DecodeLatencyMs
    {
        get => _decodeLatencyMs;
        private set { _decodeLatencyMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(DecodeLatencyText)); }
    }
    private int _rtt = -1;
    public int Rtt
    {
        get => _rtt;
        private set { _rtt = value; OnPropertyChanged(); OnPropertyChanged(nameof(RttText)); }
    }

    private int _networkSignalLevel;
    public int NetworkSignalLevel
    {
        get => _networkSignalLevel;
        private set { _networkSignalLevel = value; OnPropertyChanged(); }
    }

    private double _networkSpeedMbps = -1;
    public double NetworkSpeedMbps
    {
        get => _networkSpeedMbps;
        private set { _networkSpeedMbps = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetworkSpeedText)); }
    }

    private string _networkIp = "";
    public string NetworkIp
    {
        get => _networkIp;
        private set { _networkIp = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetworkIpText)); }
    }

    private bool _isDarkTheme = true;
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        private set { _isDarkTheme = value; OnPropertyChanged(); }
    }

    private double _uiScale = 1.0;
    public double UiScale
    {
        get => _uiScale;
        set
        {
            if (Math.Abs(_uiScale - value) > 0.001)
            {
                _uiScale = value;
                OnPropertyChanged();
            }
        }
    }

    // ── Derived strings ────────────────────────────────────────────────────────
    public string StatusText => ConnectionHealth switch
    {
        ConnectionHealth.Live       => $"● {Host}",
        ConnectionHealth.Stale      => $"● {Host} ({Loc["label.unstable"]})",
        _                           => Loc["label.notconnected"]
    };
    public string BatteryText      => BatteryLevel < 0 ? "—" : $"{BatteryLevel}%  {BatteryTemp:F1}°C";
    public string StreamStatusText => IsStreaming ? Loc["label.live"] : Loc["label.idle"];
    public string StreamButtonText => IsStreaming ? Loc["btn.stop"] : (IsStreamStarting ? Loc["btn.starting"] : Loc["btn.start"]);
    public string OrientationText  => Rotation == 0 ? Loc["label.landscape"] : Loc["label.reverse_landscape"];
    public string QualityDisplayText => CurrentQuality switch
    {
        StreamQuality.LOW    => Loc["quality.low"],
        StreamQuality.MEDIUM => Loc["quality.medium"],
        StreamQuality.HIGH   => Loc["quality.high"],
        _                    => CurrentQuality.ToString()
    };
    // Short labels — this is a compact cycle-button, not a dropdown row, so
    // it deliberately doesn't route through Loc (no per-language "480p").
    public string ResolutionDisplayText => CurrentResolution switch
    {
        StreamResolution.SD  => "480p",
        StreamResolution.HD  => "720p",
        StreamResolution.FHD => "1080p",
        _                    => CurrentResolution.ToString()
    };
    // MDI has no dedicated hd/fullhd glyphs — QualityLow/Medium/High is the
    // closest semantic triad (SD/HD/FHD "quality tiers"), so it's reused here
    // rather than reaching for the unrelated Sd (sd-card) icon.
    public MaterialIconKind ResolutionIconKind => CurrentResolution switch
    {
        StreamResolution.SD  => MaterialIconKind.QualityLow,
        StreamResolution.HD  => MaterialIconKind.QualityMedium,
        StreamResolution.FHD => MaterialIconKind.QualityHigh,
        _                    => MaterialIconKind.QualityMedium
    };
    public string RttText          => Rtt < 0 ? "—" : $"{Rtt} ms";
    public string DecodeLatencyText => DecodeLatencyMs < 0 ? "—" : $"{DecodeLatencyMs:F0} ms";
    public string NetworkSpeedText => NetworkSpeedMbps < 0 ? "—" : $"{NetworkSpeedMbps:F1} Mbps";
    public string NetworkIpText    => string.IsNullOrEmpty(NetworkIp) ? "—" : NetworkIp;
}