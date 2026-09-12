package com.droidlens.app.ui.screen

import androidx.compose.animation.*
import androidx.compose.animation.core.*
import androidx.compose.ui.unit.Dp
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.droidlens.app.R
import androidx.compose.ui.draw.drawWithCache
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.drawscope.Stroke

// ---------------------------------------------------------------------------
// Shared press-scale animation — extracted once, used by every button type.
// ---------------------------------------------------------------------------

@Composable
internal fun rememberPressScale(
    pressedScale: Float = 0.94f,
    interactionSource: MutableInteractionSource
): Float {
    val isPressed by interactionSource.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (isPressed) pressedScale else 1f,
        animationSpec = spring(Spring.DampingRatioMediumBouncy, Spring.StiffnessMedium),
        label = "press_scale"
    )
    return scale
}

// Shared background color derivation for active/inactive states.
@Composable
internal fun panelButtonBg(isActive: Boolean, tint: Color) =
    if (isActive) tint.copy(alpha = 0.15f) else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f)

// ---------------------------------------------------------------------------
// Active-state glow border — mirrors Buttons.axaml's Button.ctrl.active /
// CtrlBtnActiveBorderBrush: a radial gradient anchored top-center (50%, 15%)
// that fades from full-strength tint down to fully transparent by the
// midpoint, instead of a flat-alpha ring all the way around. Reads as a
// soft light source rather than a hard neon outline. Falls back to the
// existing flat outlineVariant ring for the inactive state.
// ---------------------------------------------------------------------------

@Composable
internal fun Modifier.panelButtonBorder(
    isActive: Boolean,
    tint: Color,
    shape: RoundedCornerShape,
    strokeWidth: Dp = 1.dp
): Modifier {
    if (!isActive) {
        return this.border(strokeWidth, MaterialTheme.colorScheme.outlineVariant, shape)
    }
    return this.drawWithCache {
        val radius = (size.width.coerceAtLeast(size.height) * 0.75f).coerceAtLeast(1f)
        val center = Offset(size.width * 0.5f, size.height * 0.15f)
        val brush = Brush.radialGradient(
            colorStops = arrayOf(
                0f to tint.copy(alpha = 1f),
                0.5f to tint.copy(alpha = 0.5f),
                1f to tint.copy(alpha = 0f)
            ),
            center = center,
            radius = radius
        )
        val strokePx = strokeWidth.toPx()
        // Inset by half the stroke width so the stroke is drawn centered on
        // the shape's edge, matching how Modifier.border renders it.
        val inset = strokePx / 2f
        val cornerPx = shape.topStart.toPx(size, this)
        onDrawWithContent {
            drawContent()
            drawRoundRect(
                brush = brush,
                topLeft = Offset(inset, inset),
                size = androidx.compose.ui.geometry.Size(size.width - strokePx, size.height - strokePx),
                cornerRadius = CornerRadius(cornerPx - inset, cornerPx - inset),
                style = Stroke(width = strokePx)
            )
        }
    }
}

// Flat-alpha border color — kept for NarrowToggleButton (Stats toggle),
// which is not one of the four grid buttons switched to the glow border.
@Composable
internal fun narrowToggleBorder(isActive: Boolean, tint: Color) =
    if (isActive) tint.copy(alpha = 0.4f) else MaterialTheme.colorScheme.outlineVariant

// ---------------------------------------------------------------------------
// AnimatedIcon — rotate+scale swap with added micro-rotation (Wobble) effect
// ---------------------------------------------------------------------------

@Composable
internal fun AnimatedIcon(icon: ImageVector, tint: Color, size: Dp) {
    AnimatedContent(
        targetState = icon,
        transitionSpec = {
            (fadeIn(tween(140)) + scaleIn(tween(200), initialScale = 0.4f))
                .togetherWith(fadeOut(tween(100)) + scaleOut(tween(120), targetScale = 0.4f))
        },
        label = "animated_icon"
    ) { currentIcon ->

        // Use the local transition instance from AnimatedContent
        val rotation by transition.animateFloat(
            transitionSpec = {
                spring(
                    dampingRatio = Spring.DampingRatioMediumBouncy,
                    stiffness = Spring.StiffnessMedium
                )
            },
            label = "icon_wobble"
        ) { state ->
            // Check if the current animation state is the final one (Visible)
            if (state == EnterExitState.Visible) 0f else -35f
        }

        Icon(
            imageVector = currentIcon,
            contentDescription = null,
            tint = tint,
            modifier = Modifier
                .size(size)
                .graphicsLayer { rotationZ = rotation }
        )
    }
}

// ---------------------------------------------------------------------------
// BigPanelButton — grid mode (icon + label)
// ---------------------------------------------------------------------------

