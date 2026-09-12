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
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.draw.alpha
import com.droidlens.app.Codec
import com.droidlens.app.Quality
import com.droidlens.app.R


// ---------------------------------------------------------------------------
// Sheet content — idle state only; streaming is handled by StreamingOverlay
// ---------------------------------------------------------------------------

@Composable
fun IdlePanelContent(
    isFrontCamera: Boolean,
    quality: Quality,
    codec: Codec,
    idlePreviewVisible: Boolean = false,
    dragOffset: Float = 0f,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onFlip: () -> Unit,
    onCycleQuality: () -> Unit,
    onCycleCodec: () -> Unit
) {
    // Enter animation — triggers on every mount (app launch, return from streaming, swipe back)
    // Part of the idle-state cascade: logo (0ms) → IP badge (150ms) → panel (300ms)
    Column(
        Modifier
            .fillMaxWidth()
            .cascadeEnter(delayMillis = 300, startOffsetY = 60f),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        IdleSheetContent(
            isFrontCamera = isFrontCamera,
            quality = quality,
            codec = codec,
            idlePreviewVisible = idlePreviewVisible,
            dragOffset = dragOffset,
            onFlip = onFlip,
            onCycleQuality = onCycleQuality,
            onCycleCodec = onCycleCodec
        )
        Box(Modifier.padding(horizontal = 24.dp, vertical = 16.dp).fillMaxWidth()) {
            ActionButton(isStreaming = false, onStart = onStart, onStop = onStop)
        }
    }
}

// ---------------------------------------------------------------------------
// AboutButton — small corner affordance that opens the About dialog.
// Overlaid in StreamingScreen's floating panel wrapper, pinned to the
// panel Surface's own top-end corner (doesn't take layout space).
// ---------------------------------------------------------------------------

@Composable
fun AboutButton(onClick: () -> Unit, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .size(30.dp)
            .clip(CircleShape)
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null
            ) { onClick() },
        contentAlignment = Alignment.Center
    ) {
        Icon(
            imageVector = Icons.Filled.Info,
            contentDescription = stringResource(R.string.about_button_desc),
            tint = MaterialTheme.colorScheme.primary,
            modifier = Modifier.size(15.dp)
        )
    }
}


// ---------------------------------------------------------------------------
// Idle sheet — camera selector, quality/codec tiles, start button
// ---------------------------------------------------------------------------

@Composable
fun IdleSheetContent(
    isFrontCamera: Boolean,
    quality: Quality,
    codec: Codec,
    idlePreviewVisible: Boolean = false,
    dragOffset: Float = 0f,
    onFlip: () -> Unit,
    onCycleQuality: () -> Unit,
    onCycleCodec: () -> Unit
) {
    // dragProgress: 0f = closed, 1f = fully open
    // While dragging up (offset negative): progress grows from 0 to 1
    // While dragging down (offset positive, preview open): progress shrinks from 1 to 0
    val dragProgress = when {
        !idlePreviewVisible -> (-dragOffset / 120f).coerceIn(0f, 1f)
        else -> (1f - dragOffset / 120f).coerceIn(0f, 1f)
    }

    Column(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        PreviewIndicator(visible = idlePreviewVisible, dragProgress = dragProgress)
        Spacer(Modifier.height(8.dp))
        FlatHubControls(
            isFrontCamera = isFrontCamera,
            quality = quality,
            codec = codec,
            idlePreviewVisible = idlePreviewVisible,
            onFlip = onFlip,
            onCycleQuality = onCycleQuality,
            onCycleCodec = onCycleCodec
        )
    }
}


// ---------------------------------------------------------------------------
// FlatHubControls — camera toggle + quality/codec cards side by side
// ---------------------------------------------------------------------------

