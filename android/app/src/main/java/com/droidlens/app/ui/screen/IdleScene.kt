package com.droidlens.app.ui.screen

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.*
import androidx.compose.animation.fadeIn
import androidx.compose.animation.scaleIn
import androidx.compose.animation.slideInVertically
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CameraAlt
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.*
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.clipRect
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.core.content.ContextCompat
import com.droidlens.app.R
import java.util.Calendar
import kotlin.math.sin

// ---------------------------------------------------------------------------
// Palette
// ---------------------------------------------------------------------------

data class ScenePalette(
    val skyTop: Color,
    val skyBottom: Color,
    val orbColor: Color,
    val orbGlow: Color,
    val mountainFar: Color,
    val mountainNear: Color,
    val isNight: Boolean
)

private val PaletteNight   = ScenePalette(Color(0xFF080E1A), Color(0xFF0F1F3D), Color(0xFFE8E4C8), Color(0x40E8E4C8), Color(0xFF1A2540), Color(0xFF0D1525), isNight = true)
private val PaletteDawn    = ScenePalette(Color(0xFF1A0A2E), Color(0xFFE8622A), Color(0xFFFFA040), Color(0x60FFA040), Color(0xFF3D2050), Color(0xFF1A0D28), isNight = false)
private val PaletteMorning = ScenePalette(Color(0xFF1A4A7A), Color(0xFF5BA3D0), Color(0xFFFFD060), Color(0x50FFD060), Color(0xFF2A4A6A), Color(0xFF152535), isNight = false)
private val PaletteDay     = ScenePalette(Color(0xFF1565C0), Color(0xFF42A5F5), Color(0xFFFFF176), Color(0x60FFF176), Color(0xFF1E3A5A), Color(0xFF0D1E2E), isNight = false)
private val PaletteEvening = ScenePalette(Color(0xFF1A0A05), Color(0xFFD4420A), Color(0xFFFF6B20), Color(0x60FF6B20), Color(0xFF3D1A0A), Color(0xFF1A0804), isNight = false)
private val PaletteDusk    = ScenePalette(Color(0xFF0D0520), Color(0xFF4A1A5E), Color(0xFFD4C8F0), Color(0x50D4C8F0), Color(0xFF2A1040), Color(0xFF120820), isNight = true)

fun getScenePalette(): ScenePalette = when (Calendar.getInstance().get(Calendar.HOUR_OF_DAY)) {
    in 0..4   -> PaletteNight
    in 5..7   -> PaletteDawn
    in 8..11  -> PaletteMorning
    in 12..15 -> PaletteDay
    in 16..19 -> PaletteEvening
    else      -> PaletteDusk
}

// ---------------------------------------------------------------------------
// Power save detection — standard Android + MIUI/HyperOS
// ---------------------------------------------------------------------------

/**
 * Returns true when any known battery-saver signal is active.
 * Covers: standard Android, MIUI (Settings.System "POWER_SAVE_MODE_OPEN"),
 * and older MIUI variants ("low_power" / "miui_battery_saver").
 */
private fun Context.isPowerSaveActive(pm: PowerManager): Boolean {
    if (pm.isPowerSaveMode) return true
    fun globalInt(key: String) = try { Settings.Global.getInt(contentResolver, key, 0) } catch (_: Exception) { 0 }
    fun systemInt(key: String) = try { Settings.System.getInt(contentResolver, key, 0) } catch (_: Exception) { 0 }
    return globalInt("low_power")          != 0
            || systemInt("POWER_SAVE_MODE_OPEN") != 0   // confirmed MIUI/HyperOS key
            || globalInt("miui_battery_saver") != 0
            || systemInt("miui_battery_saver") != 0
}

@Composable
fun rememberIsPowerSaveMode(): Boolean {
    val context = LocalContext.current
    val pm = remember { context.getSystemService(Context.POWER_SERVICE) as PowerManager }
    var isPowerSave by remember { mutableStateOf(context.isPowerSaveActive(pm)) }

    val refresh = { isPowerSave = context.isPowerSaveActive(pm) }

    // Watch all relevant Settings keys
    DisposableEffect(Unit) {
        val observer = object : android.database.ContentObserver(Handler(Looper.getMainLooper())) {
            override fun onChange(selfChange: Boolean) = refresh()
        }
        listOf(
            Settings.Global.getUriFor("low_power"),
            Settings.System.getUriFor("POWER_SAVE_MODE_OPEN"),
            Settings.Global.getUriFor("miui_battery_saver"),
            Settings.System.getUriFor("miui_battery_saver")
        ).forEach { context.contentResolver.registerContentObserver(it, false, observer) }
        onDispose { context.contentResolver.unregisterContentObserver(observer) }
    }

    // Broadcast fallback (standard + MIUI)
    DisposableEffect(Unit) {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context, intent: Intent) = refresh()
        }
        val filter = IntentFilter(PowerManager.ACTION_POWER_SAVE_MODE_CHANGED).also {
            it.addAction("miui.intent.action.POWER_SAVE_MODE_CHANGED")
        }
        ContextCompat.registerReceiver(context, receiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
        onDispose { context.unregisterReceiver(receiver) }
    }

    // onResume safety net
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) refresh()
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer) }
    }

    return isPowerSave
}

