using System.Linq;
using Makaretu.Dns;

namespace DroidLens.Client.Services;

/// <summary>
/// Finds DroidLens devices via mDNS / DNS-SD (RFC 6762 / 6763): primarily by
/// listening for the phone's "_droidlens._tcp.local." announcement (see
/// NsdHelper.kt on the Android side) as it happens, backed by a periodic
/// re-query (see RequeryInterval) so a missed announcement doesn't leave a
/// device undiscoverable until the user manually refreshes. No subnet
/// scanning involved either way.
/// </summary>
public sealed class MdnsDiscovery : IDisposable
{
    // Must match NsdHelper.kt's serviceType ("_DroidLens._tcp."). DNS-SD
    // service labels are case-insensitive on the wire, but lowercase here
    // to match Makaretu's own internal normalization.
    private const string ServiceType = "_droidlens._tcp";

    public event Action<DiscoveredDevice>? DeviceFound;
    public event Action<string>?           DeviceLost;
    // No real "sweep" exists with push-based mDNS, but MainViewModel's
    // scan-icon spin animation expects this — fired shortly after Refresh()
    // sends its query burst, so the UI still gets a "done" signal to stop
    // spinning instead of animating forever. Manual-scan-button only — the
    // passive background requery timer (RequeryInterval, below) never fires
    // this, so it can't be mistaken for a signal about the passive listener.
    public event Action?                   ManualScanCompleted;

    // Fires each time the background requery timer actually sends a query
    // burst (every RequeryInterval, while Start() is active) — NOT on the
    // initial Start()/Refresh() query, only the periodic ones. Lets the UI
    // play a one-shot pulse animation on the Wifi icon synced to real
    // background activity, instead of running a continuous animation for
    // the entire time discovery is listening.
    public event Action?                   RequeryStarted;

    private ServiceDiscovery? _discovery;
    private MulticastService? _mcast;
    private System.Threading.Timer? _requeryTimer;
    private bool _disposed;

    // How often to re-send a query burst while listening, as a safety net on
    // top of passive announcement listening. mDNS announce packets are only
    // sent a few times right after registration (RFC 6762 §8.3) — if the PC's
    // listener wasn't fully up yet at that exact moment (e.g. right after a
    // Stop()/Start() cycle), it can miss all of them and then never hear
    // from that device again until something asks. This was observed in
    // practice: closing and reopening the phone app made the device vanish
    // correctly (goodbye packets always arrive), but reappearing depended
    // on hitting the manual refresh button.
    private static readonly TimeSpan RequeryInterval = TimeSpan.FromSeconds(4);

    private readonly Dictionary<string, DiscoveredDevice> _found = new(); // key: instance name
    private readonly object _lock = new();

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Starts listening for mDNS announcements. Safe to call multiple times.</summary>
    public void Start()
    {
        if (_discovery is not null) return; // already running

        _mcast = new MulticastService();
        _discovery = new ServiceDiscovery(_mcast);

        _discovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        _discovery.ServiceInstanceShutdown   += OnServiceInstanceShutdown;

        _mcast.Start();
        _discovery.QueryServiceInstances(ServiceType);

        _requeryTimer = new System.Threading.Timer(
            _ =>
            {
                _discovery?.QueryServiceInstances(ServiceType);
                RequeryStarted?.Invoke();
            },
            null, RequeryInterval, RequeryInterval);
    }

    /// <summary>Stops listening. Call when a device is connected and discovery is no longer needed.</summary>
    public void Stop()
    {
        if (_discovery is null) return;

        _requeryTimer?.Dispose();
        _requeryTimer = null;

        _discovery.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
        _discovery.ServiceInstanceShutdown   -= OnServiceInstanceShutdown;
        _discovery.Dispose();
        _discovery = null;

        _mcast?.Stop();
        _mcast?.Dispose();
        _mcast = null;
    }

