package com.droidlens.app

import android.app.Activity
import android.app.Application
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import java.lang.ref.WeakReference
import androidx.camera.core.CameraSelector
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import java.net.Inet4Address
import java.net.NetworkInterface
import android.os.PowerManager
import com.droidlens.app.camera.VideoCodecType
import com.droidlens.app.camera.VideoEncoder
import com.droidlens.app.data.SavedSettings
import com.droidlens.app.data.SettingsRepository
import com.droidlens.app.ui.screen.PanelPosition
import com.droidlens.app.ui.screen.CameraStats
import com.droidlens.app.network.CameraStreamServer
import com.droidlens.app.network.CommandServer
import com.droidlens.app.network.VideoStreamServer
import com.droidlens.app.network.NsdHelper
@Suppress("unused")
enum class Quality(val label: String, val jpegQuality: Int, val h264Mbps: Int, val h265Mbps: Int) {
    // H265 typically achieves comparable quality at roughly 60-70% of the H264
    // bitrate, so its column is set lower rather than mirroring h264Mbps.
    LOW("Low", 50, 1, 1),
    MEDIUM("Medium", 75, 3, 2),
    HIGH("High", 92, 5, 3);
    fun next() = entries[(entries.indexOf(this) + 1) % entries.size]
}

enum class Codec(val label: String) {
    MJPEG("MJPEG"),
    H264("H.264"),
    H265("H.265");
    fun next() = entries[(entries.indexOf(this) + 1) % entries.size]
}

// Selectable only from the PC client (via CommandServer's "set_resolution");
// there is no on-device UI for it. The phone just persists whatever was last set.
// SD is 3:2 (720x480), not 16:9 like HD/FHD — deliberately NOT 854x480 or 640x360
// (both true 16:9 at this tier). Neither exists in android.scaler.available-
// StreamConfigurations on real hardware (confirmed via Camera2 dumpsys across
// multiple devices, not just one), so CameraX's ResolutionStrategy
// (FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER, see CameraPreview.kt) was silently
// upscaling the request to 1280x720 — SD never actually took effect. 720x480 is
// the closest size below HD that's actually present in the hardware's stream
// configuration list, at the cost of a visible aspect shift when switching
// to/from HD/FHD (~15.6% off 16:9, vs ~25% for the 4:3 640x480 alternative).
enum class Resolution(val width: Int, val height: Int, val label: String) {
    SD(720, 480, "480p"),
    HD(1280, 720, "720p"),
    FHD(1920, 1080, "1080p")
}

data class StreamState(
    val isStreaming: Boolean = false,
    val ipAddress: String = "—",
    val port: Int = 7878,
    val selectedCamera: CameraSelector = CameraSelector.DEFAULT_BACK_CAMERA,
    val isFrontCamera: Boolean = false,
    val quality: Quality = Quality.MEDIUM,
    val codec: Codec = Codec.MJPEG,
    val resolution: Resolution = Resolution.HD,
    val isBlackScreen: Boolean = false,
    // Timer moved to PC client — synced locally there instead of ticking on-device.
    // val elapsedSeconds: Int = 0,
    val isFlashlightOn: Boolean = false,
    val panelPosition: PanelPosition = PanelPosition.RIGHT,
    val isCameraSwitching: Boolean = false,
    val cameraStats: CameraStats? = null,
    val isClientConnected: Boolean = false,
    // true = the stream is actually going out (either Wi-Fi was available at
    // start time and still is, or the phone is connected over USB); false =
    // this is a local preview only — TCP servers are not running, no bytes
    // are sent anywhere.
    val isNetworkAvailable: Boolean = true
)

class StreamViewModel(application: Application) : AndroidViewModel(application) {

    companion object {
        // Must match CommandServer's port — kept as an explicit constant here
        // (rather than reading commandServer.port, which doesn't exist as a
        // public property) so the mDNS announcement below can never silently
        // drift out of sync with the port CommandClient.cs actually dials.
        private const val COMMAND_PORT = 7879
    }

    private val _state = MutableStateFlow(StreamState())
    val state = _state.asStateFlow()

    val server = CameraStreamServer(application, 7878)
    val h264Server: VideoStreamServer = VideoStreamServer(
        VideoCodecType.H264, 7880,
        onClientJoined = { h264Encoder.requestKeyframe() }
    )
    val h265Server: VideoStreamServer = VideoStreamServer(
        VideoCodecType.H265, 7881,
        onClientJoined = { h265Encoder.requestKeyframe() }
    )
    // Fixes bug 2: encoder initializes on first frame (in feedFrame()), so no hardcoded size;
    // size is obtained from the real frame.
    val h264Encoder: VideoEncoder = VideoEncoder(
        codecType = VideoCodecType.H264,
        bitrateMbps = _state.value.quality.h264Mbps,
        onNalUnit = { nal -> h264Server.sendNalUnit(nal) }
    )
    val h265Encoder: VideoEncoder = VideoEncoder(
        codecType = VideoCodecType.H265,
        bitrateMbps = _state.value.quality.h265Mbps,
        onNalUnit = { nal -> h265Server.sendNalUnit(nal) }
    )

