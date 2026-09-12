using System.Net.Sockets;

namespace DroidLens.Client.Network;

/// <summary>
/// Reads MJPEG multipart stream from port 7878.
/// Finds JPEG frames by SOI (FF D8) / EOI (FF D9) markers —
/// same approach as the Python client.
/// </summary>
public class MjpegClient : IDisposable
{
    public event Action<byte[]>? FrameReceived; // raw JPEG bytes

    /// <summary>
    /// Same JPEG bytes as <see cref="FrameReceived"/>, plus the UTC tick count at
    /// the moment the frame finished arriving (EOI marker found) — mirrors
    /// Videoclient.NalUnitReceivedWithTimestamp, used for the MJPEG decode-latency
    /// measurement. Doesn't affect existing FrameReceived consumers.
    /// </summary>
    public event Action<byte[], long>? FrameReceivedWithTimestamp;

    private CancellationTokenSource? _cts;
    private Task? _task;

    // FPS diagnostics
    private int      _frameCount;
    private DateTime _lastFpsLog = DateTime.Now;

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
                await tcp.ConnectAsync(host, CommandClient.MjpegPort, ct);
                var stream = tcp.GetStream();

                // Send HTTP request
                var req = "GET / HTTP/1.1\r\nHost: cam\r\nConnection: keep-alive\r\n\r\n"u8.ToArray();
                await stream.WriteAsync(req, ct);

                // Skip HTTP headers until double CRLF
                var headerBuf = new List<byte>(2048);
                var tmp = new byte[1];
                while (!ct.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(tmp, ct);
                    headerBuf.Add(tmp[0]);
                    if (headerBuf.Count >= 4)
                    {
                        int n = headerBuf.Count;
                        if (headerBuf[n-4] == '\r' && headerBuf[n-3] == '\n' &&
                            headerBuf[n-2] == '\r' && headerBuf[n-1] == '\n')
                            break;
                    }
                }

                // Read JPEG frames by SOI/EOI markers
                // Fixed buffer with offset — zero allocations in the hot path (avoids O(n) RemoveRange)
                byte[] ringBuf = new byte[4 * 1024 * 1024]; // 4 MB
                int    dataLen = 0;
                var    readBuf = new byte[65536];

                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(ringBuf.AsMemory(dataLen, ringBuf.Length - dataLen), ct);
                    if (n == 0) break;
                    dataLen += n;

                    // Extract all complete JPEG frames from buffer
                    int consumed = 0;
                    while (true)
                    {
                        int start = FindBytes(ringBuf, 0xFF, 0xD8, consumed, dataLen);
                        if (start == -1) { consumed = dataLen; break; }

                        int end = FindBytes(ringBuf, 0xFF, 0xD9, start + 2, dataLen);
                        if (end == -1) break; // wait for more data

                        int frameLen = end - start + 2;
                        byte[] frame = ringBuf[start..(end + 2)];
                        consumed = end + 2;

                        // Timestamp taken here — frame is now fully assembled from
                        // the wire, before any decode work happens on the UI side.
                        long arrivalTicks = DateTime.UtcNow.Ticks;
                        FrameReceived?.Invoke(frame);
                        FrameReceivedWithTimestamp?.Invoke(frame, arrivalTicks);

                        // FPS diagnostics — remove once issue is resolved
                        _frameCount++;
                        if ((DateTime.Now - _lastFpsLog).TotalSeconds >= 1)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MjpegClient] {_frameCount} fps");
                            _frameCount  = 0;
                            _lastFpsLog  = DateTime.Now;
                        }
                    }

                    // Shift only the remaining data (not the entire buffer)
                    if (consumed > 0 && consumed < dataLen)
                    {
                        Buffer.BlockCopy(ringBuf, consumed, ringBuf, 0, dataLen - consumed);
                        dataLen -= consumed;
                    }
                    else if (consumed >= dataLen)
                    {
                        dataLen = 0;
                    }

                    // Overflow protection (in case of a corrupted/garbage stream)
                    if (dataLen > 3 * 1024 * 1024)
                        dataLen = 0;
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

    private static int FindBytes(byte[] buf, byte b0, byte b1, int startFrom, int length)
    {
        for (int i = startFrom; i < length - 1; i++)
            if (buf[i] == b0 && buf[i + 1] == b1)
                return i;
        return -1;
    }

    public void Dispose() => Stop();
}