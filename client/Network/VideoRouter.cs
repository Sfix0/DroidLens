using DroidLens.Client.Models;

namespace DroidLens.Client.Network;

/// <summary>
/// Owns all video clients and routes frames to a single consumer.
/// Automatically switches codec when stream_status reports a change —
/// mirrors the Python client's restart_video() logic.
/// </summary>
public class VideoRouter : IDisposable
{
    public event Action<byte[]>? JpegFrameReceived;   // from MJPEG
    public event Action<byte[]>? NalUnitReceived;     // from H264 or H265

    public event Action<double>? ThroughputUpdated;   // Mbps, sampled ~1x/sec

    /// <summary>Same NAL as NalUnitReceived, plus network-arrival tick count —
    /// forwarded so the decoder (wherever it's wired up) can measure latency.</summary>
    public event Action<byte[], long>? NalUnitReceivedWithTimestamp;

    /// <summary>Same JPEG bytes as JpegFrameReceived, plus network-arrival tick
    /// count — mirrors NalUnitReceivedWithTimestamp for the MJPEG path.</summary>
    public event Action<byte[], long>? JpegFrameReceivedWithTimestamp;

    private readonly MjpegClient _mjpeg = new();
    private readonly VideoClient _h264  = new(VideoElementaryCodec.H264, CommandClient.H264Port);
    private readonly VideoClient _h265  = new(VideoElementaryCodec.H265, CommandClient.H265Port);

    private VideoCodec _currentCodec = VideoCodec.MJPEG;
    private string     _host         = "";
    private bool       _running      = false;

    // ── Throughput tracking ───────────────────────────────────────────────────
    private long _bytesSinceLastSample = 0;
    private readonly object _byteLock = new();
    private System.Threading.Timer? _throughputTimer;

    public VideoRouter()
    {
        _mjpeg.FrameReceived   += bytes => { CountBytes(bytes.Length); JpegFrameReceived?.Invoke(bytes); };
        _mjpeg.FrameReceivedWithTimestamp += (bytes, ticks) => JpegFrameReceivedWithTimestamp?.Invoke(bytes, ticks);

        _h264.NalUnitReceived  += bytes => { CountBytes(bytes.Length); NalUnitReceived?.Invoke(bytes); };
        _h264.NalUnitReceivedWithTimestamp += (bytes, ticks) => NalUnitReceivedWithTimestamp?.Invoke(bytes, ticks);

        _h265.NalUnitReceived  += bytes => { CountBytes(bytes.Length); NalUnitReceived?.Invoke(bytes); };
        _h265.NalUnitReceivedWithTimestamp += (bytes, ticks) => NalUnitReceivedWithTimestamp?.Invoke(bytes, ticks);
    }

    private void CountBytes(int length)
    {
        lock (_byteLock) { _bytesSinceLastSample += length; }
    }

    public void Start(string host, VideoCodec codec)
    {
        _host        = host;
        _currentCodec = codec;
        _running     = true;
        StartActiveClient();
        StartThroughputTimer();
    }

    private void StartThroughputTimer()
    {
        _throughputTimer?.Dispose();
        _throughputTimer = new System.Threading.Timer(_ =>
        {
            long bytes;
            lock (_byteLock)
            {
                bytes = _bytesSinceLastSample;
                _bytesSinceLastSample = 0;
            }
            double mbps = (bytes * 8) / 1_000_000.0; // sampled every 1s, so bytes/sec directly
            ThroughputUpdated?.Invoke(mbps);
        }, null, 1000, 1000);
    }

    public void SwitchCodec(VideoCodec newCodec)
    {
        if (newCodec == _currentCodec && _running) return;
        _currentCodec = newCodec;
        StopAll();
        if (_running) StartActiveClient();
    }

    public void Stop()
    {
        _running = false;
        StopAll();
        _throughputTimer?.Dispose();
        _throughputTimer = null;
        lock (_byteLock) { _bytesSinceLastSample = 0; }
    }

    private void StartActiveClient()
    {
        switch (_currentCodec)
        {
            case VideoCodec.MJPEG:
                _mjpeg.Start(_host);
                break;
            case VideoCodec.H264:
                _h264.Start(_host);
                break;
            case VideoCodec.H265:
                _h265.Start(_host);
                break;
        }
    }

    private void StopAll()
    {
        _mjpeg.Stop();
        _h264.Stop();
        _h265.Stop();
    }

    public void Dispose()
    {
        Stop();
        _mjpeg.Dispose();
        _h264.Dispose();
        _h265.Dispose();
    }
}