@Composable
fun FlatHubControls(
    isFrontCamera: Boolean,
    quality: Quality,
    codec: Codec,
    idlePreviewVisible: Boolean = false,
    onFlip: () -> Unit,
    onCycleQuality: () -> Unit,
    onCycleCodec: () -> Unit
) {
    val qualityOptions = listOf(Quality.LOW, Quality.MEDIUM, Quality.HIGH)
    val qualityLabels = listOf(
        stringResource(R.string.quality_low),
        stringResource(R.string.quality_medium),
        stringResource(R.string.quality_high)
    )

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(160.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        // Camera toggle — square card on the left
        CameraToggleCard(
            isFrontCamera = isFrontCamera,
            isPreviewActive = idlePreviewVisible,
            onClick = onFlip,
            modifier = Modifier
                .fillMaxHeight()
                .aspectRatio(1f)
        )

        // Quality + Codec cards stacked vertically on the right
        Column(
            modifier = Modifier
                .weight(1f)
                .fillMaxHeight(),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            OptionCard(
                label = stringResource(R.string.quality),
                icon = Icons.Filled.Tune,
                value = qualityLabels[qualityOptions.indexOf(quality)],
                onClick = onCycleQuality,
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
                dots = qualityOptions.size,
                activeDot = qualityOptions.indexOf(quality)
            )
            OptionCard(
                label = stringResource(R.string.codec),
                icon = Icons.Filled.Memory,
                value = codec.label,
                onClick = onCycleCodec,
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
            )
        }
    }
}

// ---------------------------------------------------------------------------
// CameraToggleCard — vertical front/back toggle with animated thumb
// ---------------------------------------------------------------------------

@Composable
fun CameraToggleCard(
    isFrontCamera: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    isPreviewActive: Boolean = false
) {
    val rotation by animateFloatAsState(
        targetValue = if (isFrontCamera) 0f else 180f,
        animationSpec = spring(
            dampingRatio = Spring.DampingRatioLowBouncy,
            stiffness = Spring.StiffnessLow
        ),
        label = "cardFlip"
    )

    val cardScale by animateFloatAsState(
        targetValue = if (isPreviewActive) 1.04f else 1f,
        animationSpec = spring(Spring.DampingRatioMediumBouncy, Spring.StiffnessMedium),
        label = "cardScale"
    )
    val gradientAlpha by animateFloatAsState(
        targetValue = if (isPreviewActive) 1f else 0f,
        animationSpec = tween(500, easing = FastOutSlowInEasing),
        label = "gradientAlpha"
    )
    val primary = MaterialTheme.colorScheme.primary
    val cardShape = RoundedCornerShape(20.dp)

    Box(
        modifier = modifier
            .graphicsLayer {
                rotationY = rotation
                cameraDistance = 12f * density
                scaleX = cardScale
                scaleY = cardScale
                shape = cardShape
                clip = true
            }
            .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.06f))
            .background(
                Brush.verticalGradient(
                    colors = listOf(
                        primary.copy(alpha = 0.18f * gradientAlpha),
                        primary.copy(alpha = 0.04f * gradientAlpha)
                    )
                )
            )
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, cardShape)
            .clickable { onClick() },
        contentAlignment = Alignment.Center
    ) {
        // Switch content visibility based on the rotation threshold
        if (rotation <= 90f) {
            // FRONT CAMERA SIDE
            CameraSideContent(
                label = stringResource(id = R.string.front_lens),
                icon = Icons.Default.Person // Changed from Outlined to Default
            )
        } else {
            // BACK CAMERA SIDE
            // Reverse the content rotation so it's not mirrored after the 180-degree flip
            Box(Modifier.graphicsLayer { rotationY = 180f }) {
                CameraSideContent(
                    label = stringResource(id = R.string.back_lens),
                    icon = Icons.Default.Landscape // Changed from Outlined to Default
                )
            }
        }
    }
}

@Composable
fun CameraSideContent(label: String, icon: ImageVector) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
        modifier = Modifier.padding(16.dp)
    ) {
        // Visual container for the icon to create a "glassy" focal point
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .size(56.dp)
                .background(
                    MaterialTheme.colorScheme.primary.copy(alpha = 0.1f),
                    CircleShape
                )
        ) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(32.dp)
            )
        }
        Spacer(Modifier.height(12.dp))
        Text(
            text = label.uppercase(),
            fontSize = 12.sp,
            fontWeight = FontWeight.Black,
            color = MaterialTheme.colorScheme.onSurface,
            letterSpacing = 1.sp
        )
    }
}

