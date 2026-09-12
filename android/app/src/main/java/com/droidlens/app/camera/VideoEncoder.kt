package com.droidlens.app.camera

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import com.droidlens.app.BuildConfig
import com.droidlens.app.logD
import com.droidlens.app.logE

enum class VideoCodecType(val mimeType: String, val label: String) {
    H264("video/avc", "H.264"),
    H265("video/hevc", "H.265")
}

// Encapsulates the only genuinely codec-specific piece of logic: recognizing
// config (parameter-set) NAL units vs. keyframes vs. regular frames from the
// Annex-B byte stream MediaCodec hands back.
//
// H264: 1-byte NAL header, type = byte & 0x1F. SPS=7, PPS=8, IDR=5.
//       A single config buffer (SPS+PPS) arrives together on BUFFER_FLAG_CODEC_CONFIG.
// H265: 2-byte NAL header, type = (byte0 >> 1) & 0x3F. VPS=32, SPS=33, PPS=34,
//       `IDR_W_RADL`=19, `IDR_N_LP`=20, CRA=21. MediaCodec typically emits VPS+SPS+PPS
//       concatenated in one BUFFER_FLAG_CODEC_CONFIG buffer (same as H264's
//       single-buffer behavior) — treated as one opaque config blob either way.
@Suppress("unused")
private enum class NalKind { CONFIG, KEYFRAME, OTHER }

@Suppress("unused")
private fun h264NalType(data: ByteArray, startCodeLen: Int): Int =
    data[startCodeLen].toInt() and 0x1F

@Suppress("unused")
private fun h265NalType(data: ByteArray, startCodeLen: Int): Int =
    (data[startCodeLen].toInt() ushr 1) and 0x3F

// Classifies a single already-demuxed NAL (used only for the keyframe-detection
// path below; MediaCodec's own CODEC_CONFIG/KEY_FRAME buffer flags are the
// primary signal and are trusted first — this is a fallback for the sps-cache
// classification done at the server level).
@Suppress("unused")
internal fun nalStartCodeLen(data: ByteArray): Int =
    if (data.size >= 4 && data[0] == 0.toByte() && data[1] == 0.toByte() &&
        data[2] == 0.toByte() && data[3] == 1.toByte()) 4 else 0

