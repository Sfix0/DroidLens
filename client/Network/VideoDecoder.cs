using Sdcb.FFmpeg.Raw;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DroidLens.Client.Network;

public sealed class VideoDecoder : IDisposable
{
    public event Action<byte[], int, int>? BgraFrameReceived;

    /// <summary>
    /// Fired right after BgraFrameReceived with the elapsed milliseconds between
    /// "NAL arrived over the socket" and "decoded frame ready" — i.e. queue time
    /// + avcodec_send/receive + hwframe transfer + sws_scale. Diagnostic only.
    /// </summary>
    public event Action<double>? DecodeLatencyMeasured;

    /// <summary>
    /// Fired once when the decoder fails to initialize (avcodec_open2 refuses
    /// the codec) — the stream for this codec will stay black. Per-frame
    /// hiccups deliberately do NOT fire this (would spam the UI).
    /// </summary>
    public event Action? DecodeFailed;

    private readonly VideoElementaryCodec _codec;
    private readonly AVCodecID _avCodecId;

    public VideoDecoder(VideoElementaryCodec codec)
    {
        _codec = codec;
        _avCodecId = codec switch
        {
            VideoElementaryCodec.H264 => AVCodecID.H264,
            VideoElementaryCodec.H265 => AVCodecID.Hevc,
            _ => throw new ArgumentOutOfRangeException(nameof(codec))
        };
    }

    // Small on purpose: this queue exists only to smooth out momentary jitter
    // between network arrival and decode. A large bound (previously 90, ~3s)
    // let backlog silently accumulate whenever decode fell behind even briefly —
    // once behind, PushNal's drop-oldest logic only kicks in once *full*, so a
    // 90-deep queue could sit near-full and add up to 3s of latency that never
    // recovered. Kept small, drops start almost immediately, so latency can't
    // build up — worst case we lose a few frames of jitter instead of gaining
    // seconds of lag.
    private readonly BlockingCollection<(byte[] Nal, long ArrivalTicks)> _queue = new(boundedCapacity: 3);
    private CancellationTokenSource? _cts;
    private Task? _task;

    // Once we drop a NAL because the queue was full, every P-frame after it is
    // referencing a decoded picture (POC) the decoder never got — feeding those
    // in produces "Could not find ref with POC N" and visible corruption until
    // the next keyframe resets the GOP. So instead of dropping just the single
    // oldest NAL (which can land mid-GOP and leave a hole in the reference
    // chain), once we're forced to drop we keep dropping everything — including
    // newly-arriving NALs, not just the queued one — until the next keyframe
    // (config blob + IDR, or a bare IDR/CRA) shows up. That's a clean resync
    // point the decoder can always handle standalone.
    private bool _waitingForKeyframe;