    val commandServer = CommandServer(
        context = application,
        port = COMMAND_PORT,
        onCommand = { action, value -> handleCommand(action, value) },
        getCodec = { _state.value.codec.name },
        getState = {
            mapOf(
                "quality" to _state.value.quality.name,
                "resolution" to _state.value.resolution.name,
                "isFrontCamera" to _state.value.isFrontCamera,
                "isFlashlightOn" to _state.value.isFlashlightOn,
                "isBlackScreen" to _state.value.isBlackScreen,
                "isReverseLandscape" to _isReverseLandscape.value,
                "isStreaming" to _state.value.isStreaming
            )
        },
        onClientConnectionChanged = { connected ->
            _state.value = _state.value.copy(isClientConnected = connected)
        }
    )

    private val settings = SettingsRepository(application)
    private var nsdHelper: NsdHelper? = null
    // Timer moved to PC client.
    // private var timerJob: kotlinx.coroutines.Job? = null

    private val _isLandscape = MutableStateFlow(false)
    val isLandscape = _isLandscape.asStateFlow()

    private val _isReverseLandscape = MutableStateFlow(false)
    val isReverseLandscape = _isReverseLandscape.asStateFlow()

    // true if the last isReverseLandscape update came from the sensor (auto-detect),
    // false if it came from a manual Rotate press (including via CommandServer).
    @Volatile var isReverseLandscapeFromSensor: Boolean = false
        private set

    private var wakeLock: PowerManager.WakeLock? = null
    private var activityRef: WeakReference<Activity>? = null
    var camera: androidx.camera.core.Camera? = null

    // ── Network watch (Wi-Fi vs mobile data) ─────────────────────────────
    private var connectivityManager: ConnectivityManager? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    // Guards against duplicate start()/stop() calls on frequent network callbacks
    private var networkServersRunning = false