class VideoEncoder(
    private val codecType: VideoCodecType,
    private var width: Int = 0,
    private var height: Int = 0,
    private val bitrateMbps: Int = 3,
    private val onNalUnit: (ByteArray) -> Unit
) {
    private var codec: MediaCodec? = null
    @Volatile var isRunning = false
        private set

    private val bufferInfo = MediaCodec.BufferInfo()

    // Encode-latency diagnostics: maps each frame's presentationTimeUs (the same
    // value we pass into queueInputBuffer) to the wall-clock moment feedFrame()
    // was called for it. MediaCodec echoes presentationTimeUs back on the output
    // side via bufferInfo.presentationTimeUs, so we can look up "how long ago did
    // this frame go in" purely from local monotonic time — no clock sync with the
    // PC needed. Bounded size as a safety net in case output ever stops draining.
    private val feedTimestamps = LinkedHashMap<Long, Long>()
    private var encodeLatencyLogCount = 0
    private var encodeLatencySumMs = 0.0

    fun start() {
        if (isRunning) stop()

        // Attempt 1: Full configuration with LOW_LATENCY / PREPEND_HEADER.
        // Attempt 2 (fallback): Without these two flags — some budget Qualcomm
        // Venus HW encoders (e.g., Trinket/SD460 on OPPO CPH2069) fail on
        // configure() with error 0xfffffff4 (errno ENOMEM, "out of memory") specifically due to these extended
        // parameters, even despite the SDK version formally supporting them.
        // Applies to both codecs — the failure is about the extras themselves, not
        // which video/* MIME type they're attached to.
        if (tryStart(useLowLatencyExtras = true)) return
        logD(TAG) { "Retrying without LOW_LATENCY/PREPEND_HEADER extras" }
        if (tryStart(useLowLatencyExtras = false)) return

        logE(TAG) { "Start failed on both attempts" }
        isRunning = false
        codec = null
    }

    private fun tryStart(useLowLatencyExtras: Boolean): Boolean {
        try {
            val format = MediaFormat.createVideoFormat(codecType.mimeType, width, height).apply {
                // COLOR_FormatYUV420SemiPlanar (NV12-like) is deprecated in favor of
                // COLOR_FormatYUV420Flexible, but switching color format on this code
                // path is risky without per-device testing given the existing HW encoder
                // quirks noted above (e.g. Qualcomm Venus); kept as-is intentionally.
                @Suppress("DEPRECATION")
                setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420SemiPlanar)
                setInteger(MediaFormat.KEY_BIT_RATE, bitrateMbps * 1_000_000)
                setInteger(MediaFormat.KEY_FRAME_RATE, 30)
                // Reverted to 1s after FrameInsert HAL failure on POCO F6 with interval=3.
                // TODO: investigate a safe intermediate value (e.g. 2) once confirmed as the cause.
                setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
                if (useLowLatencyExtras) {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                        setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1)
                    }
                    // Ask the encoder not to hold frames longer than the codec standard
                    // requires (no internal lookahead/reorder buffering for rate control).
                    // Supported since Android 11 (API 30); silently ignored by encoders
                    // that don't support it.
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                        setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                    }
                }
            }
            val mc = MediaCodec.createEncoderByType(codecType.mimeType)
            mc.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            mc.start()
            codec = mc
            isRunning = true
            logD(TAG) {
                "Started ${width}x${height} @ ${bitrateMbps}Mbps codec=${codecType.label} (lowLatencyExtras=$useLowLatencyExtras)"
            }
            return true
        } catch (e: Exception) {
            logE(TAG) { "Start failed codec=${codecType.label} (lowLatencyExtras=$useLowLatencyExtras): ${e.message}" }
            isRunning = false
            codec = null
            return false
        }
    }

    fun restartWithSize(newWidth: Int, newHeight: Int) {
        if (isRunning && width == newWidth && height == newHeight) return
        width = newWidth
        height = newHeight
        configData = null
        ptsStartNanos = 0L
        logD(TAG) { "restartWithSize ${newWidth}x${newHeight}" }
        stop()
        start()
    }
    // Opaque config blob (SPS+PPS for H264; VPS+SPS+PPS for H265) — MediaCodec
    // emits it as a single BUFFER_FLAG_CODEC_CONFIG buffer for both codecs, so
    // one field covers either case without needing to parse it apart here.
    private var configData: ByteArray? = null

    // Diagnostics for the non-blocking dequeueInputBuffer change below: counts how
    // often the encoder had no free input buffer available and the frame was
    // dropped, so we can tell from logs whether this path is actually hit often
    // enough on a given device to justify moving to MediaCodec.Callback (async
    // mode) instead of polling.
    private var droppedFrameCount = 0
    private var droppedFrameLogCount = 0

    // Internal, monotonically increasing presentationTimeUs fed to MediaCodec —
    // deliberately independent of the timestampUs the caller passes in.
    // CameraPreview's timestampUs is derived from a `startTimeUs` local that
    // resets to 0 every time its LaunchedEffect restarts the camera (camera
    // flip, or toggling black screen while streaming; see the effect keys
    // there). If we fed that directly into queueInputBuffer, a flip or a
    // black screen toggle mid-session would hand the *same still-running*
    // MediaCodec a presentationTimeUs that suddenly jumps backwards. That
    // stalls or corrupts the encoder's internal rate-control and reorder
    // state — the "stream freezes on camera restart" bug. Deriving our own
    // PTS from a local nanoTime-based clock means restarts on the caller's
    // side can never produce a non-monotonic timestamp here, regardless of
    // what timestampUs says.
    private var ptsStartNanos: Long = 0L

    fun feedFrame(i420: ByteArray, width: Int, height: Int, timestampUs: Long) {
        // Lazy init: start the encoder on the first frame or if the size has changed
        if (!isRunning || width != this.width || height != this.height) {
            restartWithSize(width, height)
        }

        val mc = codec ?: return
        try {
            val nowNanos = System.nanoTime()
            if (ptsStartNanos == 0L) ptsStartNanos = nowNanos
            val monotonicPtsUs = (nowNanos - ptsStartNanos) / 1000

            // Feed the frame. Non-blocking (timeout=0): if no input buffer is free
            // right now, drop this frame instead of stalling the analyzer thread for
            // up to 10ms — at 30fps that's ~30% of the entire per-frame budget, and
            // blocking here delays every downstream step (UV interleave already done,
            // but the output-buffer drain loop right below never even starts). A
            // dropped frame is a strictly better outcome than a stuttering pipeline.
            val inputIndex = mc.dequeueInputBuffer(0)
            if (inputIndex >= 0) {
                val buf = mc.getInputBuffer(inputIndex)!!
                buf.clear()
                buf.put(i420)
                // Record when this frame entered the encoder, keyed by the same
                // monotonicPtsUs MediaCodec will echo back in bufferInfo.presentationTimeUs.
                // Debug-only: this tracking exists purely for the encode-latency
                // diagnostic below and has no effect on release builds.
                if (BuildConfig.DEBUG) {
                    feedTimestamps[monotonicPtsUs] = System.nanoTime()
                    if (feedTimestamps.size > 30) {
                        // Safety net: drop the oldest entry if output ever stalls,
                        // so this map can't grow unbounded.
                        feedTimestamps.remove(feedTimestamps.keys.first())
                    }
                }
                mc.queueInputBuffer(inputIndex, 0, i420.size, monotonicPtsUs, 0)
            } else if (BuildConfig.DEBUG) {
                // No free input buffer right now — frame dropped rather than blocked on.
                // Logged periodically (not every drop) to avoid log spam if this is
                // happening frequently; frequency here is the signal for whether async

                // MediaCodec.Callback would be worth the extra complexity.
                droppedFrameCount++
                droppedFrameLogCount++
                if (droppedFrameLogCount >= 30) {
                    logD(TAG) { "Dropped $droppedFrameCount frames total (no free input buffer)" }
                    droppedFrameLogCount = 0
                }
            }

            // Read output data
            while (true) {
                val outputIndex = mc.dequeueOutputBuffer(bufferInfo, 0)
                when {
                    outputIndex >= 0 -> {
                        try {
                            if (bufferInfo.size > 0) {
                                val buf = mc.getOutputBuffer(outputIndex)!!
                                val data = ByteArray(bufferInfo.size)
                                buf.get(data)

                                // Encode-latency diagnostic: look up when this exact frame
                                // (by presentationTimeUs) went into the encoder. Config
                                // buffers (SPS/PPS or VPS/SPS/PPS) don't carry a real frame's
                                // timestamp, so this naturally only matches real frames.
                                // Debug-only — feedTimestamps is never populated in release
                                // builds, so this whole block is skipped there.
                                if (BuildConfig.DEBUG) {
                                    feedTimestamps.remove(bufferInfo.presentationTimeUs)?.let { feedNanos ->
                                        val latencyMs = (System.nanoTime() - feedNanos) / 1_000_000.0
                                        encodeLatencySumMs += latencyMs
                                        encodeLatencyLogCount++
                                        if (encodeLatencyLogCount >= 30) { // ~1s at 30fps
                                            logD(TAG) {
                                                "Encode latency (avg over $encodeLatencyLogCount frames): " +
                                                        "${"%.1f".format(encodeLatencySumMs / encodeLatencyLogCount)}ms"
                                            }
                                            encodeLatencyLogCount = 0
                                            encodeLatencySumMs = 0.0
                                        }
                                    }
                                }

                                val isConfig   = bufferInfo.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0
                                val isKeyFrame = bufferInfo.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                                val startCode  = byteArrayOf(0x00, 0x00, 0x00, 0x01)

                                when {
                                    isConfig -> {
                                        // Save config blob, don't send separately.
                                        // H264: SPS+PPS. H265: VPS+SPS+PPS.
                                        configData = data
                                        logD(TAG) { "Config saved (${codecType.label}): ${data.size} bytes" }
                                    }
                                    isKeyFrame -> {
                                        val combined = if (configData != null) {
                                            val cfg = if (configData!!.size >= 4 &&
                                                configData!![0] == 0.toByte() && configData!![1] == 0.toByte() &&
                                                configData!![2] == 0.toByte() && configData!![3] == 1.toByte()
                                            ) configData!! else startCode + configData!!

                                            val idr = if (data.size >= 4 &&
                                                data[0] == 0.toByte() && data[1] == 0.toByte() &&
                                                data[2] == 0.toByte() && data[3] == 1.toByte()
                                            ) data else startCode + data

                                            cfg + idr
                                        } else {
                                            if (data.size >= 4 &&
                                                data[0] == 0.toByte() && data[1] == 0.toByte() &&
                                                data[2] == 0.toByte() && data[3] == 1.toByte()
                                            ) data else startCode + data
                                        }
                                        onNalUnit(combined)
                                    }
                                    else -> {
                                        val frame = if (data.size >= 4 &&
                                            data[0] == 0.toByte() && data[1] == 0.toByte() &&
                                            data[2] == 0.toByte() && data[3] == 1.toByte()
                                        ) data else startCode + data
                                        onNalUnit(frame)
                                    }
                                }
                            }
                        } finally {
                            mc.releaseOutputBuffer(outputIndex, false)
                        }
                    }
                    outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                        logD(TAG) { "Output format changed" }
                    }
                    else -> break
                }
            }
        } catch (e: Exception) {
            logE(TAG) { "feedFrame error: ${e.message}" }
        }
    }
    fun updateBitrate(newBitrateMbps: Int) {
        val params = android.os.Bundle().apply {
            putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, newBitrateMbps * 1_000_000)
        }
        codec?.setParameters(params)
        logD(TAG) { "Bitrate updated to ${newBitrateMbps}Mbps" }
    }

    // Forces the encoder to emit a fresh IDR (keyframe) on the next feedFrame()
    // call. Needed whenever a new client joins the stream mid-session: without
    // this, a late-joining client is handed the *cached* last keyframe (see
    // VideoStreamServer.lastConfigPacket) while the encoder keeps producing
    // P-frames against a GOP that started long before the client connected —
    // the client's decoder then hits P-frames referencing POCs it was never
    // sent, i.e. "Could not find ref with POC N". Requesting a real, freshly
    // produced sync frame here guarantees whatever a new client receives as
    // its "config" packet is the actual start of the GOP that follows it.
    fun requestKeyframe() {
        val params = android.os.Bundle().apply {
            putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0)
        }
        codec?.setParameters(params)
        logD(TAG) { "Keyframe requested (new client joined)" }
    }

    fun stop() {
        logD(TAG) { "stop() called, isRunning=$isRunning, codec=${codec != null}" }
        isRunning = false
        try {
            codec?.stop()
            codec?.release()
        } catch (e: Exception) {
            logE(TAG) { "Stop error: ${e.message}" }
        }
        codec = null
        ptsStartNanos = 0L
        if (BuildConfig.DEBUG) {
            feedTimestamps.clear()
            encodeLatencyLogCount = 0
            encodeLatencySumMs = 0.0
            droppedFrameCount = 0
            droppedFrameLogCount = 0
        }
        logD(TAG) { "Stopped" }
    }

    private companion object {
        const val TAG = "VideoEncoder"
    }
}