package com.droidlens.app.ui.screen

import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInParent
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// ---------------------------------------------------------------------------
// Splash screen — "Droid" + "Lens" starts large and centered, then travels
// up-left into the exact spot where the logo sits in ViewportHeader, shrinking
// and fading its container in as it lands. Reuses the same easing curves as
// cascadeEnter() in ViewportComponents.kt so the hand-off feels continuous.
// ---------------------------------------------------------------------------

private const val HOLD_MILLIS = 450L
private const val TRAVEL_MILLIS = 650

private const val SPLASH_FONT_SP = 48f
private const val HEADER_FONT_SP = 22f

@Composable
fun SplashScreen(
    onFinished: () -> Unit
) {
    // Progress driven manually so font size, offset and container fade can
    // all be derived from a single source of truth (0f = centered/huge,
    // 1f = docked at header position/normal size).
    val progress = remember { Animatable(0f) }

    // Target offset is measured at runtime because header padding/position
    // can vary slightly by device (status bar height, insets, etc.).
    var targetOffsetX by remember { mutableFloatStateOf(0f) }
    var targetOffsetY by remember { mutableFloatStateOf(0f) }
    var rootWidth by remember { mutableFloatStateOf(0f) }
    var rootHeight by remember { mutableFloatStateOf(0f) }
    var logoWidth by remember { mutableFloatStateOf(0f) }
    var logoHeight by remember { mutableFloatStateOf(0f) }

    val fontSize = SPLASH_FONT_SP - (SPLASH_FONT_SP - HEADER_FONT_SP) * progress.value
    val containerAlpha = progress.value // capsule bg fades in as it docks

    // Fade the traveling logo out over the last stretch of the trip so it
    // never has to land pixel-perfectly on the real header logo — it simply
    // dissolves as ViewportHeader's own cascadeEnter fades the real one in.
    val fadeOutStart = 0.45f
    val logoAlpha = if (progress.value <= fadeOutStart) {
        1f
    } else {
        1f - ((progress.value - fadeOutStart) / (1f - fadeOutStart))
    }

    LaunchedEffect(Unit) {
        delay(HOLD_MILLIS)
        launch {
            progress.animateTo(
                targetValue = 1f,
                animationSpec = tween(TRAVEL_MILLIS, easing = FastOutSlowInEasing)
            )
            onFinished()
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            .onGloballyPositioned { coordinates ->
                rootWidth = coordinates.size.width.toFloat()
                rootHeight = coordinates.size.height.toFloat()
            }
    ) {
        // Header-position anchor: an invisible copy of the exact logo Row from
        // ViewportHeader (same padding, same alignment, same font size) so the
        // measured position matches the real landing spot pixel-for-pixel,
        // instead of approximating it with a bare padded Box.
        Row(
            modifier = Modifier
                .padding(horizontal = 24.dp, vertical = 32.dp)
                .background(Color.Transparent, RoundedCornerShape(20.dp))
                .padding(horizontal = 12.dp, vertical = 6.dp)
                .graphicsLayer { alpha = 0f }
                .onGloballyPositioned { coordinates ->
                    val pos = coordinates.positionInParent()
                    targetOffsetX = pos.x
                    targetOffsetY = pos.y
                },
            verticalAlignment = Alignment.Bottom
        ) {
            Text(
                "Droid",
                fontSize = HEADER_FONT_SP.sp,
                fontWeight = FontWeight.Black,
                letterSpacing = (-0.5).sp
            )
            Text(
                "Lens",
                fontSize = HEADER_FONT_SP.sp,
                fontWeight = FontWeight.Black,
                letterSpacing = (-0.5).sp
            )
        }

        Row(
            modifier = Modifier
                .onGloballyPositioned { coordinates ->
                    logoWidth = coordinates.size.width.toFloat()
                    logoHeight = coordinates.size.height.toFloat()
                }
                .offset {
                    // Explicit top-start positioning: compute the "centered"
                    // position ourselves (root center minus half of this row's
                    // own size), then interpolate toward the measured header anchor.
                    // Avoids combining Alignment.Center with a graphicsLayer
                    // translation, which double-applies centering.
                    val startX = rootWidth / 2f - logoWidth / 2f
                    val startY = rootHeight / 2f - logoHeight / 2f
                    val x = startX + (targetOffsetX - startX) * progress.value
                    val y = startY + (targetOffsetY - startY) * progress.value
                    androidx.compose.ui.unit.IntOffset(x.toInt(), y.toInt())
                }
                .clip(RoundedCornerShape(20.dp))
                .background(Color(0xFF0E0D14).copy(alpha = 0.65f * containerAlpha))
                .graphicsLayer { alpha = logoAlpha }
                .padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.Bottom
        ) {
            Text(
                "Droid",
                fontSize = fontSize.sp,
                fontWeight = FontWeight.Black,
                color = MaterialTheme.colorScheme.onBackground,
                letterSpacing = (-0.5).sp
            )
            Text(
                "Lens",
                fontSize = fontSize.sp,
                fontWeight = FontWeight.Black,
                color = MaterialTheme.colorScheme.primary,
                letterSpacing = (-0.5).sp
            )
        }
    }
}