    private fun isOnWifi(context: Context): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val network = cm.activeNetwork ?: return false
        val caps = cm.getNetworkCapabilities(network) ?: return false
        return caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
    }

    // True while the phone is physically connected via USB (charging or data).
    // Used so the mobile-data-saving check below doesn't also kill the stream
    // when the client is actually connected over an adb USB forward, which
    // never touches mobile data in the first place.
    private fun isUsbConnected(context: Context): Boolean {
        val batteryIntent = context.registerReceiver(
            null,
            IntentFilter(Intent.ACTION_BATTERY_CHANGED)
        )
        val plugged = batteryIntent?.getIntExtra(android.os.BatteryManager.EXTRA_PLUGGED, -1) ?: -1
        return plugged == android.os.BatteryManager.BATTERY_PLUGGED_USB
    }

    // Single decision point for whether the TCP servers are allowed to run:
    // either real Wi-Fi is up, or the phone is tethered over USB (adb forward
    // case), which never spends mobile data regardless of the active network.
    private fun shouldServersRun(context: Context): Boolean =
        isOnWifi(context) || isUsbConnected(context)

    // Starts the TCP servers (actual network streaming). Only called when
    // Wi-Fi is available. Idempotent — safe to call more than once.
    private fun startNetworkServers(@Suppress("UNUSED_PARAMETER") context: Context) {
        if (networkServersRunning) return
        networkServersRunning = true
        server.start()
        h264Server.start()
        h265Server.start()
    }

    // Stops the TCP servers when Wi-Fi disappears (or on stopStreaming).
    // After this, sendFrame/sendNalUnit have no connected clients, so
    // no mobile data is consumed — only the local preview remains.
    private fun stopNetworkServers() {
        if (!networkServersRunning) return
        networkServersRunning = false
        server.stop()
        h264Server.stop()
        h265Server.stop()
    }

    private fun registerNetworkWatch(context: Context) {
        if (networkCallback != null) return
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        connectivityManager = cm

        val request = NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()

        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                handleNetworkChange(context, shouldServersRun(context))
            }

            override fun onLost(network: Network) {
                handleNetworkChange(context, shouldServersRun(context))
            }

            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                handleNetworkChange(context, shouldServersRun(context))
            }
        }
        networkCallback = callback
        cm.registerNetworkCallback(request, callback)
    }

    private fun unregisterNetworkWatch() {
        val cm = connectivityManager ?: return
        val callback = networkCallback ?: return
        runCatching { cm.unregisterNetworkCallback(callback) }
        networkCallback = null
        connectivityManager = null
    }

    // Single decision point for whether to stream over the network during an active session.
    // "shouldRun" covers both real Wi-Fi and USB tethering — see shouldServersRun().
    private fun handleNetworkChange(context: Context, shouldRun: Boolean) {
        if (!_state.value.isStreaming) return
        if (shouldRun == _state.value.isNetworkAvailable) return // no change

        _state.value = _state.value.copy(isNetworkAvailable = shouldRun)
        if (shouldRun) {
            startNetworkServers(context)
        } else {
            stopNetworkServers()
        }
    }

    fun bindActivity(activity: Activity) {
        activityRef = WeakReference(activity)
    }

    init {

        _state.value = _state.value.copy(ipAddress = getLocalIpAddress())
        commandServer.start()
        // Announce presence over mDNS as soon as the command channel is up —
        // this is what lets the desktop client find the phone before any
        // stream has started (see NsdHelper.kt / MdnsDiscovery.cs on the PC
        // side). Deliberately registers COMMAND_PORT (7879), not the MJPEG
        // port in _state.value.port (7878) — the PC connects to the command
        // channel first and only opens the video ports afterward.
        nsdHelper = NsdHelper(getApplication())
        nsdHelper?.register(COMMAND_PORT)
        viewModelScope.launch {
            val saved = settings.settingsFlow.first()
            _state.value = _state.value.copy(
                quality = saved.quality,
                codec = saved.codec,
                resolution = saved.resolution,
                isFrontCamera = saved.isFrontCamera,
                selectedCamera = if (saved.isFrontCamera)
                    CameraSelector.DEFAULT_FRONT_CAMERA
                else
                    CameraSelector.DEFAULT_BACK_CAMERA,
                panelPosition = saved.panelPosition
            )
        }
    }

    fun startStreaming(context: Context) {
        val pm = context.getSystemService(Context.POWER_SERVICE) as PowerManager
        @Suppress("DEPRECATION")
        wakeLock = pm.newWakeLock(
            PowerManager.SCREEN_BRIGHT_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP,
            "DroidLens::StreamWakeLock"
        )
        wakeLock?.acquire(10 * 60 * 60 * 1000L)

        val serversShouldRun = shouldServersRun(context)

        if (serversShouldRun) {
            startNetworkServers(context)
        }
        registerNetworkWatch(context)

        _isLandscape.value = true
        _state.value = _state.value.copy(
            isStreaming = true,
            isNetworkAvailable = serversShouldRun
            // elapsedSeconds = 0
        )
        // startTimer()
    }

    @Suppress("SourceLockedOrientationActivity")
    fun stopStreaming() {
        stopNetworkServers()
        unregisterNetworkWatch()
        // timerJob?.cancel()
        _isLandscape.value = false
        _isReverseLandscape.value = false
        _state.value = _state.value.copy(isStreaming = false)
        // elapsedSeconds = 0
        // Check isHeld before release to avoid RuntimeException
        if (wakeLock?.isHeld == true) wakeLock?.release()
        wakeLock = null
        if (_state.value.isFlashlightOn) {
            if (_state.value.isFrontCamera) {
                val activity = activityRef?.get()
                activity?.runOnUiThread {
                    val params = activity.window.attributes
                    params.screenBrightness = -1f
                    activity.window.attributes = params
                }
            } else {
                camera?.cameraControl?.enableTorch(false)
            }
            _state.value = _state.value.copy(isFlashlightOn = false)
        }
    }

    fun clearCameraSwitching() {
        viewModelScope.launch {
            // Give the first phase of the flip animation (0°→90°) time to complete
            // before triggering the second phase (90°→final position)
            kotlinx.coroutines.delay(400)
            _state.value = _state.value.copy(isCameraSwitching = false)
        }
    }

    fun flipCamera() {
        val isFront = !_state.value.isFrontCamera
        _state.value = _state.value.copy(
            isFrontCamera = isFront,
            selectedCamera = if (isFront) CameraSelector.DEFAULT_FRONT_CAMERA
            else CameraSelector.DEFAULT_BACK_CAMERA,
            isCameraSwitching = true
        )
        saveCurrentSettings()
    }

    fun cycleQuality() {
        _state.value = _state.value.copy(quality = _state.value.quality.next())
        saveCurrentSettings()
        applyBitrateForActiveCodec()
    }

    // Pushes the current quality's bitrate to whichever encoder is actually
    // active. Only one of h264Encoder/h265Encoder is ever feeding a camera
    // frame at a time (CameraPreview branches on state.codec), so updating
    // the inactive one is harmless but pointless — this keeps it targeted.
    private fun applyBitrateForActiveCodec() {
        when (_state.value.codec) {
            Codec.H264 -> h264Encoder.updateBitrate(_state.value.quality.h264Mbps)
            Codec.H265 -> h265Encoder.updateBitrate(_state.value.quality.h265Mbps)
            Codec.MJPEG -> Unit
        }
    }

    fun cycleCodec() {
        _state.value = _state.value.copy(codec = _state.value.codec.next())
        saveCurrentSettings()
    }

    fun toggleBlackScreen() {
        _state.value = _state.value.copy(isBlackScreen = !_state.value.isBlackScreen)
    }
    fun toggleFlashlight() {
        val isOn = !_state.value.isFlashlightOn
        _state.value = _state.value.copy(isFlashlightOn = isOn)

        if (_state.value.isFrontCamera) {
            val activity = activityRef?.get() ?: return
            // window.attributes must be changed on the main thread;
            // toggleFlashlight() can be called from a background thread (CommandServer)
            activity.runOnUiThread {
                val params = activity.window.attributes
                params.screenBrightness = if (isOn) 1f else -1f
                activity.window.attributes = params
            }
        } else {
            camera?.cameraControl?.enableTorch(isOn)
        }
    }

    fun toggleOrientation() {
        // Toggles only between landscape and reverse landscape — portrait is left alone
        isReverseLandscapeFromSensor = false
        _isReverseLandscape.value = !_isReverseLandscape.value
    }

    // Called only from physical-orientation auto-detection (OrientationEventListener in MainActivity).
    // Ignored once the user has manually pressed Rotate at least once (enforced by MainActivity).
    fun setReverseLandscapeFromSensor(reverse: Boolean) {
        if (_isReverseLandscape.value == reverse) return
        isReverseLandscapeFromSensor = true
        _isReverseLandscape.value = reverse
    }

    @Suppress("unused")
    fun setPanelPosition(position: PanelPosition) {
        _state.value = _state.value.copy(panelPosition = position)
        saveCurrentSettings()
    }

    fun updateCameraStats(stats: CameraStats) {
        _state.value = _state.value.copy(cameraStats = stats)
    }

    // Timer moved to PC client — it now tracks elapsed time locally
    // based on when it receives the stream, instead of Android ticking it.
    // private fun startTimer() {
    //     timerJob = viewModelScope.launch {
    //         while (true) {
    //             kotlinx.coroutines.delay(1000)
    //             _state.value = _state.value.copy(
    //                 elapsedSeconds = _state.value.elapsedSeconds + 1
    //             )
    //         }
    //     }
    // }

    private fun getLocalIpAddress(): String {
        try {
            NetworkInterface.getNetworkInterfaces()?.toList()?.forEach { iface ->
                iface.inetAddresses?.toList()?.forEach { addr ->
                    if (!addr.isLoopbackAddress && addr is Inet4Address) {
                        return addr.hostAddress ?: "—"
                    }
                }
            }
        } catch (_: Exception) {}
        return "—"
    }

    private fun handleCommand(action: String, value: String?) {
        when (action) {
            "set_quality" -> {
                val q = Quality.entries.find { it.name.equals(value, ignoreCase = true) } ?: return
                _state.value = _state.value.copy(quality = q)
                saveCurrentSettings()
                applyBitrateForActiveCodec()
            }
            "set_codec" -> {
                val co = Codec.entries.find { it.name.equals(value, ignoreCase = true) } ?: return
                _state.value = _state.value.copy(codec = co)
                saveCurrentSettings()
            }
            "set_resolution" -> {
                val res = Resolution.entries.find { it.name.equals(value, ignoreCase = true) } ?: return
                _state.value = _state.value.copy(resolution = res)
                saveCurrentSettings()
            }
            "flip_camera" -> flipCamera()
            "black_screen" -> toggleBlackScreen()
            "flashlight" -> toggleFlashlight()
            "rotate" -> toggleOrientation()
            "stop" -> stopStreaming()
            "start" -> {
                val ctx = getApplication<Application>()
                startStreaming(ctx)
            }
        }
    }

    private fun saveCurrentSettings() {
        viewModelScope.launch {
            settings.save(
                SavedSettings(
                    quality = _state.value.quality,
                    codec = _state.value.codec,
                    resolution = _state.value.resolution,
                    isFrontCamera = _state.value.isFrontCamera,
                    panelPosition = _state.value.panelPosition
                )
            )
        }
    }

    override fun onCleared() {
        super.onCleared()
        commandServer.stop()
        nsdHelper?.unregister()
        nsdHelper = null
        unregisterNetworkWatch()
        stopStreaming()
    }
}