// ---------------------------------------------------------------------------
// PreviewIndicator — pulsing dot + label that appears when idle preview is on
// ---------------------------------------------------------------------------

@Composable
fun PreviewIndicator(visible: Boolean, dragProgress: Float = 0f) {
    val transition = rememberInfiniteTransition(label = "pulse")
    val pulseAlpha by transition.animateFloat(
        initialValue = 1f,
        targetValue = 0.3f,
        animationSpec = infiniteRepeatable(
            animation = tween(800, easing = FastOutSlowInEasing),
            repeatMode = RepeatMode.Reverse
        ),
        label = "pulseAlpha"
    )

    // Combine drag-in-progress progress with settled visible state
    val targetProgress = if (visible) 1f else dragProgress
    val animatedProgress by animateFloatAsState(
        targetValue = targetProgress,
        animationSpec = tween(durationMillis = 380, easing = FastOutSlowInEasing),
        label = "indicatorProgress"
    )

    val indicatorHeight = 36.dp
    val show = animatedProgress > 0.01f

    if (show) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(indicatorHeight * animatedProgress)
                .alpha(animatedProgress),
            contentAlignment = Alignment.Center
        ) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 4.dp),
                horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(7.dp)
                        .alpha(if (visible) pulseAlpha else 1f)
                        .background(MaterialTheme.colorScheme.primary, CircleShape)
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    text = stringResource(R.string.preview_active),
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Black,
                    color = MaterialTheme.colorScheme.primary,
                    letterSpacing = 1.5.sp
                )
            }
        }
    }
}


// ---------------------------------------------------------------------------
// OptionCard — label + icon + animated value + optional dots
// ---------------------------------------------------------------------------

@Composable
fun OptionCard(
    label: String,
    icon: ImageVector,
    value: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    dots: Int = 0,
    activeDot: Int = 0
) {
    val interactionSource = remember { MutableInteractionSource() }
    val isPressed by interactionSource.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (isPressed) 0.94f else 1f,
        animationSpec = spring(Spring.DampingRatioMediumBouncy, Spring.StiffnessMedium),
        label = "card_scale_$label"
    )

    Row(
        modifier = modifier
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clip(RoundedCornerShape(16.dp))
            .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.06f))
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(16.dp))
            .clickable(indication = null, interactionSource = interactionSource) { onClick() }
            .padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.primary,
            modifier = Modifier.size(18.dp)
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                label,
                fontSize = 9.sp,
                fontWeight = FontWeight.Black,
                color = MaterialTheme.colorScheme.primary,
                letterSpacing = 1.5.sp
            )
            AnimatedContent(
                targetState = value,
                transitionSpec = {
                    (fadeIn(tween(180)) + slideInVertically(tween(180)) { -it / 2 }) togetherWith
                            (fadeOut(tween(120)) + slideOutVertically(tween(120)) { it / 2 })
                },
                label = "value_$label"
            ) { v ->
                Text(
                    v,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.onSurface
                )
            }
        }
        // Dots indicator — only shown when dots > 0
        if (dots > 0) {
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                repeat(dots) { i ->
                    val dotAlpha by animateFloatAsState(
                        targetValue = if (i == activeDot) 1f else 0.3f,
                        animationSpec = tween(200),
                        label = "dot_${label}_$i"
                    )
                    val dotSize by animateDpAsState(
                        targetValue = if (i == activeDot) 6.dp else 4.dp,
                        animationSpec = spring(Spring.DampingRatioMediumBouncy, Spring.StiffnessMedium),
                        label = "dot_size_${label}_$i"
                    )
                    Box(
                        modifier = Modifier
                            .size(dotSize)
                            .clip(CircleShape)
                            .background(MaterialTheme.colorScheme.primary.copy(alpha = dotAlpha))
                    )
                }
            }
        }
    }
}