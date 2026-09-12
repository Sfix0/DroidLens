package com.droidlens.app.network

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import com.droidlens.app.logD
import com.droidlens.app.logE
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.Executors

class CommandServer(
    private val context: Context,
    private val port: Int = 7879,
    private val onCommand: (String, String?) -> Unit,
    private val getCodec: () -> String = { "MJPEG" },
    private val getState: () -> Map<String, Any> = { emptyMap() }, // додати
    private val onClientConnectionChanged: (Boolean) -> Unit = {}
) {
    private var serverSocket: ServerSocket? = null
    private val executor = Executors.newCachedThreadPool()
    @Volatile private var running = false
    private var clientSocket: Socket? = null

    private var serverThread: Thread? = null
    private var clientThread: Thread? = null

    @Volatile private var lastKnownTemp: Double? = null

    fun start() {
        if (running) return
        running = true
        logD("DroidLens") { "CommandServer started on port $port" }
        serverThread = Thread {
            try {
                serverSocket = ServerSocket(port)
                while (running) {
                    val socket = serverSocket?.accept() ?: break
                    logD("DroidLens") { "Connection accepted: ${socket.inetAddress}" }
                    clientThread = Thread { handleClient(socket) }
                    clientThread?.start()
                }
            } catch (e: Exception) {
                logD("DroidLens") { "CommandServer ended: ${e.message}" }
            }
        }.also { it.start() }
    }

    private fun handleClient(socket: Socket) {
        var recognizedAsClient = false
        // Per-connection, NOT a shared field: a global flag meant a `ping_stop` from
        // one connection (e.g. the PC closing a probe/reconnect) silenced pong for
        // EVERY connection — the real client would stop ponging and its watchdog
        // would force-drop at 10s. That's exactly the "phone disconnects a few
        // seconds after the first launch, second connection is fine" bug.
        var pingEnabled = false
        try {
            // Send device_info on connection
            sendDeviceInfo(socket)
            sendBatteryStatus(socket)
            sendNetworkStatus(socket)

            val reader = BufferedReader(InputStreamReader(socket.getInputStream()))

            // Start status update timers
            val statusJob = startStatusUpdates(socket)

            while (running && !socket.isClosed) {
                val line = reader.readLine() ?: break
                try {
                    val json = JSONObject(line)
                    if (json.getString("type") == "cmd") {
                        // Only a real client sends commands (a probe just reads and
                        // disconnects), so this is where we know `socket` is the one
                        // that should receive event-driven pushes like the power receiver.
                        if (!recognizedAsClient) {
                            recognizedAsClient = true
                            clientSocket = socket
                            onClientConnectionChanged(true)
                            logD("DroidLens") { "Command client recognized: ${socket.inetAddress}" }
                        }
                        val action = json.getString("action")
                        val value = if (json.has("value")) json.getString("value") else null
                        logD("DroidLens") { "Command received: $action value=$value" }
                        when (action) {
                            "ping_start" -> pingEnabled = true
                            "ping_stop"  -> pingEnabled = false
                            "ping"       -> if (pingEnabled) sendPong(socket, value)
                            else         -> onCommand(action, value)
                        }
                    }
                } catch (e: Exception) {
                    logE("DroidLens") { "Command parse error: ${e.message}" }
                }
            }

            statusJob.cancel()
        } catch (e: Exception) {
            logD("DroidLens") { "Command client error: ${e.message}" }
        } finally {
            runCatching { socket.close() }
            if (recognizedAsClient) {
                if (clientSocket === socket) clientSocket = null
                onClientConnectionChanged(false)
                logD("DroidLens") { "Command client disconnected" }
            }
        }
    }

    // Event-driven charging updates via BroadcastReceiver (ACTION_POWER_CONNECTED/
    // DISCONNECTED, then ACTION_BATTERY_CHANGED) turned out unreliable on some OEM
    // firmware (e.g. MIUI silently drops delivery to apps without "Autostart"
    // permission enabled, even while the app is in the foreground). Rather than
    // depend on OEM-specific permissions the user has to manually grant, we fall
    // back to simple, reliable polling — see startStatusUpdates() below.

    private fun sendDeviceInfo(socket: Socket) {
        val modelName = DeviceModelResolver.resolve()

        val json = JSONObject().apply {
            put("type", "device_info")
            put("model", modelName)
            put("android", Build.VERSION.RELEASE)
        }
        sendJson(socket, json)
    }

    private fun sendNetworkStatus(socket: Socket) {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val activeNetwork = cm.activeNetwork

        // NetworkCapabilities.transportInfo (-> WifiInfo) needs API 29+; below that
        // (minSdk 26) it doesn't exist, so we fall back to the deprecated but only
        // available WifiManager.connectionInfo on those older versions.
        val rssi: Int
        val signalLevel: Int
        val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val capabilities = activeNetwork?.let { cm.getNetworkCapabilities(it) }
            val wifiInfo = capabilities?.transportInfo as? WifiInfo
            rssi = wifiInfo?.rssi ?: -127
            signalLevel = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                // Instance method, API 30+ — NOT the static overload, which is the
                // deprecated one. Must be called on a WifiManager instance.
                wifiManager.calculateSignalLevel(rssi)
            } else {
                @Suppress("DEPRECATION")
                WifiManager.calculateSignalLevel(rssi, 5)
            }
        } else {
            @Suppress("DEPRECATION")
            val connectionInfo = wifiManager.connectionInfo
            rssi = connectionInfo?.rssi ?: -127
            @Suppress("DEPRECATION")
            signalLevel = WifiManager.calculateSignalLevel(rssi, 5)
        }

        val ip = cm.getLinkProperties(activeNetwork)
            ?.linkAddresses?.firstOrNull { it.address.hostAddress?.contains(":") == false }
            ?.address?.hostAddress ?: ""

        val json = JSONObject().apply {
            put("type", "net")
            put("sig", signalLevel)
            put("ip", ip)
        }
        sendJson(socket, json)
    }

    private fun startStatusUpdates(socket: Socket): Job {
        val scope = CoroutineScope(Dispatchers.IO)
        return scope.launch {
            var batteryTick = 0
            while (isActive && !socket.isClosed) {
                // Stream status кожні 2 секунди
                sendStreamStatus(socket)

                // Battery/net every 10 seconds. No reliable event-driven signal
                // exists across OEMs (see note above), so we poll reasonably
                // often instead — still cheap, and far better UX than 60s.
                batteryTick++
                if (batteryTick >= 5) {
                    sendBatteryStatus(socket)
                    sendNetworkStatus(socket)
                    batteryTick = 0
                }

                delay(2000)
            }
        }
    }

    private fun sendStreamStatus(socket: Socket) {
        val state = getState()
        val isReverse = state["isReverseLandscape"] as? Boolean ?: false
        val rotation = if (isReverse) 180 else 0
        val json = JSONObject().apply {
            put("type", "stream_status")
            put("active", state["isStreaming"] as? Boolean ?: false)
            put("rotation", rotation)
            put("codec", getCodec())
            put("quality", state["quality"] ?: "")
            put("resolution", state["resolution"] ?: "")
            put("front_camera", state["isFrontCamera"] ?: false)
            put("flashlight", state["isFlashlightOn"] ?: false)
            put("black_screen", state["isBlackScreen"] ?: false)
        }
        sendJson(socket, json)
    }

    private fun sendBatteryStatus(socket: Socket) {
        val bm = context.getSystemService(Context.BATTERY_SERVICE) as BatteryManager
        val batteryPct = bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)

        // BatteryManager.isCharging can return a stale cached value right after a
        // plug/unplug event on some devices. The sticky intent's BATTERY_STATUS /
        // EXTRA_PLUGGED are the reliable source, same as temp below.
        val batteryIntent = context.applicationContext.registerReceiver(
            null,
            IntentFilter(Intent.ACTION_BATTERY_CHANGED)
        )

        val status = batteryIntent?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
        val plugged = batteryIntent?.getIntExtra(BatteryManager.EXTRA_PLUGGED, -1) ?: -1
        val isCharging = status == BatteryManager.BATTERY_STATUS_CHARGING ||
                status == BatteryManager.BATTERY_STATUS_FULL ||
                plugged != 0

        val temp = batteryIntent?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Int.MIN_VALUE) ?: Int.MIN_VALUE

        val tempValue: Double? = if (temp == Int.MIN_VALUE) {
            lastKnownTemp // fallback на останнє відоме значення, якщо sticky-intent ще не готовий
        } else {
            (temp / 10.0).also { lastKnownTemp = it }
        }

        val json = JSONObject().apply {
            put("type", "battery")
            put("level", batteryPct)
            put("charging", isCharging)
            put("temp", tempValue ?: JSONObject.NULL)
        }
        sendJson(socket, json)
    }

    private fun sendPong(socket: Socket, timestamp: String?) {
        val json = JSONObject().apply {
            put("type", "pong")
            put("ts", timestamp ?: "")
        }
        sendJson(socket, json)
    }

    fun sendJson(socket: Socket, json: JSONObject) {
        try {
            if (!socket.isClosed) {
                val out = socket.getOutputStream()
                synchronized(out) {
                    out.write((json.toString() + "\n").toByteArray())
                    out.flush()
                }
            }
        } catch (e: Exception) {
            logD("DroidLens") { "Send error: ${e.message}" }
        }
    }

    fun stop() {
        running = false
        runCatching { clientSocket?.close() }
        clientSocket = null
        runCatching { serverSocket?.close() }
        serverSocket = null
        executor.shutdownNow()
        logD("DroidLens") { "CommandServer stopped" }
    }
}