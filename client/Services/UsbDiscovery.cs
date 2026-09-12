using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using DroidLens.Client.Network;

namespace DroidLens.Client.Services;

/// <summary>
/// Finds a DroidLens phone connected over USB via adb, and forwards the app's
/// fixed TCP ports (7878/7879/7880/7881) from the device to 127.0.0.1 on the host.
/// Mirrors MdnsDiscovery's event contract so MainViewModel can treat USB and
/// Wi-Fi devices as the same kind of DiscoveredDevice.
///
/// Single-device design: only one USB phone is forwarded at a time. If a
/// second device appears while one is already forwarded, it is ignored until
/// the first disconnects.
/// </summary>
public sealed class UsbDiscovery : IDisposable
{
    public event Action<DiscoveredDevice>? DeviceFound;
    public event Action<string>?           DeviceLost;

    /// <summary>
    /// Fired when the bundled adb server can't be started (binary missing or
    /// launch failed) — USB discovery stays inactive. The UI surfaces this as
    /// a one-off warning; Wi-Fi-only users just never get USB devices.
    /// </summary>
    public event Action? ServerStartFailed;

    private const string LoopbackHost = "127.0.0.1";

    private readonly AdbClient _adb = new();
    private System.Threading.Timer? _pollTimer;
    private bool _disposed;

    private string? _activeSerial;   // serial of the device we've currently forwarded
    private string? _activeName;     // display name given to DeviceFound, needed for DeviceLost

    // ── Public API ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (!TryEnsureServerStarted())
        {
            // adb.exe missing or failed to launch — USB discovery simply stays inactive
            try { ServerStartFailed?.Invoke(); } catch { }
            return;
        }