@Composable
internal fun BigPanelButton(
    action: ActionButton,
    modifier: Modifier = Modifier
) {
    val interactionSource = remember { MutableInteractionSource() }
    val scale = rememberPressScale(interactionSource = interactionSource)
    val tint = action.tint()
    val shape = RoundedCornerShape(18.dp)

    Box(
        modifier = modifier
            .fillMaxWidth()
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clip(shape)
            .background(panelButtonBg(action.isActive, tint))
            .panelButtonBorder(action.isActive, tint, shape)
            .clickable(indication = ripple(bounded = true), interactionSource = interactionSource) { action.onClick() },
        contentAlignment = Alignment.Center
    ) {
        Column(
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally,
            modifier = Modifier.padding(vertical = 16.dp, horizontal = 8.dp)
        ) {
            AnimatedIcon(action.icon, tint = tint, size = 28.dp)
            Spacer(Modifier.height(8.dp))
            Text(
                text = stringResource(action.label),
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.85f),
                fontSize = 12.sp,
                fontWeight = if (action.isActive) FontWeight.Bold else FontWeight.Medium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
    }
}

// ---------------------------------------------------------------------------
// CompactIconButton — stats mode (icon only, square)
// ---------------------------------------------------------------------------

@Composable
internal fun CompactIconButton(
    icon: ImageVector,
    onClick: () -> Unit,
    tint: Color = MaterialTheme.colorScheme.onSurface,
    isActive: Boolean = false
) {
    val interactionSource = remember { MutableInteractionSource() }
    val scale = rememberPressScale(interactionSource = interactionSource)
    val shape = RoundedCornerShape(14.dp)

    Box(
        modifier = Modifier
            .size(48.dp)
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clip(shape)
            .background(panelButtonBg(isActive, tint))
            .panelButtonBorder(isActive, tint, shape)
            .clickable(indication = ripple(bounded = true), interactionSource = interactionSource) { onClick() },
        contentAlignment = Alignment.Center
    ) {
        AnimatedIcon(icon, tint = tint, size = 22.dp)
    }
}

// ---------------------------------------------------------------------------
// StopButton
// ---------------------------------------------------------------------------

@Composable
internal fun StopButton(onStop: () -> Unit, compact: Boolean = false) {
    val shape = RoundedCornerShape(14.dp)
    // Solid fill to match the PC client's Button.danger style (App.axaml/
    // Buttons.axaml): full-color background with white text/icon, no
    // border or soft alpha tint. Note colorScheme.error is aliased to the
    // amber accent in Theme.kt, not a real red — so this is the same
    // "amber = live session" color language as the PC client, not a
    // destructive-red button.
    Box(
        modifier = (if (compact) Modifier.size(48.dp) else Modifier.fillMaxWidth().height(60.dp))
            .clip(shape)
            .background(MaterialTheme.colorScheme.error)
            .clickable(
                indication = ripple(bounded = true, color = MaterialTheme.colorScheme.onError),
                interactionSource = remember { MutableInteractionSource() }
            ) { onStop() },
        contentAlignment = Alignment.Center
    ) {
        if (compact) {
            Icon(Icons.Filled.Stop, null, tint = MaterialTheme.colorScheme.onError, modifier = Modifier.size(24.dp))
        } else {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = stringResource(R.string.stop),
                    color = MaterialTheme.colorScheme.onError,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Black,
                    letterSpacing = 0.5.sp
                )
                Icon(Icons.Filled.Stop, null, tint = MaterialTheme.colorScheme.onError, modifier = Modifier.size(28.dp))
            }
        }
    }
}

// ---------------------------------------------------------------------------
// NarrowToggleButton — full-width, low-height toggle bar (used for Stats)
// ---------------------------------------------------------------------------

