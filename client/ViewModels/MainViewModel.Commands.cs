using DroidLens.Client.Models;
using DroidLens.Client.Network;
using DroidLens.Client.Services;
using DroidLens.Client.VirtualCamera;
using Material.Icons;

namespace DroidLens.Client.ViewModels;

public partial class MainViewModel
{
    // ── Stream elapsed-time timer ───────────────────────────────────────────────
    // Deliberately NOT synced against the phone's clock or a start-timestamp sent
    // over the wire: the two devices' clocks aren't guaranteed to agree (this is
    // exactly the skew the NTP-style offset measurement for latency deals with),
    // and for a UI "how long has this been running" badge that precision isn't
    // worth the complexity. Instead we just mark the moment *this* client sees
    // the stream go active and count locally from there — always internally
    // consistent, never drifts against itself.
    private void StartStreamElapsedTimer()
    {
        _streamStartUtc = DateTime.UtcNow;
        StreamElapsedSeconds = 0;

        _streamElapsedTimer?.Dispose();
        _streamElapsedTimer = new System.Threading.Timer(_ =>
        {
            var seconds = (int)(DateTime.UtcNow - _streamStartUtc).TotalSeconds;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StreamElapsedSeconds = seconds);
        }, null, 1000, 1000);
    }

    private void StopStreamElapsedTimer()
    {
        _streamElapsedTimer?.Dispose();
        _streamElapsedTimer = null;
        StreamElapsedSeconds = 0;
    }

    // ── Decode-latency averaging ────────────────────────────────────────────────
    // Same 1s-window approach as the view's FPS counter: accumulate samples as
    // they arrive, then once a second compute the average and reset. Keeps the
    // displayed number stable instead of jumping around on every single frame.
    private void SetupLatencyTimer()
    {
        _latencyTimer = new System.Threading.Timer(_ =>
        {
            double avgMs;
            lock (_latencyLock)
            {
                avgMs = _latencySampleCount > 0 ? _latencySumMs / _latencySampleCount : -1;
                _latencySumMs = 0;
                _latencySampleCount = 0;
            }
            if (avgMs >= 0)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => DecodeLatencyMs = avgMs);
        }, null, 1000, 1000);
    }

    /// <summary>
    /// Called by the view once it's finished decoding an MJPEG frame (JPEG -> BGRA),
    /// with the tick count captured when that frame's bytes arrived over the network
    /// (from JpegFrameReceivedWithTimestamp). Feeds the same 1s-averaged latency
    /// display as the H.264/H.265 path — mirrors _h264Decoder/_h265Decoder's
    /// DecodeLatencyMeasured, just fed from the view instead of from VideoDecoder,
    /// since MJPEG decode happens there.
    /// </summary>
    public void ReportMjpegDecodeLatency(long arrivalTicks)
    {
        double latencyMs = new TimeSpan(DateTime.UtcNow.Ticks - arrivalTicks).TotalMilliseconds;
        lock (_latencyLock)
        {
            _latencySumMs += latencyMs;
            _latencySampleCount++;
        }
    }

    // ── Public commands ────────────────────────────────────────────────────────

    public void ScanMdns()
    {
        IsScanning = true;
        _mdns.Refresh();
    }

    // Called whenever mDNS or USB discovery reports a device — decides
    // whether this "device came online" event should trigger an automatic
    // connection.
    //
    // Whichever transport the user was actually connected over when they
    // enabled auto-connect for this device (Settings.PrefersUsbAutoConnect)
    // is preferred on rediscovery. mDNS and USB discovery fire independently
    // and in whatever order the OS/network happens to deliver them, so a
    // bare "connect to whichever DiscoveredDevice arrives first" would just
    // as often connect over the non-preferred transport and never even try
    // the preferred one.
    //
    // Instead: a hit on the non-preferred transport for a device with
    // auto-connect enabled doesn't connect immediately — it starts a short
    // grace-period timer keyed by device model, giving the preferred
    // transport a window to report the same phone. If the preferred
    // transport wins the race, its own TryAutoConnect call cancels the
    // pending timer and connects immediately. If the grace period elapses
    // with nothing on the preferred transport (phone on a different
    // network, cable unplugged, etc.), the non-preferred transport is used
    // as the fallback.
    private static readonly TimeSpan AutoConnectGracePeriod = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, CancellationTokenSource> _pendingAutoConnects = new();

    // Guards against exactly the scenario a manual Disconnect() creates: the
    // phone is still on the network and still answers scans, so without
    // _manualDisconnect this would silently reconnect the user right after
    // they chose to disconnect.
    private async void TryAutoConnect(DiscoveredDevice device)
    {
        if (IsConnected) return;               // already busy with a device
        if (_manualDisconnect) return;          // user explicitly disconnected — don't auto-rejoin

        // Device model is the same regardless of transport, so USB and Wi-Fi
        // sightings of the same phone share one auto-connect key (see
        // DeviceAutoConnectKey/UsbDiscovery.ResolveDisplayName — Name no
        // longer carries a "(USB)" suffix, so no stripping needed here).
        string key = device.Name;

        if (!Settings.IsAutoConnectEnabled(key)) return;

        bool prefersUsb = Settings.PrefersUsbAutoConnect(key);
        bool isPreferredTransport = device.IsUsb == prefersUsb;

        if (!isPreferredTransport)
        {
            // This sighting is on the non-preferred transport — a sighting
            // on the preferred one already has (or will get) its own
            // timer/attempt racing for this key, so don't let this one jump
            // the queue if one's already pending, and don't fire yet in case
            // the preferred transport is still on its way in.
            if (_pendingAutoConnects.ContainsKey(key)) return;

            var cts = new CancellationTokenSource();
            _pendingAutoConnects[key] = cts;
            try
            {
                await Task.Delay(AutoConnectGracePeriod, cts.Token);
            }
            catch (TaskCanceledException)
            {
                return; // preferred transport won the race — its own TryAutoConnect call handles connecting
            }
            finally
            {
                _pendingAutoConnects.Remove(key);
            }

            // Re-check after the wait: user may have connected manually,
            // disconnected, or another attempt may have started in the meantime.
            if (IsConnected || _manualDisconnect) return;
        }
        else
        {
            // This sighting is on the preferred transport — if a grace-period
            // timer is waiting on this same key for the non-preferred one,
            // cancel it so the preferred transport wins and the other one
            // never fires.
            if (_pendingAutoConnects.TryGetValue(key, out var pendingCts))
            {
                _pendingAutoConnects.Remove(key);
                pendingCts.Cancel();
            }
        }

        Host = device.IpAddress;
        await ConnectAsync(silent: true);
    }

    public void ToggleTheme()
    {
        bool newIsDark = !IsDarkTheme;
        var variant = newIsDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        if (Avalonia.Application.Current is not null)
            Avalonia.Application.Current.RequestedThemeVariant = variant;

        IsDarkTheme = newIsDark;
        Settings.UpdateTheme(IsDarkTheme ? "Dark" : "Light");
    }

    public void SetLanguage(string lang)
    {
        Loc.Load(lang); // Loc now raises its own "Item[]" PropertyChanged (see
                         // LocalizationService.Load), which refreshes every
                         // "{Binding Loc[...]}" indexer binding directly —
                         // including ones inside derived properties like
                         // StatusText/StreamStatusText/StreamButtonText/
                         // OrientationText, since those re-evaluate whenever
                         // whatever they depend on changes. No need to
                         // enumerate them here anymore.
        Settings.UpdateLanguage(lang);
        OnPropertyChanged(string.Empty); // still needed for the few view
                                              // bindings that go through the VM
                                             // directly rather than through Loc
    }

    public void SetUiScale(double scale)
    {
        if (scale < 0.5 || scale > 2.0) return;
        UiScale = scale;
        Settings.UpdateUiScale(scale);
    }

    public bool ToggleVirtualCamera(bool showNotice = true)
    {
        if (IsVCamActive)
        {
            VCam.Stop();
            IsVCamActive = false;
        }
        else
        {
            AppLog.I("VCam", () => $"IsInstalled={VirtualCameraInstaller.IsInstalled()}");
            AppLog.I("VCam", () => $"IsAvailable={VCam.IsAvailable}");
            if (!VirtualCameraInstaller.IsInstalled())
            {
                AppLog.W("VCam", () => "Not installed — aborting.");
                if (showNotice)
                    ShowNotice(Loc["notice.vcam_not_installed"], NoticeKind.Warning, MaterialIconKind.Webcam);
                return false;
            }
            IsVCamActive = VCam.Start();
            AppLog.I("VCam", () => $"IsVCamActive={IsVCamActive}");
            if (!IsVCamActive && showNotice)
                ShowNotice(Loc["notice.vcam_failed"], NoticeKind.Error, MaterialIconKind.Webcam);
        }
        return IsVCamActive;
    }

    public async Task ConnectAsync(bool silent = false)
    {
        // Any explicit connection attempt (button click, tapping a discovered
        // device, or an auto-connect firing) re-arms auto-connect for future
        // drops — only Disconnect() (the manual button) should suppress it.
        _manualDisconnect = false;

        Settings.UpdateHost(Host);
        bool ok = await _cmd.ConnectAsync(Host);
        IsConnected = ok;
        ConnectionHealth = ok ? ConnectionHealth.Live : ConnectionHealth.Disconnected;
        if (!ok)
        {
            // Background auto-connect retries stay silent — only an explicit
            // user attempt gets the banner (see TryAutoConnect passing silent).
            if (!silent)
                ShowNotice($"{Loc["notice.connect_failed"]} — {Host}", NoticeKind.Error, MaterialIconKind.WifiOff);
            return;
        }
            // No point continuing to scan the subnet / poll USB once we're
            // already connected to a device — stops both discovery services
            // until Disconnect() brings them back. Note: _usb.Stop() only
            // pauses polling here — it must NOT release the adb port forwards,
            // since StartVideo() below is about to open new TCP connections
            // to 7878/7880 through those exact forwards for a USB device.
            _mdns.Stop();
            _usb.Stop();
            IsScanning = false;
            IsAutoDiscovering = false; // stop breathing — no longer listening in the background

            var codec = Enum.TryParse<VideoCodec>(Settings.Current.LastCodec, out var c)
                ? c : VideoCodec.MJPEG;
            StartVideo(codec);
    }

    public async Task DisconnectAsync()
    {
        // If a stream is live, tell the phone to stop it while the command
        // socket is still open — this only works here (deliberate teardown),
        // not from OnDisconnected(), since by the time a health-watchdog
        // drop fires the socket is already gone and there's no one left to
        // send "stop" to.
        if (IsStreaming && ConnectionHealth == ConnectionHealth.Live &&
            Settings.Current.StopStreamOnDisconnect)
        {
            try { await _cmd.StopStreamAsync(); }
            catch { /* best-effort — fall through to DoDisconnect() regardless */ }
        }
        DoDisconnect();
    }

    // Kept as a synchronous entry point for any call site that can't await
    // (e.g. Dispose paths) — just skips the stop-stream step.
    public void Disconnect() => DoDisconnect();

    private void DoDisconnect()
    {
        IsStreamStarting  = false;
        IsStreamStopping  = false;
        // Marks this as a user-initiated disconnect so TryAutoConnect refuses
        // to silently reconnect the instant the still-reachable phone shows
        // up again in the next scan. Auto-connect resumes protecting the
        // device the next time the user connects explicitly (see
        // ConnectAsync, which clears this flag).
        _manualDisconnect = true;

        _cmd.Disconnect();
        _video.Stop();
        _h264Decoder.Stop();
        _h265Decoder.Stop();
        IsConnected = false;
        ConnectionHealth = ConnectionHealth.Disconnected;

        // Resume looking for devices now that we're back in the idle state.
        // StopAndRelease (rather than plain Stop) clears any forwards left
        // over from the just-ended session so USB discovery starts clean.
        _usb.StopAndRelease();
        _usb.Start();
        IsAutoDiscovering = true; // resume breathing — back to passively listening
        ScanMdns();
    }

    public async Task SetCodecAsync(VideoCodec codec)
    {
        CurrentCodec = codec;
        Settings.UpdateCodec(codec.ToString());
        StartVideo(codec);
        await _cmd.SetCodecAsync(codec.ToString());
    }

    public async Task SetQualityAsync(StreamQuality q)
    {
        Settings.UpdateQuality(q.ToString());
        await _cmd.SetQualityAsync(q.ToString());
    }

    public async Task SetResolutionAsync(StreamResolution r)
    {
        CurrentResolution = r;
        Settings.UpdateResolution(r.ToString());
        await _cmd.SetResolutionAsync(r.ToString());
    }

    public Task FlipCameraAsync()
    {
        IsFrontCamera = !IsFrontCamera;
        return _cmd.FlipCameraAsync();
    }

    public Task ToggleBlackScreenAsync()
    {
        if (ConnectionHealth != ConnectionHealth.Live) return Task.CompletedTask;
        IsBlackScreen = !IsBlackScreen;
        return _cmd.ToggleBlackScreenAsync();
    }

    public Task ToggleFlashlightAsync()
    {
        if (ConnectionHealth != ConnectionHealth.Live) return Task.CompletedTask;
        IsFlashlightOn = !IsFlashlightOn;
        return _cmd.ToggleFlashlightAsync();
    }

    public async Task StartStreamAsync()
    {
        IsStreamStopping = false;
        IsStreamStarting = true;
        try
        {
            await _cmd.StartStreamAsync();
        }
        catch
        {
            IsStreamStarting = false;
            ShowNotice(Loc["notice.stream_start_failed"], NoticeKind.Error, MaterialIconKind.Play);
            throw;
        }
    }

    public async Task StopStreamAsync()
    {
        // Mirrors StartStreamAsync: keep the button in the busy/animated state
        // for the whole stop handshake (command sent -> phone ACK -> OnStreamStatus
        // confirms Active=false), then clear it so the button settles back to primary.
        IsStreamStarting = false;
        IsStreamStopping = true;
        try
        {
            await _cmd.StopStreamAsync();
        }
        catch
        {
            IsStreamStopping = false;
            ShowNotice(Loc["notice.stream_stop_failed"], NoticeKind.Error, MaterialIconKind.Stop);
            throw;
        }
    }

    public Task RotateAsync()
    {
        if (ConnectionHealth != ConnectionHealth.Live) return Task.CompletedTask;
        return _cmd.RotateAsync();
    }

    // ── Private ────────────────────────────────────────────────────────────────

    private void StartVideo(VideoCodec codec)
    {
        _video.Stop();
        _h264Decoder.Stop();
        _h265Decoder.Stop();
        CurrentCodec = codec;

        if (codec == VideoCodec.H264)
            _h264Decoder.Start();
        else if (codec == VideoCodec.H265)
            _h265Decoder.Start();

        _video.Start(Host, codec);
    }

    private void OnDeviceInfo(DeviceInfo info)
    {
        DeviceModel    = info.Model;
        AndroidVersion = string.IsNullOrEmpty(info.Android) ? "" : $"Android {info.Android}";
    }

    private void OnBattery(BatteryInfo bat)
    {
        BatteryLevel    = bat.Level;
        BatteryCharging = bat.Charging;
        BatteryTemp     = bat.Temp;
    }

    private void OnNetworkStatus(NetworkStatus net)
    {
        NetworkSignalLevel = net.SignalLevel;
        NetworkIp           = net.Ip;
    }

    private void OnStreamStatus(StreamStatus s)
    {
        IsStreamStarting  = false;
        IsStreamStopping  = false;
        IsStreaming    = s.Active;
        Rotation       = s.Rotation;
        IsFrontCamera  = s.FrontCamera;
        IsFlashlightOn = s.Flashlight;
        IsBlackScreen  = s.BlackScreen;

        if (Enum.TryParse<StreamQuality>(s.Quality, out var q) && q != CurrentQuality)
            CurrentQuality = q;

        if (Enum.TryParse<StreamResolution>(s.Resolution, out var res) && res != CurrentResolution)
            CurrentResolution = res;

        // Only switch video pipeline locally — do not send command back to Android
        if (Enum.TryParse<VideoCodec>(s.Codec, out var codec) && codec != CurrentCodec)
        {
            CurrentCodec = codec;
            Settings.UpdateCodec(codec.ToString());
            StartVideo(codec);
        }
    }

    private void OnDisconnected()
    {
        // Fires only on an unexpected drop (read-loop/watchdog) — manual
        // Disconnect() goes through DoDisconnect() directly and never lands
        // here, and Dispose() tears the socket down without the event, so a
        // banner here always means "lost", never a deliberate action.
        bool wasConnected = IsConnected;
        IsStreamStarting  = false;
        IsStreamStopping  = false;
        IsConnected      = false;
        ConnectionHealth = ConnectionHealth.Disconnected;
        Rtt              = -1;
        NetworkSignalLevel = 0;
        NetworkSpeedMbps   = -1;
        NetworkIp          = "";
        DecodeLatencyMs    = -1;
        lock (_latencyLock) { _latencySumMs = 0; _latencySampleCount = 0; }
        _video.Stop();
        _h264Decoder.Stop();
        _h265Decoder.Stop();
        // IsStreaming's setter only stops the elapsed timer on a true->false edge;
        // guard here too in case the timer's still running for any reason on disconnect.
        StopStreamElapsedTimer();
        IsStreaming = false;
        if (wasConnected && !_manualDisconnect)
            ShowNotice(Loc["notice.connection_lost"], NoticeKind.Error, MaterialIconKind.LinkOff);
    }
}