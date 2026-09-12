package com.droidlens.app.ui.screen

import androidx.compose.animation.*
import androidx.compose.animation.core.*
import androidx.compose.foundation.*
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import com.droidlens.app.R
import kotlinx.coroutines.delay


// ---------------------------------------------------------------------------
// Cascade enter animation — shared fade + slide-up used to stagger idle-state
// elements (panel, logo, IP badge) so they appear one after another instead
// of all at once. Each element passes its own delayMillis to control order.
// ---------------------------------------------------------------------------

@Composable
fun Modifier.cascadeEnter(
    delayMillis: Int,
    startOffsetY: Float = 24f
): Modifier {
    var visible by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        delay(delayMillis.toLong())
        visible = true
    }
    val alpha by animateFloatAsState(
        targetValue = if (visible) 1f else 0f,
        animationSpec = tween(600, easing = FastOutSlowInEasing),
        label = "cascade_alpha"
    )
    val offsetY by animateFloatAsState(
        targetValue = if (visible) 0f else startOffsetY,
        animationSpec = tween(700, easing = EaseOutCubic),
        label = "cascade_offset_y"
    )
    return this.graphicsLayer {
        this.alpha = alpha
        translationY = offsetY
    }
}

// ---------------------------------------------------------------------------
// Header (app title / LIVE badge + timer)
// ---------------------------------------------------------------------------

@Composable
fun ViewportHeader(
    isStreaming: Boolean,
    // Timer moved to PC client — no longer tracked on-device.
    // elapsedSeconds: Int,
    ipAddress: String = "",
    port: Int = 0,
    isClientConnected: Boolean = false,
    isNetworkAvailable: Boolean = true
) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(start = 24.dp, end = 24.dp, top = 16.dp, bottom = 32.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        // Left: logo (idle) or LIVE/PREVIEW badge (streaming)
        if (!isStreaming) {
            Row(
                modifier = Modifier
                    .cascadeEnter(delayMillis = 0)
                    .background(Color(0xFF0E0D14).copy(alpha = 0.65f), RoundedCornerShape(20.dp))
                    .padding(horizontal = 12.dp, vertical = 6.dp),
                verticalAlignment = Alignment.Bottom
            ) {
                Text(
                    "Droid",
                    fontSize = 22.sp,
                    fontWeight = FontWeight.Black,
                    color = Color.White,
                    letterSpacing = (-0.5).sp
                )
                Text(
                    "Lens",
                    fontSize = 22.sp,
                    fontWeight = FontWeight.Black,
                    color = MaterialTheme.colorScheme.primary,
                    letterSpacing = (-0.5).sp
                )
            }
        } else {
            LiveBadge(isNetworkAvailable = isNetworkAvailable)
        }

        // Right: IP badge (idle only) — timer moved to PC client, nothing shown while streaming
        if (!isStreaming && ipAddress.isNotEmpty()) {
            IpBadge(
                ipAddress = ipAddress,
                port = port,
                isClientConnected = isClientConnected,
                modifier = Modifier.cascadeEnter(delayMillis = 150)
            )
        }
    }
}

@Composable
fun LiveBadge(isNetworkAvailable: Boolean = true) {
    val infiniteTransition = rememberInfiniteTransition(label = "pulse")
    val alpha by infiniteTransition.animateFloat(
        initialValue = 1f, targetValue = 0.4f,
        animationSpec = infiniteRepeatable(tween(900), RepeatMode.Reverse),
        label = "dot"
    )
    val accentColor = if (isNetworkAvailable) MaterialTheme.colorScheme.error else Color(0xFFFFA040)
    Row(
        Modifier
            .background(Color(0xFF0E0D14).copy(alpha = 0.65f), RoundedCornerShape(20.dp))
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        Box(
            Modifier
                .size(6.dp)
                .clip(RoundedCornerShape(3.dp))
                .background(accentColor.copy(alpha = alpha))
        )
        Text(
            if (isNetworkAvailable) stringResource(R.string.live) else "PREVIEW · NO WI-FI",
            fontSize = 10.sp,
            fontWeight = FontWeight.Black,
            color = accentColor,
            letterSpacing = 1.5.sp
        )
    }
}

// Timer moved to PC client — kept here for reference in case it's needed on-device again.
// @Composable
// fun TimerBadge(seconds: Int) {
//     val m = seconds / 60
//     val s = seconds % 60
//     Text(
//         "%02d:%02d".format(m, s),
//         modifier = Modifier
//             .background(Color(0xFF0E0D14).copy(alpha = 0.65f), RoundedCornerShape(20.dp))
//             .padding(horizontal = 12.dp, vertical = 6.dp),
//         fontSize = 12.sp,
//         fontWeight = FontWeight.Bold,
//         color = Color.White
//     )
// }


@Composable
fun rememberIsWifiConnected(): Boolean {
    val context = LocalContext.current
    val cm = remember { context.getSystemService(ConnectivityManager::class.java) }

    fun checkWifi(): Boolean {
        val caps = cm.getNetworkCapabilities(cm.activeNetwork)
        return caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
    }

    var isWifi by remember { mutableStateOf(checkWifi()) }

    DisposableEffect(Unit) {
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(
                network: android.net.Network,
                caps: NetworkCapabilities
            ) { isWifi = caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) }
            override fun onLost(network: android.net.Network) { isWifi = checkWifi() }
        }
        cm.registerDefaultNetworkCallback(callback)
        onDispose { cm.unregisterNetworkCallback(callback) }
    }

    return isWifi
}