// ---------------------------------------------------------------------------
// IdleViewport — top-level entry point
// ---------------------------------------------------------------------------

@Composable
fun IdleViewport() {
    val isPowerSave = rememberIsPowerSaveMode()
    val palette = remember { getScenePalette() }
    var visible by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { visible = true }

    val sceneAlpha by animateFloatAsState(
        targetValue = if (visible) 1f else 0f,
        animationSpec = tween(500),
        label = "scene_alpha"
    )

    if (isPowerSave) {
        IdleViewportStatic()
    } else {
        Box(
            Modifier
                .fillMaxSize()
                .graphicsLayer { alpha = sceneAlpha }
        ) {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .graphicsLayer { translationY = -size.height * 0.15f }
            ) {
                IdleViewportScene(palette = palette)
            }

            AnimatedVisibility(
                visible = visible,
                enter = fadeIn(tween(600, delayMillis = 300)),
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .navigationBarsPadding()
                    .padding(bottom = 336.dp)
            ) {
                SceneCaption()
            }
        }
    }
}

// ---------------------------------------------------------------------------
// IdleViewportStatic — power-save fallback
// ---------------------------------------------------------------------------

@Composable
fun IdleViewportStatic() {
    var visible by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { visible = true }

    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
            modifier = Modifier.padding(32.dp)
        ) {
            AnimatedVisibility(
                visible = visible,
                enter = fadeIn(tween(500)) + scaleIn(tween(500))
            ) {
                CameraButton()
            }

            Spacer(Modifier.height(24.dp))

            AnimatedVisibility(
                visible = visible,
                enter = fadeIn(tween(500, delayMillis = 150)) +
                        slideInVertically(tween(500, delayMillis = 150)) { it / 2 }
            ) {
                SceneCaption()
            }
        }
    }
}

@Composable
private fun CameraButton() {
    val interactionSource = remember { MutableInteractionSource() }
    val isPressed by interactionSource.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (isPressed) 0.88f else 1f,
        animationSpec = spring(Spring.DampingRatioMediumBouncy, Spring.StiffnessLow),
        label = "cam_scale"
    )
    Box(
        Modifier
            .size(56.dp)
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clip(RoundedCornerShape(16.dp))
            .background(MaterialTheme.colorScheme.onSurface.copy(alpha = if (isPressed) 0.1f else 0.05f))
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(16.dp)),
        contentAlignment = Alignment.Center
    ) {
        Icon(Icons.Filled.CameraAlt, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(22.dp))
    }
}

@Composable
private fun SceneCaption() {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(stringResource(R.string.ready_to_stream), fontSize = 20.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onBackground)
        Spacer(Modifier.height(4.dp))
        Text(stringResource(R.string.select_config), fontSize = 10.sp, fontWeight = FontWeight.Black, color = MaterialTheme.colorScheme.onSurfaceVariant, letterSpacing = 2.sp)
    }
}

// ---------------------------------------------------------------------------
// IdleViewportScene — animated scene with phone + monitor
// ---------------------------------------------------------------------------

private val EaseInOutSine = CubicBezierEasing(0.37f, 0f, 0.63f, 1f)

@Composable
fun IdleViewportScene(palette: ScenePalette) {
    // Transitions are always created unconditionally (Compose rules).
    // Since this composable is only called when animated=true (isPowerSave=false),
    // we always want full animation here.
    val phoneFloat by rememberInfiniteTransition(label = "phone").animateFloat(
        initialValue = 0f, targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(3800, easing = EaseInOutSine), RepeatMode.Reverse),
        label = "float"
    )
    val mountainShift by rememberInfiniteTransition(label = "mtn").animateFloat(
        initialValue = 0f, targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(12000, easing = EaseInOutSine), RepeatMode.Reverse),
        label = "shift"
    )

    val phoneOffsetDp = phoneFloat * 8f - 4f     // -4dp … +4dp
    val mtnOffsetDp   = mountainShift * 12f - 6f // -6dp … +6dp

    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(32.dp),
            modifier = Modifier.padding(horizontal = 32.dp)
        ) {
            Box(modifier = Modifier.offset(y = phoneOffsetDp.dp)) {
                PhoneIllustration(palette, mtnOffsetDp)
            }
            MonitorIllustration(palette, mtnOffsetDp * 0.6f)
        }
    }
}

