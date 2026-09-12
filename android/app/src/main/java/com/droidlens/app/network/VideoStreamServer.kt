package com.droidlens.app.network

import com.droidlens.app.camera.VideoCodecType
import com.droidlens.app.logD
import com.droidlens.app.logE
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.CopyOnWriteArrayList

// Generic length-prefixed NAL broadcaster, used for both H264 (port 7880) and
// H265 (port 7881). Framing, client lifecycle, and broadcast logic are fully
// codec-agnostic — the only codec-specific bit is recognizing a "config" NAL
// (SPS for H264, VPS/SPS/PPS blob for H265) so it can be cached and replayed
// to clients that connect mid-stream, the same way H264StreamServer did.
class VideoStreamServer(
    private val codecType: VideoCodecType,
    private val port: Int,
    // Called on a new client connection, off the main thread. Used by
    // StreamViewModel to ask the live encoder for a fresh IDR (see
    // VideoEncoder.requestKeyframe) instead of replaying the stale cached
    // one below — a cached keyframe is the start of a GOP the encoder has
    // already moved on from, so P-frames sent afterward reference POCs the
    // new client's decoder never received ("Could not find ref with POC N").
    private val onClientJoined: () -> Unit = {}
) {
    private var serverSocket: ServerSocket? = null
    private val clients = CopyOnWriteArrayList<OutputStream>()
    // Clients that are connected but haven't yet received a keyframe belonging
    // to their own session — sendNalUnit() skips these for regular frames and
    // only promotes them to `clients` once a real (non-cached) keyframe goes out.
    private val pendingClients = CopyOnWriteArrayList<OutputStream>()
    @Volatile private var running = false
    @Volatile private var lastConfigPacket: ByteArray? = null
    private var serverThread: Thread? = null

    private val tag = "VideoStreamServer[${codecType.label}]"

    fun start() {
        if (running) return
        running = true
        serverThread = Thread {
            try {
                serverSocket = ServerSocket(port).also { it.soTimeout = 1000 }
                logD(tag) { "started on port $port" }
                while (running) {
                    try {
                        val socket = serverSocket?.accept() ?: break
                        logD(tag) { "client connected: ${socket.inetAddress}" }
                        Thread { handleClient(socket) }.start()
                    } catch (_: java.net.SocketTimeoutException) {
                        // normal — just check running and keep waiting
                    } catch (e: Exception) {
                        if (running) logE(tag) { "accept error: ${e.message}" }
                    }
                }
            } catch (e: Exception) {
                logD(tag) { "ended: ${e.message}" }
            }
        }.also { it.start() }
    }

    private fun handleClient(socket: Socket) {
        var out: OutputStream? = null
        try {
            out = socket.getOutputStream()
            // No longer replay lastConfigPacket here — it's a keyframe from a
            // GOP the encoder has already moved past. The client is parked in
            // pendingClients and only starts receiving data once a genuinely
            // fresh keyframe is produced (see sendNalUnit + requestKeyframe()).
            pendingClients.add(out)
            onClientJoined()
            logD(tag) { "client pending (awaiting fresh keyframe), total pending=${pendingClients.size}" }
            while (running && !socket.isClosed) {
                Thread.sleep(300)
            }
        } catch (e: Exception) {
            logE(tag) { "client error: ${e.message}" }
        } finally {
            out?.let { clients.remove(it); pendingClients.remove(it) }
            runCatching { socket.close() }
            logD(tag) { "client gone, remaining=${clients.size}" }
        }
    }

    // Detects whether a NAL unit (Annex-B, starting with 00 00 00 01) is a
    // "config" unit worth caching for late-joining clients: SPS for H264,
    // any of VPS/SPS/PPS for H265. It's fine to cache on any of the three for
    // H265 since VideoEncoder already concatenates VPS+SPS+PPS into a single
    // BUFFER_FLAG_CODEC_CONFIG buffer before calling onNalUnit — in practice
    // the whole blob arrives as one nalBytes value, so this only needs to
    // match its first NAL header.
    private fun isConfigNal(nalBytes: ByteArray): Boolean {
        if (nalBytes.size <= 4) return false
        if (nalBytes[0] != 0x00.toByte() || nalBytes[1] != 0x00.toByte() ||
            nalBytes[2] != 0x00.toByte() || nalBytes[3] != 0x01.toByte()) return false

        return when (codecType) {
            VideoCodecType.H264 -> {
                val type = nalBytes[4].toInt() and 0x1F
                type == 7 // SPS
            }
            VideoCodecType.H265 -> {
                if (nalBytes.size <= 5) return false
                val type = (nalBytes[4].toInt() ushr 1) and 0x3F
                type == 32 || type == 33 || type == 34 // VPS, SPS, PPS
            }
        }
    }

    // Packet format: [ 4 bytes: size ][ data ] — identical for both codecs
    fun sendNalUnit(nalBytes: ByteArray) {
        val isKeyframePacket = isConfigNal(nalBytes)
        if (isKeyframePacket) {
            lastConfigPacket = nalBytes.copyOf()
        }

        val header = ByteBuffer.allocate(4)
            .putInt(nalBytes.size)
            .array()

        // Promote pending (newly-joined) clients on a keyframe: this is the
        // first packet that's actually safe for them to start decoding from,
        // since it's the real start of a GOP rather than a replayed one.
        // Non-keyframe packets are never sent to pending clients — sending a
        // P-frame before a client has seen its own session's keyframe is
        // exactly the bug this fixes.
        if (isKeyframePacket && pendingClients.isNotEmpty()) {
            val promoted = mutableListOf<OutputStream>()
            pendingClients.forEach { out ->
                try {
                    synchronized(out) {
                        out.write(header)
                        out.write(nalBytes)
                        out.flush()
                    }
                    promoted.add(out)
                } catch (_: Exception) {
                    // dead socket — drop it, don't promote
                }
            }
            pendingClients.removeAll(promoted.toSet())
            clients.addAll(promoted)
            if (promoted.isNotEmpty()) logD(tag) { "promoted ${promoted.size} pending client(s) on fresh keyframe" }
        }

        if (clients.isEmpty()) return
        val dead = mutableListOf<OutputStream>()
        clients.forEach { out ->
            try {
                synchronized(out) {
                    out.write(header)
                    out.write(nalBytes)
                    out.flush()
                }
            } catch (_: Exception) {
                dead.add(out)
            }
        }
        if (dead.isNotEmpty()) clients.removeAll(dead.toSet())
    }

    fun stop() {
        running = false
        clients.forEach { runCatching { it.close() } }
        clients.clear()
        pendingClients.forEach { runCatching { it.close() } }
        pendingClients.clear()
        serverSocket?.close()
        serverThread?.interrupt()
        serverThread = null
        // Clear the cached config packet — otherwise a new session (possibly
        // with a different resolution/PPS after VideoEncoder.restartWithSize)
        // would replay a stale VPS/SPS/PPS blob to the first client of the
        // *next* start(), producing decode errors like "PPS id out of range"
        // or "Could not find ref with POC N" on the PC side.
        lastConfigPacket = null
        logD(tag) { "stopped" }
    }
}