@Composable
fun IpBadge(ipAddress: String, port: Int, modifier: Modifier = Modifier, isClientConnected: Boolean = false) {
    val isWifi = rememberIsWifiConnected()

    // A connected client while Wi-Fi is down can only mean it came in over
    // the USB adb forward — there is no other transport. This is far more
    // reliable than checking BATTERY_PLUGGED_USB/ADB_ENABLED, which only
    // tell us a cable *could* carry a session, not that one actually exists.
    val isUsb = isClientConnected && !isWifi

    val targetAccentColor = when {
        !isWifi && !isUsb -> Color(0xFFFFA040)
        isClientConnected -> Color(0xFF4CD964)
        else -> MaterialTheme.colorScheme.primary
    }
    val accentColor by animateColorAsState(
        targetValue = targetAccentColor,
        animationSpec = tween(300),
        label = "ip_badge_accent_color"
    )
    val wifiIcon = when {
        !isWifi && !isUsb -> Icons.Filled.WifiOff
        isClientConnected -> Icons.Filled.Link
        else -> Icons.Filled.Wifi // isWifi is always true here: the two branches
        // above already cover both "no wifi, no usb" and "client connected".
    }

    // The thin (1dp) contrasting border around this rounded shape was the source of a
    // persistent shimmer artifact. It's likely a rasterization/antialiasing effect on
    // the stroke that some displays render as a subtle flicker. This is independent of
    // layout or recomposition — confirmed static via logging. Removing the stroke and
    // instead tinting the background with the accent color avoids the hard contrasting
    // edge while still communicating the connection state visually.
    val badgeBackground = Color(0xFF0E0D14).copy(alpha = 0.65f)
    val tintedBackground = lerp(badgeBackground, accentColor, 0.12f)

    Row(
        modifier = modifier
            .background(tintedBackground, RoundedCornerShape(20.dp))
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        AnimatedIcon(icon = wifiIcon, tint = accentColor, size = 16.dp)
        Text(
            when {
                isWifi -> "$ipAddress:$port"
                isUsb  -> stringResource(R.string.usb_connected)
                else   -> stringResource(R.string.no_wifi)
            },
            fontSize = if (isWifi) 15.sp else 12.sp,
            fontWeight = FontWeight.Black,
            color = if (isWifi) Color.White else accentColor,
            letterSpacing = if (isWifi) 0.sp else 1.5.sp
        )
        // Small secondary USB indicator alongside the Wi-Fi IP — only shown
        // when a client is actually connected over Wi-Fi at the same time a
        // USB session could be layered on top isn't distinguishable here, so
        // this stays Wi-Fi-IP-only; the dedicated USB state above handles
        // the no-Wi-Fi case.
    }
}


@Composable
fun ActionButton(isStreaming: Boolean, onStart: () -> Unit, onStop: () -> Unit) {
    val interactionSource = remember { MutableInteractionSource() }
    val isPressed by interactionSource.collectIsPressedAsState()

    val pressScale by animateFloatAsState(
        targetValue = if (isPressed) 0.97f else 1f,
        animationSpec = spring(Spring.DampingRatioMediumBouncy, Spring.StiffnessMedium),
        label = "btn_scale"
    )
    val iconRotation by animateFloatAsState(
        targetValue = if (isStreaming) 90f else 0f,
        animationSpec = spring(Spring.DampingRatioLowBouncy, Spring.StiffnessMedium),
        label = "icon_rotation"
    )
    val bgColor by animateColorAsState(
        targetValue = if (isStreaming) MaterialTheme.colorScheme.error.copy(alpha = 0.1f)
        else MaterialTheme.colorScheme.primary,
        animationSpec = tween(400),
        label = "btn_bg"
    )
    val borderColor by animateColorAsState(
        targetValue = if (isStreaming) MaterialTheme.colorScheme.error.copy(alpha = 0.25f)
        else Color.Transparent,
        animationSpec = tween(400),
        label = "btn_border"
    )
    val textColor by animateColorAsState(
        targetValue = if (isStreaming) MaterialTheme.colorScheme.error else Color.White,
        animationSpec = tween(400),
        label = "btn_text"
    )

    Box(
        Modifier
            .fillMaxWidth()
            .height(64.dp)
            .graphicsLayer { scaleX = pressScale; scaleY = pressScale }
            .clip(RoundedCornerShape(50.dp))
            .background(bgColor)
            .border(1.dp, borderColor, RoundedCornerShape(50.dp))
            .clickable(interactionSource = interactionSource, indication = null) {
                if (isStreaming) onStop() else onStart()
            },
        contentAlignment = Alignment.Center
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            // Text morphs with slide animation
            AnimatedContent(
                targetState = isStreaming,
                transitionSpec = {
                    (fadeIn(tween(220)) + slideInVertically(tween(220)) { if (targetState) it / 3 else -it / 3 }) togetherWith
                            (fadeOut(tween(150)) + slideOutVertically(tween(150)) { if (targetState) -it / 3 else it / 3 })
                },
                label = "btn_text_anim"
            ) { streaming ->
                Text(
                    if (streaming) stringResource(R.string.stop)
                    else stringResource(R.string.start_stream),
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    color = textColor,
                    letterSpacing = 0.5.sp
                )
            }
            // The icon wraps when transitioning
            Icon(
                if (isStreaming) Icons.Filled.Stop else Icons.Filled.PlayArrow,
                null,
                tint = textColor,
                modifier = Modifier
                    .size(18.dp)
                    .graphicsLayer { rotationZ = iconRotation }
            )
        }
    }
}