@Composable
internal fun NarrowToggleButton(
    icon: ImageVector,
    label: String,
    onClick: () -> Unit,
    isActive: Boolean = false
) {
    val interactionSource = remember { MutableInteractionSource() }
    val scale = rememberPressScale(pressedScale = 0.97f, interactionSource = interactionSource)
    val tint = if (isActive) MaterialTheme.colorScheme.tertiary else MaterialTheme.colorScheme.onSurface

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(38.dp)
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clip(RoundedCornerShape(12.dp))
            .background(panelButtonBg(isActive, tint))
            .border(1.dp, narrowToggleBorder(isActive, tint), RoundedCornerShape(12.dp))
            .clickable(indication = ripple(bounded = true), interactionSource = interactionSource) { onClick() }
            .padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Icon(icon, null, tint = tint, modifier = Modifier.size(18.dp))
        Text(
            text = label,
            color = MaterialTheme.colorScheme.onSurface,
            fontSize = 12.sp,
            fontWeight = if (isActive) FontWeight.Bold else FontWeight.Medium,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

// ---------------------------------------------------------------------------
// FlipCameraButton — 3D flip between front and rear camera.
// compact=false: grid mode with icon + label; compact=true: 48dp icon-only square.
// ---------------------------------------------------------------------------

@Composable
internal fun FlipCameraButton(
    isFrontCamera: Boolean,
    isCameraSwitching: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    compact: Boolean = false
) {
    val interactionSource = remember { MutableInteractionSource() }
    val scale = rememberPressScale(interactionSource = interactionSource)

    // Two-phase animation:
    // Phase 1 (isCameraSwitching=true):  rotate to 90° — card is edge-on while camera switches
    // Phase 2 (isCameraSwitching=false): rotate to final angle (0° or 180°)
    val targetRotation = when {
        isCameraSwitching -> 90f   // pause edge-on while hardware switches
        isFrontCamera     -> 0f    // front camera — default position
        else              -> 180f  // back camera — flipped
    }
    val rotation by animateFloatAsState(
        targetValue = targetRotation,
        animationSpec = if (isCameraSwitching)
            tween(durationMillis = 300, easing = FastOutSlowInEasing)  // phase 1: fast to edge
        else
            spring(dampingRatio = Spring.DampingRatioLowBouncy, stiffness = Spring.StiffnessLow), // phase 2: springy to final
        label = "flip_rotation"
    )

    val shape = RoundedCornerShape(if (compact) 14.dp else 18.dp)
    val boxModifier = if (compact) modifier.size(48.dp) else modifier.fillMaxWidth()

    Box(
        modifier = boxModifier
            .graphicsLayer { scaleX = scale; scaleY = scale; rotationY = rotation; cameraDistance = 12f * density }
            .clip(shape)
            .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f))
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, shape)
            .clickable(indication = ripple(bounded = true), interactionSource = interactionSource) { onClick() },
        contentAlignment = Alignment.Center
    ) {
        if (compact) {
            // Compact mode: icon only, counter-rotate back-side to avoid mirroring
            val icon = if (rotation <= 90f) Icons.Filled.Person else Icons.Filled.Landscape
            val iconModifier = if (rotation > 90f) Modifier.graphicsLayer { rotationY = 180f } else Modifier
            Box(iconModifier) {
                Icon(icon, null, tint = MaterialTheme.colorScheme.onSurface, modifier = Modifier.size(22.dp))
            }
        } else {
            // Grid mode: icon + label, show content based on which side faces the user
            if (rotation <= 90f) {
                FlipCameraContent(label = stringResource(R.string.front_lens), icon = Icons.Filled.Person)
            } else {
                // Back-side content must be counter-rotated to avoid mirroring
                Box(Modifier.graphicsLayer { rotationY = 180f }) {
                    FlipCameraContent(label = stringResource(R.string.back_lens), icon = Icons.Filled.Landscape)
                }
            }
        }
    }
}

@Composable
private fun FlipCameraContent(label: String, icon: ImageVector) {
    Column(
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = Modifier.padding(vertical = 16.dp, horizontal = 8.dp)
    ) {
        Icon(icon, null, tint = MaterialTheme.colorScheme.onSurface, modifier = Modifier.size(28.dp))
        Spacer(Modifier.height(8.dp))
        Text(
            text = label,
            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.85f),
            fontSize = 12.sp,
            fontWeight = FontWeight.Medium,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

// ---------------------------------------------------------------------------
// Stats display components
// ---------------------------------------------------------------------------

@Composable
internal fun StatsDivider() {
    Box(
        modifier = Modifier
            .fillMaxHeight()
            .width(1.dp)
            .background(
                brush = Brush.verticalGradient(
                    colors = listOf(
                        Color.Transparent,
                        MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f),
                        Color.Transparent
                    )
                )
            )
    )
}

@Composable
internal fun StatContent(stats: CameraStats, codec: String, quality: String) {
    Column(
        verticalArrangement = Arrangement.spacedBy(10.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 2.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Icon(
                imageVector = Icons.Filled.Analytics,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.tertiary,
                modifier = Modifier.size(16.dp)
            )
            Text(
                text = stringResource(R.string.panel_stats).uppercase(),
                color = MaterialTheme.colorScheme.tertiary,
                fontSize = 11.sp,
                fontWeight = FontWeight.Black,
                letterSpacing = 1.sp
            )
        }

        StatItem(Icons.Filled.Speed,       stringResource(R.string.stat_fps),          "%.1f".format(stats.fps))
        StatItem(Icons.Filled.Exposure,    stringResource(R.string.stat_iso_exposure), "${stats.iso} / ${stats.exposureFraction}")
        StatItem(Icons.Filled.Code,        stringResource(R.string.codec),             codec)
        StatItem(Icons.Filled.HighQuality, stringResource(R.string.quality),           quality)
    }
}

@Composable
private fun StatItem(icon: ImageVector, label: String, value: String, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = 4.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(20.dp)
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = label.uppercase(),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                fontSize = 10.sp,
                fontWeight = FontWeight.Bold,
                letterSpacing = 0.5.sp
            )
            Text(
                text = value,
                color = MaterialTheme.colorScheme.onSurface,
                fontSize = 14.sp,
                fontWeight = FontWeight.ExtraBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
    }
}