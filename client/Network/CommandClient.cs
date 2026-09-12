using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DroidLens.Client.Models;

namespace DroidLens.Client.Network;

/// <summary>
/// Connection health derived from ping/pong heartbeat timing.
/// Live: pong received recently. Stale: pong overdue but socket still open.
/// Disconnected: socket closed / forcibly dropped after prolonged silence.
/// </summary>
public enum ConnectionHealth
{
    Live,
    Stale,
    Disconnected
}

/// <summary>
/// Manages the bidirectional JSON command channel on port 7879.
/// Receives: device_info, battery, stream_status, net
/// Sends:    {"type":"cmd","action":"...","value":"..."}
/// </summary>
public class CommandClient : IDisposable
{
    // ── Ports ──────────────────────────────────────────────────────────────────
    public const int MjpegPort = 7878;
    public const int CmdPort   = 7879;
    public const int H264Port  = 7880;
    public const int H265Port  = 7881;

    // ── Heartbeat tuning ──────────────────────────────────────────────────────
    private const int PingIntervalMs = 1000;
    private const int StaleAfterMs   = 4000;  // no pong for this long → Stale
    private const int DropAfterMs    = 10000; // no pong for this long → force Disconnect
    // A freshly-established connection gets a longer grace period than a
    // steady-state one. The phone can take a moment to start ponging on a brand
    // new socket (especially right after the phone app itself was launched), and
    // killing it at 10s — the same threshold used for "pongs were flowing, then
    // stopped" — made the first connection after launch flaky.
    private const int InitialDropAfterMs = 30000;

    // ── Events ─────────────────────────────────────────────────────────────────
    public event Action<DeviceInfo>?       DeviceInfoReceived;
    public event Action<BatteryInfo>?      BatteryReceived;
    public event Action<StreamStatus>?     StreamStatusReceived;
    public event Action<NetworkStatus>?    NetworkStatusReceived;
    public event Action<int>?              RttReceived; // milliseconds
    public event Action?                   Disconnected;
    public event Action<ConnectionHealth>? ConnectionHealthChanged;

    // ── State ──────────────────────────────────────────────────────────────────
    public bool IsConnected => _socket?.Connected == true;

    private ConnectionHealth _health = ConnectionHealth.Disconnected;
    public ConnectionHealth Health => _health;

    private TcpClient?     _socket;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _pingCts;
    private long _lastPongAtMs;
    // False until the very first pong of the current connection has arrived.
    // While false the watchdog uses InitialDropAfterMs instead of DropAfterMs
    // (see the comment on InitialDropAfterMs).
    private bool _hasPongReceived;

    // ── Connect ────────────────────────────────────────────────────────────────
    public async Task<bool> ConnectAsync(string host, CancellationToken ct = default)
    {
        Disconnect();

        try
        {
            _cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _socket = new TcpClient();
            _socket.SendTimeout    = 5000;
            _socket.ReceiveTimeout = 0; // no timeout on read – we read until disconnected

            await _socket.ConnectAsync(host, CmdPort, _cts.Token);
            _stream = _socket.GetStream();

            // Start background reader
            _ = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);

            // Heartbeat starts automatically — it both reports RTT and detects silent drops
            _lastPongAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _hasPongReceived = false;
            SetHealth(ConnectionHealth.Live);
            StartHeartbeat();

            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    // ── Send commands ──────────────────────────────────────────────────────────

    public Task SendCommandAsync(string action, string? value = null)
    {
        var obj = new JsonObject { ["type"] = "cmd", ["action"] = action };
        if (value is not null)
            obj["value"] = value;
        return SendJsonAsync(obj);
    }

    public Task SetCodecAsync(string codec)   => SendCommandAsync("set_codec",   codec);
    public Task SetQualityAsync(string q)     => SendCommandAsync("set_quality", q);
    public Task SetResolutionAsync(string r)  => SendCommandAsync("set_resolution", r);
    public Task FlipCameraAsync()             => SendCommandAsync("flip_camera");
    public Task ToggleBlackScreenAsync()      => SendCommandAsync("black_screen");
    public Task ToggleFlashlightAsync()       => SendCommandAsync("flashlight");
    public Task StartStreamAsync()            => SendCommandAsync("start");
    public Task StopStreamAsync()             => SendCommandAsync("stop");
    public Task RotateAsync()                 => SendCommandAsync("rotate");

    private void StartHeartbeat()
    {
        _pingCts?.Cancel();
        _ = SendCommandAsync("ping_start");
        _pingCts = new CancellationTokenSource();
        var ct = _pingCts.Token;
        _ = Task.Run(() => PingLoopAsync(ct));
        _ = Task.Run(() => WatchdogLoopAsync(ct));
    }

    private void StopHeartbeat()
    {
        _pingCts?.Cancel();
        _pingCts = null;
        _ = SendCommandAsync("ping_stop");
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            await SendCommandAsync("ping", ts);
            await Task.Delay(PingIntervalMs, ct).ContinueWith(_ => { });
        }
    }

