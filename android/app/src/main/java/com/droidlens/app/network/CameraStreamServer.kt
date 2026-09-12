package com.droidlens.app.network

import android.content.Context
import com.droidlens.app.logD
import com.droidlens.app.logE
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.CopyOnWriteArrayList

class CameraStreamServer(private val port: Int = 7878) {

    constructor(@Suppress("UNUSED_PARAMETER") context: Context, port: Int) : this(port)

    private var serverSocket: ServerSocket? = null
    private val clients = CopyOnWriteArrayList<OutputStream>()
    @Volatile private var running = false
    private var serverThread: Thread? = null

    fun start() {
        if (running) return
        running = true
        logD("DroidLens") { "Server started on port $port" }
        serverThread = Thread {
            try {
                serverSocket = ServerSocket(port).also { it.soTimeout = 1000 }
                while (running) {
                    try {
                        val socket = serverSocket?.accept() ?: break
                        logD("DroidLens") { "Client connected: ${socket.inetAddress}" }
                        Thread { handleClient(socket) }.start()
                    } catch (_: java.net.SocketTimeoutException) {
                        // normal — just check running and keep waiting
                    } catch (e: Exception) {
                        if (running) logE("DroidLens") { "Accept error: ${e.message}" }
                    }
                }
            } catch (e: Exception) {
                logD("DroidLens") { "Server ended: ${e.message}" }
            }
        }.also { it.start() }
    }

    private fun handleClient(socket: Socket) {
        var out: OutputStream? = null
        try {
            logD("DroidLens") { "handleClient start" }
            out = socket.getOutputStream()
            out.write(
                ("HTTP/1.1 200 OK\r\n" +
                        "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n" +
                        "Cache-Control: no-cache\r\n" +
                        "Connection: keep-alive\r\n\r\n").toByteArray()
            )
            out.flush()
            clients.add(out)
            logD("DroidLens") { "Client ADDED, total=${clients.size}" }

            while (running && !socket.isClosed) {
                Thread.sleep(300)
            }
        } catch (e: Exception) {
            logE("DroidLens") { "handleClient error: ${e.message}" }
        } finally {
            out?.let { clients.remove(it) }
            runCatching { socket.close() }
            logD("DroidLens") { "Client gone, remaining=${clients.size}" }
        }
    }

    fun sendFrame(jpegBytes: ByteArray) {
        if (clients.isEmpty()) return
        val frame = ("--frame\r\n" +
                "Content-Type: image/jpeg\r\n" +
                "Content-Length: ${jpegBytes.size}\r\n\r\n").toByteArray()
        val footer = "\r\n".toByteArray()
        val dead = mutableListOf<OutputStream>()
        clients.forEach { out ->
            try {
                synchronized(out) {
                    out.write(frame)
                    out.write(jpegBytes)
                    out.write(footer)
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
        serverSocket?.close()
        serverThread?.interrupt()
        serverThread = null
        logD("DroidLens") { "Server stopped" }
    }
}