        _pollTimer = new System.Threading.Timer(_ => Poll(), null, 0, 2000);
    }

    /// <summary>
    /// Stops the poll loop only. Deliberately does NOT release the active
    /// device's port forwards — this is called when the app transitions into
    /// an active connection to the just-discovered USB device, and tearing
    /// down the forwards here would kill the MJPEG/H264/H265 ports (7878/7880/7881)
    /// out from under the video pipeline that's about to connect to them,
    /// even though the already-open command socket (7879) keeps working.
    /// Use <see cref="StopAndRelease"/> when the device itself is going away.
    /// </summary>
    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    /// <summary>
    /// Stops polling AND releases the active device's forwards. Use this on
    /// real teardown (e.g. app shutdown, or when the user disconnects and USB
    /// discovery should go back to a clean slate before resuming Poll()).
    /// </summary>
    public void StopAndRelease()
    {
        Stop();
        ReleaseActiveDevice();
    }

    // ── Server bootstrap ───────────────────────────────────────────────────────

    private bool TryEnsureServerStarted()
    {
        try
        {
            if (AdbServer.Instance.GetStatus().IsRunning)
                return true;

            string adbPath = ResolveEmbeddedAdbPath();
            var result = new AdbServer().StartServer(adbPath, restartServerIfNewer: false);
            return result is StartServerResult.Started or StartServerResult.AlreadyRunning;
        }
        catch
        {
            return false; // no adb binary bundled, or USB support unavailable on this machine
        }
    }

    private static string ResolveEmbeddedAdbPath()
    {
        string exeName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        return Path.Combine(AppContext.BaseDirectory, "Tools", exeName);
    }

    // ── Poll loop ──────────────────────────────────────────────────────────────

    private void Poll()
    {
        try
        {
            var devices = _adb.GetDevices()?.ToList() ?? new List<DeviceData>();

            // Find the online device defensively: an empty `devices` list (cable pulled)
            // must never let us dereference a default/null DeviceData below — that was
            // the source of the NullReferenceException that skipped ReleaseActiveDevice()
            // entirely and left _activeSerial stuck forever.
            string? onlineSerial = null;
            DeviceData? onlineDevice = null;
            foreach (var d in devices)
            {
                if (d.State == DeviceState.Online && !string.IsNullOrEmpty(d.Serial))
                {
                    onlineSerial = d.Serial;
                    onlineDevice = d;
                    break;
                }
            }

            if (onlineSerial is null)
            {
                // nothing plugged in (or still unauthorized) — drop whatever we had
                if (_activeSerial is not null)
                    ReleaseActiveDevice();
                return;
            }

            if (_activeSerial == onlineSerial)
            {
                // Same device still plugged in and adb-online — but that only means
                // the cable/adb link is alive, not that DroidLens itself is still
                // running on the phone. If the user closed the app while the cable
                // stayed in, adb keeps reporting Online forever, so we'd otherwise
                // leave a dead device sitting in the list indefinitely. Re-check the
                // command port on every tick and drop the device the same way a
                // cable-pull would if the app has stopped answering on it.
                if (!IsAppReachable())
                    ReleaseActiveDevice();
                return;
            }

            // A different (or first) device appeared — release any stale forward, then adopt it
            if (_activeSerial is not null)
                ReleaseActiveDevice();

            AdoptDevice(onlineDevice!);
        }
        catch
        {
            // GetDevices()/CreateForward() can throw transiently around USB
            // (re)connects — swallow and let the next poll tick retry.
        }
    }

    private void AdoptDevice(DeviceData device)
    {
        try
        {
            _adb.CreateForward(device, "tcp:" + CommandClient.MjpegPort, "tcp:" + CommandClient.MjpegPort, allowRebind: true);
            _adb.CreateForward(device, "tcp:" + CommandClient.CmdPort,   "tcp:" + CommandClient.CmdPort,   allowRebind: true);
            _adb.CreateForward(device, "tcp:" + CommandClient.H264Port, "tcp:" + CommandClient.H264Port,  allowRebind: true);
            _adb.CreateForward(device, "tcp:" + CommandClient.H265Port, "tcp:" + CommandClient.H265Port,  allowRebind: true);

            // adb Online only means the cable/debugging link is up — it says nothing
            // about whether DroidLens is actually running on the phone. Confirm the
            // app itself is listening on the command port before surfacing the
            // device to the UI, otherwise every phone with USB debugging on shows
            // up as a "device" whether or not the app was ever launched.
            if (!IsAppReachable())
            {
                _adb.RemoveAllForwards(device);
                return; // leave state clean — next Poll() tick will retry
            }

            _activeSerial = device.Serial;
            string name = ResolveDisplayName(device);
            _activeName = name;

            DeviceFound?.Invoke(new DiscoveredDevice(name, LoopbackHost, CommandClient.MjpegPort, IsUsb: true));
        }
        catch
        {
            // forwarding failed (device unauthorized, offline mid-call, etc.) — leave state clean
            _activeSerial = null;
            _activeName = null;
        }
    }

    // Lightweight liveness probe for the command port — deliberately NOT
    // CommandClient: that class opens a persistent JSON session, starts a
    // ping/pong heartbeat, and fires Disconnected/ConnectionHealthChanged
    // events. Reusing it here from a 2s poll loop would spin up (and tear
    // down) a real session every tick and risk racing the actual connection
    // MainViewModel opens once the user hits Connect.
    //
    // A bare TcpClient.Connect() is NOT enough here, unlike a normal "is this
    // port open" check: adb's forward is itself a local listener on the host.
    // adb accepts the loopback TCP connection immediately and only afterwards
    // tries to relay it to the device side, so Connect() succeeds even when
    // nothing on the phone is listening on 7879 — the relay failure (if any)
    // only shows up later, on the data path. So we have to actually speak the
    // app's protocol: send a "ping" (same message CommandClient sends) and
    // wait a short, bounded time for any line back. Only a real reply proves
    // there's a live DroidLens instance on the other end of the forward.
    private static bool IsAppReachable()
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            var connectTask = probe.ConnectAsync(LoopbackHost, CommandClient.CmdPort);
            if (!connectTask.Wait(TimeSpan.FromMilliseconds(600)))
                return false; // timed out — nothing answering on the port

            if (!probe.Connected)
                return false;

            probe.SendTimeout    = 500;
            probe.ReceiveTimeout = 500;
            using var stream = probe.GetStream();

            byte[] ping = System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":\"cmd\",\"action\":\"ping\",\"value\":\"0\"}\n");
            stream.Write(ping, 0, ping.Length);
            stream.Flush();

            var buf = new byte[256];
            int n = stream.Read(buf, 0, buf.Length); // throws/times out if no live peer
            return n > 0;
        }
        catch
        {
            return false; // port closed / connection refused / reset / timed out — app isn't running
        }
    }

    // adb's DeviceData.Model comes from `ro.product.model`, which is usually a
    // technical codename (e.g. "SM_S911B"), not the marketing name users
    // recognize. The phone's own CommandServer already solves this over Wi-Fi
    // by reading `ro.product.marketname` via reflection — we do the same thing
    // here via `adb shell getprop`, since it's just as available over USB.
    private string ResolveDisplayName(DeviceData device)
    {
        string? marketName = null;
        try
        {
            var receiver = new AdvancedSharpAdbClient.Receivers.ConsoleOutputReceiver();
            _adb.ExecuteRemoteCommand("getprop ro.product.marketname", device, receiver);
            marketName = receiver.ToString()?.Trim();
        }
        catch { /* fall through to Model/Serial below */ }

        string baseName = !string.IsNullOrWhiteSpace(marketName)
            ? marketName!
            : (string.IsNullOrWhiteSpace(device.Model) ? device.Serial : device.Model);

        return baseName;
    }

    private void ReleaseActiveDevice()
    {
        if (_activeSerial is null) return;

        try
        {
            _adb.RemoveAllForwards(new DeviceData { Serial = _activeSerial });
        }
        catch
        {
            // device likely already unplugged — nothing to clean up on its end,
            // but we still must clear our own state and fire DeviceLost below.
        }

        string? nameToReport = _activeName;
        _activeSerial = null;
        _activeName   = null;

        if (nameToReport is not null)
            DeviceLost?.Invoke(nameToReport);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAndRelease();
    }
}