    /// <summary>
    /// Re-sends a query burst to prompt any devices that might have missed
    /// the last announcement (e.g. this app just started listening after
    /// the phone already sent its announce). Kept for the manual scan
    /// button — the passive listener above already catches new devices on
    /// its own without this ever being called.
    /// </summary>
    public void Refresh()
    {
        if (_discovery is null) Start();
        _discovery?.QueryServiceInstances(ServiceType);

        // No actual sweep to wait for — signal "done" shortly after so the
        // UI's spin animation doesn't run forever. Responses that arrive
        // after this still come through DeviceFound as normal. 600ms is
        // plenty for LAN mDNS replies to start arriving — this only gates
        // the spin animation, not discovery itself, so shortening it never
        // drops a device.
        _ = Task.Delay(600).ContinueWith(_ => ManualScanCompleted?.Invoke());
    }

    public IReadOnlyList<DiscoveredDevice> CurrentDevices()
    {
        lock (_lock) return _found.Values.ToList();
    }

    // ── mDNS event handlers ──────────────────────────────────────────────────

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        // Ask the phone to (re)confirm its own records rather than trusting
        // only whatever came bundled with the initial packet — some
        // responders only send the PTR record in the first announce burst.
        _mcast?.SendQuery(e.ServiceInstanceName, type: DnsType.SRV);
        _mcast?.SendQuery(e.ServiceInstanceName, type: DnsType.A);
        _mcast?.SendQuery(e.ServiceInstanceName, type: DnsType.TXT);

        // The additional records bundled with the discovery event are
        // usually already enough (SRV + A + TXT) — try resolving from those
        // first before waiting on the queries above.
        TryResolveAndAnnounce(e.ServiceInstanceName, e.Message.AdditionalRecords);
    }

    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        string instanceName = e.ServiceInstanceName.ToString();
        string? lostName = null;

        lock (_lock)
        {
            if (_found.TryGetValue(instanceName, out var device))
            {
                lostName = device.Name;
                _found.Remove(instanceName);
            }
        }

        if (lostName is not null)
            DeviceLost?.Invoke(lostName);
    }

    private void TryResolveAndAnnounce(DomainName instanceName, IEnumerable<ResourceRecord> records)
    {
        string? ip    = null;
        int     port  = 0;
        string? model = null;

        foreach (var record in records)
        {
            switch (record)
            {
                case SRVRecord srv:
                    port = srv.Port;
                    break;
                case ARecord a:
                    ip = a.Address.ToString();
                    break;
                case TXTRecord txt:
                    model = txt.Strings
                        .Select(s => s.Split('=', 2))
                        .Where(kv => kv.Length == 2 && kv[0] == "model")
                        .Select(kv => kv[1])
                        .FirstOrDefault();
                    break;
            }
        }

        if (ip is null || port == 0) return; // wait for the follow-up query response instead

        // Prefer the model name from the TXT record (device-specific, set via
        // NsdServiceInfo.setAttribute("model", ...) on the Android side —
        // MainViewModel.cs keys auto-connect on this). serviceName itself
        // ("DroidLens") is a fixed constant, not per-device, so it can't be
        // used to tell two phones apart or to match a previously-saved
        // auto-connect preference. Fall back to the instance name only if
        // the TXT record hasn't arrived yet, so the device still shows up
        // in the list rather than being dropped.
        string displayName = !string.IsNullOrWhiteSpace(model) ? model! : FirstLabelOf(instanceName, ip);

        var device = new DiscoveredDevice(displayName, ip, port);
        string key = instanceName.ToString();

        bool isNew;
        lock (_lock)
        {
            isNew = !_found.ContainsKey(key);
            _found[key] = device;
        }

        if (isNew) DeviceFound?.Invoke(device);
    }

    // Instance name looks like "DroidLens._droidlens._tcp.local." — the
    // first dot-delimited label is the raw mDNS service name. Only used as a
    // fallback when the TXT record's model attribute isn't available yet.
    private static string FirstLabelOf(DomainName instanceName, string fallback)
    {
        string fullName = instanceName.ToString();
        int dotIdx = fullName.IndexOf('.');
        if (dotIdx > 0) return fullName[..dotIdx];
        return fullName.Length > 0 ? fullName : fallback;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

// ── Reuse same record ──────────────────────────────────────────────────────────

public sealed record DiscoveredDevice(string Name, string IpAddress, int Port, bool IsUsb = false)
{
    public override string ToString() => IpAddress;
}