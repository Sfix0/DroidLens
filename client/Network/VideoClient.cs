using System.Net.Sockets;

namespace DroidLens.Client.Network;

// Which codec this client instance reads. Only affects the sync heuristic
// (which NAL type(s) count as "keyframe config data worth waiting for") —
// framing and connection handling are identical for both.
public enum VideoElementaryCodec
{
    H264,
    H265
}

/// <summary>
/// Reads H.264 or H.265 NAL units from the codec's dedicated port.
///
/// Wire format (matches Android VideoStreamServer):
///   [4 bytes big-endian uint32 = payload size][payload bytes = Annex-B NAL unit]
///
/// Syncing: waits for the first keyframe containing a parameter-set NAL
/// before emitting frames — same as Python client for H264. For H265 this
/// waits for the SPS NAL specifically (type 33); VPS/PPS are not required
/// individually because VideoEncoder on the Android side always concatenates
/// VPS+SPS+PPS into a single buffer, so seeing the SPS within a packet means
/// the whole config blob is already present in that same packet.
/// </summary>
public class VideoClient : IDisposable
{
    /// <summary>Raw Annex-B NAL unit (starts with 00 00 00 01)</summary>
    public event Action<byte[]>? NalUnitReceived;

    /// <summary>
    /// Same NAL unit as <see cref="NalUnitReceived"/>, plus the UTC tick count
    /// at the moment the payload finished arriving over the socket. Used only
    /// for the decode-latency measurement (network arrival -> frame on screen);
    /// doesn't affect the existing NalUnitReceived consumers.
    /// </summary>
    public event Action<byte[], long>? NalUnitReceivedWithTimestamp;

    private readonly VideoElementaryCodec _codec;
    private readonly int _port;

    public VideoClient(VideoElementaryCodec codec, int port)
    {
        _codec = codec;
        _port  = port;
    }

    private CancellationTokenSource? _cts;
    private Task? _task;

    // FPS diagnostics
    private int      _frameCount;
    private DateTime _lastFpsLog = DateTime.Now;

    // TEMP DIAGNOSTIC — counter for the DIAG# log lines in ReadLoopAsync;
    // remove together with that logging block once the cause is confirmed.
    private int _diagLogCount;

    public void Start(string host)
    {
        Stop();
        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => ReadLoopAsync(host, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(2000); } catch { }
        _cts?.Dispose();
        _cts  = null;
        _task = null;
    }

    private async Task ReadLoopAsync(string host, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = new TcpClient();
                await tcp.ConnectAsync(host, _port, ct);
                var stream = tcp.GetStream();

                bool synced = false;
                var  header = new byte[4];

                while (!ct.IsCancellationRequested)
                {
                    // 1. Read 4-byte big-endian length header
                    await stream.ReadExactlyAsync(header, ct);
                    int size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];

                    // Sanity check (same as Python: 0 < size < 10 MB)
                    if (size <= 0 || size > 10_000_000)
                        continue;

                    // 2. Read exactly `size` bytes
                    byte[] data = new byte[size];
                    await stream.ReadExactlyAsync(data, ct);

                    // TEMP DIAGNOSTIC — remove once startup POC-error cause is
                    // confirmed. Logs every raw packet as it arrives off the wire,
                    // before ContainsConfigNal/sync gating, so it can be directly
                    // compared against VideoEncoder's DIAG# log on the Android side
                    // to see whether the byte sequence reaching the client already
                    // differs from what the encoder emitted.
                    if (_diagLogCount < 90)
                    {
                        _diagLogCount++;
                        int firstNalType = -1;
                        if (data.Length >= 5 &&
                            data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x01)
                        {
                            firstNalType = _codec == VideoElementaryCodec.H264
                                ? data[4] & 0x1F
                                : (data[4] >> 1) & 0x3F;
                        }
                        System.Diagnostics.Debug.WriteLine(
                            $"[VideoClient:{_codec}] DIAG#{_diagLogCount} size={data.Length} " +
                            $"firstNalType={firstNalType} synced={synced}");
                    }

                    // 3. Wait for first keyframe/config packet before emitting
                    if (!synced)
                    {
                        if (!ContainsConfigNal(data))
                            continue; // skip until keyframe
                        synced = true;
                    }

                    // 4. Emit — timestamp taken right here, as close as possible to
                    // "bytes finished arriving over the wire", before any queueing/decode.
                    long arrivalTicks = DateTime.UtcNow.Ticks;
                    NalUnitReceived?.Invoke(data);
                    NalUnitReceivedWithTimestamp?.Invoke(data, arrivalTicks);

                    // FPS diagnostics — remove once issue is resolved
                    _frameCount++;
                    if ((DateTime.Now - _lastFpsLog).TotalSeconds >= 1)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VideoClient:{_codec}] {_frameCount} nal/s");
                        _frameCount = 0;
                        _lastFpsLog = DateTime.Now;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* reconnect */ }
            finally
            {
                tcp?.Close();
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    // Checks for the codec-appropriate parameter-set NAL that marks "safe to
    // start decoding from here":
    //   H264: 00 00 00 01 67          (type 7  = SPS, 1-byte NAL header)
    //   H265: 00 00 00 01 4X          (type 33 = SPS, 2-byte NAL header —
    //         type is bits 1-6 of the first header byte, i.e. (byte >> 1) & 0x3F)
    private bool ContainsConfigNal(byte[] data)
    {
        for (int i = 0; i < data.Length - 4; i++)
        {
            if (data[i] != 0x00 || data[i + 1] != 0x00 ||
                data[i + 2] != 0x00 || data[i + 3] != 0x01)
                continue;

            if (i + 4 >= data.Length) continue;

            if (_codec == VideoElementaryCodec.H264)
            {
                if ((data[i + 4] & 0x1F) == 7) return true; // SPS
            }
            else // H265
            {
                int nalType = (data[i + 4] >> 1) & 0x3F;
                if (nalType == 33) return true; // SPS
            }
        }
        return false;
    }

    public void Dispose() => Stop();
}