    // Watches time since last pong; downgrades Live→Stale, and force-drops
    // the connection if it stays silent well beyond the heartbeat interval.
    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            await Task.Delay(500, ct).ContinueWith(_ => { });
            if (ct.IsCancellationRequested) break;

            long silentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastPongAtMs;
            long dropAfter = _hasPongReceived ? DropAfterMs : InitialDropAfterMs;

            if (silentMs >= dropAfter)
            {
                Disconnect();
                break;
            }
            else if (silentMs >= StaleAfterMs)
            {
                SetHealth(ConnectionHealth.Stale);
            }
            else
            {
                SetHealth(ConnectionHealth.Live);
            }
        }
    }

    private void SetHealth(ConnectionHealth health)
    {
        if (_health == health) return;
        _health = health;
        ConnectionHealthChanged?.Invoke(health);
    }

    // ── Private ────────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        var leftover = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream!.ReadAsync(buf, ct);
                if (n == 0) break; // connection closed
                //Console.WriteLine($"[RAW-READ] {DateTime.Now:HH:mm:ss.fff} {n} bytes");

                leftover.Append(Encoding.UTF8.GetString(buf, 0, n));

                // Process complete lines (messages are newline-delimited)
                string accumulated = leftover.ToString();
                int newlineIdx;
                while ((newlineIdx = accumulated.IndexOf('\n')) >= 0)
                {
                    string line = accumulated[..newlineIdx].Trim();
                    accumulated = accumulated[(newlineIdx + 1)..];
                    if (!string.IsNullOrEmpty(line))
                        ParseMessage(line);
                }
                leftover.Clear();
                leftover.Append(accumulated);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* socket error */ }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    private void ParseMessage(string json)
    {
        //Console.WriteLine($"[CMD-RX] {DateTime.Now:HH:mm:ss.fff} {json}");
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return;

            string? type = node["type"]?.GetValue<string>();
            switch (type)
            {
                case "pong":
                    if (long.TryParse(node["ts"]?.GetValue<string>(), out long sent))
                    {
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _lastPongAtMs = now;
                        _hasPongReceived = true;
                        RttReceived?.Invoke((int)(now - sent));
                    }
                    break;

                case "device_info":
                    DeviceInfoReceived?.Invoke(new DeviceInfo(
                        Model:   node["model"]?.GetValue<string>()   ?? "Unknown",
                        Android: node["android"]?.GetValue<string>() ?? ""
                    ));
                    break;

                case "net":
                    NetworkStatusReceived?.Invoke(new NetworkStatus(
                        SignalLevel: node["sig"]?.GetValue<int>()   ?? 0,
                        Ip:          node["ip"]?.GetValue<string>() ?? ""
                    ));
                    break;

                case "battery":
                    var tempNode = node["temp"];
                    double tempVal = (tempNode is not null && tempNode.GetValueKind() != System.Text.Json.JsonValueKind.Null)
                        ? tempNode.GetValue<double>()
                        : 0;

                    BatteryReceived?.Invoke(new BatteryInfo(
                        Level:    node["level"]?.GetValue<int>()      ?? -1,
                        Charging: node["charging"]?.GetValue<bool>()  ?? false,
                        Temp:     tempVal
                    ));
                    break;

                case "stream_status":
                    StreamStatusReceived?.Invoke(new StreamStatus(
                        Active:      node["active"]?.GetValue<bool>()       ?? false,
                        Rotation:    node["rotation"]?.GetValue<int>()      ?? 0,
                        Codec:       node["codec"]?.GetValue<string>()      ?? "MJPEG",
                        Quality:     node["quality"]?.GetValue<string>()    ?? "MEDIUM",
                        Resolution:  node["resolution"]?.GetValue<string>() ?? "HD",
                        FrontCamera: node["front_camera"]?.GetValue<bool>() ?? false,
                        Flashlight:  node["flashlight"]?.GetValue<bool>()   ?? false,
                        BlackScreen: node["black_screen"]?.GetValue<bool>() ?? false
                    ));
                    break;
            }
        }
        catch { /* malformed JSON – ignore */ }
    }

    private async Task SendJsonAsync(JsonObject obj)
    {
        if (_stream is null || !IsConnected) return;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(obj.ToJsonString() + "\n");
            await _stream.WriteAsync(data);
        }
        catch { /* ignore send errors – UI will show disconnected via event */ }
    }

    public void Disconnect()
    {
        StopHeartbeat();
        _cts?.Cancel();
        _stream?.Close();
        _socket?.Close();
        _stream = null;
        _socket = null;
        SetHealth(ConnectionHealth.Disconnected);
    }

    public void Dispose() => Disconnect();
}