// ---------------------------------------------------------------------------
// Device illustrations
// ---------------------------------------------------------------------------

@Composable
fun PhoneIllustration(palette: ScenePalette, mtnShiftDp: Float = 0f) {
    Box(
        modifier = Modifier
            .size(90.dp, 160.dp)
            .clip(RoundedCornerShape(18.dp))
            .background(Color(0xFF1C2333))
            .border(2.dp, Color(0xFF2E3D5C), RoundedCornerShape(18.dp))
    ) {
        Canvas(Modifier.fillMaxSize()) { drawScene(palette, mtnShiftDp * density, isCompact = true) }

        // Notch
        Box(
            Modifier
                .align(Alignment.TopCenter)
                .padding(top = 6.dp)
                .size(22.dp, 6.dp)
                .clip(RoundedCornerShape(3.dp))
                .background(Color(0xFF0D1525))
        )

        Canvas(Modifier.fillMaxSize()) { drawGlare(0.6f, 0.4f) }
    }
}

@Composable
fun MonitorIllustration(palette: ScenePalette, mtnShiftDp: Float = 0f) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Box(
            modifier = Modifier
                .size(140.dp, 90.dp)
                .clip(RoundedCornerShape(10.dp))
                .background(Color(0xFF1A2233))
                .border(2.dp, Color(0xFF2E3D5C), RoundedCornerShape(10.dp))
        ) {
            Canvas(Modifier.fillMaxSize()) { drawScene(palette, mtnShiftDp * density, isCompact = false) }
            Canvas(Modifier.fillMaxSize()) { drawGlare(0.5f, 0.35f) }
        }
        Box(Modifier.size(12.dp, 14.dp).background(Color(0xFF1E2C42)))
        Box(Modifier.size(50.dp, 6.dp).clip(RoundedCornerShape(3.dp)).background(Color(0xFF1E2C42)))
    }
}

// ---------------------------------------------------------------------------
// Canvas draw helpers
// ---------------------------------------------------------------------------

private fun DrawScope.drawGlare(endXFraction: Float, endYFraction: Float) {
    drawRect(
        brush = Brush.linearGradient(
            colors = listOf(Color.White.copy(alpha = 0.04f), Color.Transparent),
            start = Offset.Zero,
            end   = Offset(size.width * endXFraction, size.height * endYFraction)
        )
    )
}

private fun DrawScope.drawScene(palette: ScenePalette, mtnShiftPx: Float, isCompact: Boolean) {
    val (w, h) = size.width to size.height

    // Sky
    drawRect(brush = Brush.verticalGradient(listOf(palette.skyTop, palette.skyBottom), 0f, h))

    // Orb
    val orbR = if (isCompact) w * 0.18f else w * 0.14f
    val orbCenter = Offset(w * 0.62f, h * 0.35f)
    drawCircle(
        brush = Brush.radialGradient(listOf(palette.orbGlow, Color.Transparent), orbCenter, orbR * 2.2f),
        radius = orbR * 2.2f, center = orbCenter
    )
    drawCircle(color = palette.orbColor, radius = orbR, center = orbCenter)

    if (palette.isNight) {
        val darken = Color.Black.copy(alpha = 0.12f)
        drawCircle(darken, orbR * 0.22f, Offset(orbCenter.x - orbR * 0.28f, orbCenter.y - orbR * 0.18f))
        drawCircle(darken, orbR * 0.13f, Offset(orbCenter.x + orbR * 0.30f, orbCenter.y + orbR * 0.25f))
        drawCircle(darken, orbR * 0.10f, Offset(orbCenter.x - orbR * 0.10f, orbCenter.y + orbR * 0.38f))
    }

    // Mountains
    clipRect(0f, 0f, w, h) {
        drawMountainRange(palette.mountainFar,  h * 0.72f, h * 0.28f, mtnShiftPx * 0.40f, if (isCompact) 4 else 5)
        drawMountainRange(palette.mountainNear, h * 0.88f, h * 0.38f, mtnShiftPx * 0.15f, if (isCompact) 3 else 4)
    }
}

private fun DrawScope.drawMountainRange(
    color: Color, baselineY: Float, peakHeight: Float, shiftX: Float, peaks: Int
) {
    val w = size.width
    val h = size.height
    val segW = w * 1.4f / peaks

    val path = Path().apply {
        moveTo(-w * 0.2f + shiftX, h)
        for (i in 0..peaks) {
            val peakX = -w * 0.1f + shiftX + i * segW
            lineTo(peakX, baselineY - peakHeight * (0.75f + 0.25f * sin(i * 1.7f)))
            lineTo(peakX + segW * 0.5f, baselineY)
        }
        lineTo(w * 1.2f, h)
        close()
    }
    drawPath(path, color)
}