    public void Start()
    {
        Stop();
        _cts  = new CancellationTokenSource();
        _task = Task.Factory.StartNew(
            () => DecodeLoop(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void PushNal(byte[] nal) => PushNal(nal, DateTime.UtcNow.Ticks);

    /// <summary>Same as PushNal, but carries the network-arrival timestamp through
    /// to the queue so decode latency can be measured end-to-end.</summary>
    public void PushNal(byte[] nal, long arrivalTicks)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;

        bool isKeyframe = IsKeyframeNal(nal);

        // If we're mid-resync (already dropped something), discard everything
        // that isn't a keyframe — a lone P-frame here would just reference a
        // POC we never decoded and produce the same corruption we're trying
        // to avoid.
        if (_waitingForKeyframe && !isKeyframe)
            return;

        var item = (nal, arrivalTicks);
        if (_queue.TryAdd(item))
        {
            if (isKeyframe) _waitingForKeyframe = false;
            return;
        }

        // Queue is full. Don't just evict the oldest slot and keep going —
        // that can leave a hole mid-GOP. Instead, drain the whole queue and
        // wait for the next keyframe before accepting anything again, unless
        // this incoming NAL is itself a keyframe (clean resync point).
        while (_queue.TryTake(out _)) { }

        if (isKeyframe)
        {
            _queue.TryAdd(item);
            _waitingForKeyframe = false;
        }
        else
        {
            _waitingForKeyframe = true;
        }
    }

    // Recognizes a keyframe (config blob prepended to an IDR, or a bare
    // IDR/CRA) from the Annex-B NAL as produced by VideoEncoder.kt: config
    // data (VPS+SPS+PPS for H265, SPS+PPS for H264) is concatenated directly
    // in front of the IDR bytes with no separator, so scanning for the first
    // start code identifies the leading NAL, and — for H265 specifically —
    // config blobs always start with VPS (type 32) while a bare keyframe
    // starts with IDR_W_RADL/IDR_N_LP/CRA (19/20/21).
    private bool IsKeyframeNal(byte[] data)
    {
        if (data.Length < 5) return false;
        if (data[0] != 0x00 || data[1] != 0x00 || data[2] != 0x00 || data[3] != 0x01)
            return false;

        if (_codec == VideoElementaryCodec.H264)
        {
            int nalType = data[4] & 0x1F;
            return nalType == 7 || nalType == 5; // SPS (config+IDR combo) or bare IDR
        }
        else
        {
            int nalType = (data[4] >> 1) & 0x3F;
            return nalType == 32 || nalType == 19 || nalType == 20 || nalType == 21; // VPS or bare IDR_W_RADL/IDR_N_LP/CRA
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _queue.TryAdd((Array.Empty<byte>(), 0L)); } catch { }
        try { _task?.Wait(2000); } catch { }
        _cts?.Dispose();
        _cts  = null;
        _task = null;
    }

    private unsafe void DecodeLoop(CancellationToken ct)
    {
        AVCodec*        codec = ffmpeg.avcodec_find_decoder(_avCodecId);
        AVCodecContext* ctx   = ffmpeg.avcodec_alloc_context3(codec);

        // --- GPU: try D3D11VA, fallback to CPU if failed ---
        // D3D11VA supports HEVC decode on any GPU with driver-level HEVC support
        // (all modern GPUs) exactly the same way it supports H264 — no separate
        // device-creation path needed per codec, avcodec_open2 below negotiates
        // the right hardware decoder profile from _avCodecId automatically.
        AVBufferRef* hwDeviceCtx = null;
#if WINDOWS
        bool useHwAccel = false;
        int hwResult = ffmpeg.av_hwdevice_ctx_create(
            &hwDeviceCtx,
            AVHWDeviceType.D3d11va,
            null, null, 0);

        if (hwResult >= 0)
        {
            ctx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
            useHwAccel = true;
        }
        // hwResult < 0: GPU unavailable, silently fall back to CPU
#else
        bool useHwAccel = false;
#endif

        if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
        {
            try { DecodeFailed?.Invoke(); } catch { }
            return;
        }

        // --- Diagnostics: check active decoding path ---
        System.Diagnostics.Debug.WriteLine(useHwAccel
            ? $"[VideoDecoder:{_codec}] ✓ GPU decoding via D3D11VA"
            : $"[VideoDecoder:{_codec}] ⚠ CPU decoding (software fallback)");

        AVFrame*  yuvFrame = ffmpeg.av_frame_alloc();
        AVFrame* swFrame  = ffmpeg.av_frame_alloc(); // for GPU->CPU transfer
        AVPacket* pkt      = ffmpeg.av_packet_alloc();
        long ptsCounter = 0;

        SwsContext* swsCtx = null;
        int swsW = 0, swsH = 0;

        // Reusable output buffer — reallocated on size change
        byte[]? outBuf = null;
        IntPtr  outPtr = IntPtr.Zero;
        int     outStride = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[] nal;
                long   arrivalTicks;
                try   { (nal, arrivalTicks) = _queue.Take(ct); }
                catch (OperationCanceledException) { break; }
                if (nal.Length == 0) continue;

                // Send packet
                fixed (byte* nalPtr = nal)
                {
                    pkt->data = nalPtr;
                    pkt->size = nal.Length;
                    pkt->pts  = ptsCounter;
                    pkt->dts  = ptsCounter;
                    ptsCounter++;
                    if (ffmpeg.avcodec_send_packet(ctx, pkt) < 0) continue;
                }

                // Receive frames
                while (!ct.IsCancellationRequested)
                {
                    int ret = ffmpeg.avcodec_receive_frame(ctx, yuvFrame);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) break;

                    int w = yuvFrame->width;
                    int h = yuvFrame->height;
                    if (w <= 0 || h <= 0) { ffmpeg.av_frame_unref(yuvFrame); continue; }

                    // --- First frame diagnostics ---
                    if (ptsCounter == 1)
                        System.Diagnostics.Debug.WriteLine(
                            $"[VideoDecoder:{_codec}] First frame: fmt={(AVPixelFormat)yuvFrame->format} " +
                            $"hw_frames_ctx={(yuvFrame->hw_frames_ctx != null ? "GPU" : "CPU")}");

                    // --- GPU->CPU transfer if the frame is on the GPU ---
                    AVFrame* srcFrame; // points either to yuvFrame (CPU) or swFrame (after transfer)
                    if (useHwAccel && yuvFrame->hw_frames_ctx != null)
                    {
                        ffmpeg.av_frame_unref(swFrame);
                        int transferResult = ffmpeg.av_hwframe_transfer_data(swFrame, yuvFrame, 0);
                        if (transferResult < 0)
                        {
                            // Transfer failed - skip the frame
                            ffmpeg.av_frame_unref(yuvFrame);
                            continue;
                        }
                        swFrame->width  = yuvFrame->width;
                        swFrame->height = yuvFrame->height;
                        srcFrame = swFrame; // NV12 on CPU
                    }
                    else
                    {
                        srcFrame = yuvFrame; // YUV420P on CPU (as before)
                    }

                    // Recreate sws + output buffer if size or pixel format changed
                    AVPixelFormat srcFmt = (AVPixelFormat)srcFrame->format;
                    if (swsCtx == null || swsW != w || swsH != h)
                    {
                        if (swsCtx != null) ffmpeg.sws_freeContext(swsCtx);
                        if (outPtr != IntPtr.Zero) Marshal.FreeHGlobal(outPtr);

                        // srcFmt: NV12 (GPU path) or YUV420P (CPU path) — sws supports both
                        swsCtx = ffmpeg.sws_getContext(
                            w, h, srcFmt,
                            w, h, AVPixelFormat.Rgba,
                            2, null, null, null); // 2 = SWS_BILINEAR

                        outStride = w * 4; // RGBA = 4 bytes per pixel
                        outPtr    = Marshal.AllocHGlobal(outStride * h);
                        outBuf    = new byte[outStride * h];
                        swsW = w; swsH = h;
                    }

                    // sws_scale into our manually allocated buffer
                    byte* dstData   = (byte*)outPtr;
                    int   dstStride = outStride;

                    byte*[] srcPtrs    = new byte*[4];
                    srcPtrs[0] = (byte*)srcFrame->data[0];
                    srcPtrs[1] = (byte*)srcFrame->data[1];
                    srcPtrs[2] = (byte*)srcFrame->data[2];
                    srcPtrs[3] = (byte*)srcFrame->data[3];

                    byte*[] dstPtrs    = new byte*[1] { dstData };
                    int[]   srcStrides = new int[4]
                    {
                        srcFrame->linesize[0], srcFrame->linesize[1],
                        srcFrame->linesize[2], srcFrame->linesize[3]
                    };
                    int[]   dstStrides = new int[1] { dstStride };

                    int scaled = ffmpeg.sws_scale(swsCtx,
                        srcPtrs, srcStrides, 0, h,
                        dstPtrs, dstStrides);

                    if (scaled <= 0) { ffmpeg.av_frame_unref(yuvFrame); continue; }

                    // Copy to outBuf (reusable) and dispatch immediately — Dispatcher.Post
                    // is guaranteed not to modify the array, only reads it for rendering
                    Marshal.Copy(outPtr, outBuf!, 0, outBuf!.Length);
                    BgraFrameReceived?.Invoke(outBuf, w, h);

                    // arrivalTicks is stamped when this NAL's bytes finished arriving over
                    // the socket (see VideoClient). Note this is the queue slot's timestamp,
                    // not necessarily this exact decoded frame's — with B-frame-free encoding
                    // (confirmed on the Android side) decode order == arrival order, so the
                    // two line up in practice, but a queued packet can still yield 0 or 2+
                    // frames from one send_packet/receive_frame pass. Good enough for a
                    // diagnostic reading; not frame-exact.
                    if (arrivalTicks > 0)
                    {
                        double latencyMs = new TimeSpan(DateTime.UtcNow.Ticks - arrivalTicks).TotalMilliseconds;
                        DecodeLatencyMeasured?.Invoke(latencyMs);
                    }

                    ffmpeg.av_frame_unref(yuvFrame);
                }
            }
        }
        finally
        {
            if (swsCtx != null) ffmpeg.sws_freeContext(swsCtx);
            if (outPtr != IntPtr.Zero) Marshal.FreeHGlobal(outPtr);
            ffmpeg.av_frame_free(&swFrame);
            ffmpeg.av_frame_free(&yuvFrame);
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.avcodec_free_context(&ctx);
            if (hwDeviceCtx != null) ffmpeg.av_buffer_unref(&hwDeviceCtx);
        }
    }

    public void Dispose() => Stop();
}