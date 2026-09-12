package com.droidlens.app.ui.screen

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.core.EaseOutCubic
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.foundation.background
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.res.stringResource
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.droidlens.app.R
import com.droidlens.app.StreamViewModel
import com.droidlens.app.camera.CameraPreview
import androidx.compose.ui.draw.clip
import com.droidlens.app.Quality
import com.droidlens.app.ui.theme.appBackgroundTexture
import dev.chrisbanes.haze.HazeState
import dev.chrisbanes.haze.hazeSource
import dev.chrisbanes.haze.hazeEffect
import dev.chrisbanes.haze.HazeStyle
import dev.chrisbanes.haze.HazeTint

// Base height of the floating panel
private val PANEL_BASE_HEIGHT = 320.dp
// Max extra height added when dragging up
private val PANEL_DRAG_MAX_EXTRA = 120.dp

// Derives the glass tint from the theme's surface color rather than hardcoding
// black/white — keeps the effect correct in both light and dark mode.
private fun glazeTintColor(themeSurface: Color, isDark: Boolean): Color {
    return themeSurface.copy(alpha = if (isDark) 0.55f else 0.65f)
}

@androidx.camera.camera2.interop.ExperimentalCamera2Interop
@Composable
fun StreamingScreen(vm: StreamViewModel = viewModel()) {
    val state by vm.state.collectAsState()
    val context = LocalContext.current
    val activity = context as? android.app.Activity

    LaunchedEffect(Unit) {
        activity?.let { vm.bindActivity(it) }
    }

    val configuration = androidx.compose.ui.platform.LocalConfiguration.current
    val isLandscape = configuration.orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE

    Box(Modifier.fillMaxSize()) {
        if (isLandscape && state.isStreaming) {
            // ── Landscape streaming layout (unchanged) ──────────────────────
            Box(
                Modifier
                    .fillMaxSize()
                    .background(Color.Black)
            ) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(end = 196.dp),
                    contentAlignment = Alignment.CenterStart
                ) {
                    CameraPreview(
                        modifier = Modifier.fillMaxSize(),
                        cameraSelector = state.selectedCamera,
                        codec = state.codec,
                        resolution = state.resolution,
                        jpegQuality = state.quality.jpegQuality,
                        isFrontCamera = state.isFrontCamera,
                        isReverseLandscape = vm.isReverseLandscape.collectAsState().value,
                        h264Encoder = vm.h264Encoder,
                        h265Encoder = vm.h265Encoder,
                        isStreaming = true,
                        renderPreview = !state.isBlackScreen,
                        onCameraReady = { cam -> vm.camera = cam; vm.clearCameraSwitching() },
                        onStatsUpdated = { stats -> vm.updateCameraStats(stats) },
                        onFrameReady = { jpeg -> vm.server.sendFrame(jpeg) }
                    )
                    Box(
                        Modifier
                            .fillMaxSize()
                            .windowInsetsPadding(WindowInsets.systemBars),
                        contentAlignment = Alignment.TopStart
                    ) {
                        ViewportHeader(
                            isStreaming = true,
                            ipAddress = state.ipAddress,
                            port = state.port,
                            isNetworkAvailable = state.isNetworkAvailable
                        )
                    }
                }

                StreamingOverlay(
                    isFrontCamera = state.isFrontCamera,
                    isBlackScreen = state.isBlackScreen,
                    isFlashlightOn = state.isFlashlightOn,
                    isCameraSwitching = state.isCameraSwitching,
                    panelPosition = state.panelPosition,
                    cameraStats = state.cameraStats,
                    codec = state.codec.label,
                    quality = when (state.quality) {
                        Quality.LOW -> stringResource(R.string.quality_low)
                        Quality.MEDIUM -> stringResource(R.string.quality_medium)
                        Quality.HIGH -> stringResource(R.string.quality_high)
                    },
                    onFlip = { vm.flipCamera() },
                    onRotate = { vm.toggleOrientation() },
                    onBlackScreen = { vm.toggleBlackScreen() },
                    onFlashlight = { vm.toggleFlashlight() },
                    onStop = { vm.stopStreaming() }
                )

                BackHandler { vm.stopStreaming() }
            }
        } else {
            // ── Portrait layout ──────────────────────────────────────────────
            val viewportColor by animateColorAsState(
                targetValue = if (state.isStreaming) Color.Black else MaterialTheme.colorScheme.background,
                animationSpec = tween(600),
                label = "viewport_bg"
            )

            var idlePreviewVisible by remember { mutableStateOf(false) }
            var dragOffset by remember { mutableFloatStateOf(0f) }
            var aboutDialogVisible by remember { mutableStateOf(false) }
            val density = LocalDensity.current
            val hazeState = remember { HazeState() }
            val isPowerSave = rememberIsPowerSaveMode()

            LaunchedEffect(state.isStreaming) {
                if (state.isStreaming) {
                    idlePreviewVisible = false
                    dragOffset = 0f
                }
            }

            // ── Layer 1: Camera — edge-to-edge, under status bar and nav bar ──
            Box(
                Modifier
                    .fillMaxSize()
                    .windowInsetsPadding(WindowInsets(0, 0, 0, 0))
                    .background(viewportColor)
                    .appBackgroundTexture()
                    .then(if (isPowerSave) Modifier else Modifier.hazeSource(hazeState))
            ) {
                if (state.isStreaming || idlePreviewVisible) {
                    CameraPreview(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(bottom = PANEL_BASE_HEIGHT - 60.dp)
                            .clip(RoundedCornerShape(bottomStart = 24.dp, bottomEnd = 24.dp)),
                        cameraSelector = state.selectedCamera,
                        codec = state.codec,
                        resolution = state.resolution,
                        jpegQuality = state.quality.jpegQuality,
                        isFrontCamera = state.isFrontCamera,
                        h264Encoder = vm.h264Encoder,
                        h265Encoder = vm.h265Encoder,
                        isStreaming = state.isStreaming,
                        onCameraReady = { cam ->
                            vm.camera = cam
                            vm.clearCameraSwitching()
                        },
                        onStatsUpdated = { stats -> vm.updateCameraStats(stats) },
                        onFrameReady = { jpeg ->
                            if (state.isStreaming) {
                                vm.server.sendFrame(jpeg)
                            }
                        }
                    )
                }

                // Fade-in every time IdleViewport enters composition
                // (app launch, swipe back from preview, return from streaming)
                AnimatedVisibility(
                    visible = !state.isStreaming && !idlePreviewVisible,
                    enter = fadeIn(animationSpec = tween(400)),
                    exit = fadeOut(animationSpec = tween(200))
                ) {
                    IdleViewport()
                }
            }

            // ── Layer 2: Status bar area + header (with its own padding) ─────
            Box(
                Modifier
                    .fillMaxSize()
                    .statusBarsPadding(),
                contentAlignment = Alignment.TopCenter
            ) {
                ViewportHeader(
                    isStreaming = state.isStreaming,
                    ipAddress = state.ipAddress,
                    port = state.port,
                    isClientConnected = state.isClientConnected,
                    isNetworkAvailable = state.isNetworkAvailable
                )
            }

            // ── Layer 3: Streaming overlay OR floating panel ─────────────────
            if (state.isStreaming) {
                StreamingOverlay(
                    isFrontCamera = state.isFrontCamera,
                    isBlackScreen = state.isBlackScreen,
                    isFlashlightOn = state.isFlashlightOn,
                    isCameraSwitching = state.isCameraSwitching,
                    panelPosition = state.panelPosition,
                    cameraStats = state.cameraStats,
                    codec = state.codec.label,
                    quality = when (state.quality) {
                        Quality.LOW -> stringResource(R.string.quality_low)
                        Quality.MEDIUM -> stringResource(R.string.quality_medium)
                        Quality.HIGH -> stringResource(R.string.quality_high)
                    },
                    onFlip = { vm.flipCamera() },
                    onRotate = { vm.toggleOrientation() },
                    onBlackScreen = { vm.toggleBlackScreen() },
                    onFlashlight = { vm.toggleFlashlight() },
                    onStop = { vm.stopStreaming() }
                )
                BackHandler { vm.stopStreaming() }
            } else {
                // ── Floating panel ───────────────────────────────────────────
                val maxDragPx = with(density) { PANEL_DRAG_MAX_EXTRA.toPx() }
                val dragExtraDp = with(density) {
                    dragOffset.coerceIn(0f, maxDragPx).toDp()
                }
                val dragProgress = (dragOffset / maxDragPx).coerceIn(0f, 1f)

                AnimatedVisibility(
                    // Always true here: this whole block only runs in the
                    // `else` branch of `if (state.isStreaming)` above.
                    visible = true,
                    enter = fadeIn(animationSpec = tween(300)) +
                            slideInVertically(
                                animationSpec = tween(400, easing = EaseOutCubic),
                                initialOffsetY = { it / 3 }
                            ),
                    exit = fadeOut(animationSpec = tween(150))
                ) {
                    Box(
                        modifier = Modifier
                            .fillMaxSize()
                            .navigationBarsPadding()
                            .padding(start = 12.dp, end = 12.dp, bottom = 16.dp),
                        contentAlignment = Alignment.BottomCenter
                    ) {
                        val isDark = androidx.compose.foundation.isSystemInDarkTheme()
                        val glassTintBase = MaterialTheme.colorScheme.surface

                        val panelBackgroundModifier = if (isPowerSave) {
                            // Battery saver: skip the per-frame blur pass entirely —
                            // flat, semi-transparent surface using the same tint color.
                            Modifier.background(glazeTintColor(glassTintBase, isDark))
                        } else {
                            Modifier.hazeEffect(
                                state = hazeState,
                                style = HazeStyle(
                                    tints = listOf(
                                        HazeTint(glazeTintColor(glassTintBase, isDark))
                                    ),
                                    blurRadius = 20.dp,
                                    noiseFactor = if (isDark) 0.15f else 0.08f
                                )
                            )
                        }

                        // Panel wrapper — holds the Surface + corner About button.
                        // The drag top padding lives here so the About button
                        // stays pinned to the panel's actual top-end corner.
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                // extra top padding grows when dragging up — panel expands upward
                                .padding(top = PANEL_DRAG_MAX_EXTRA - dragExtraDp)
                        ) {
                        Surface(
                            modifier = Modifier
                                .fillMaxWidth()
                                // ─── DEBUG: .shadow(...) ТИМЧАСОВО ВИДАЛЕНО ───
                                // Єдина змінена річ у цьому файлі. Все інше (Haze,
                                // hazeChild, clip, border, color=Transparent) — як в оригіналі.
                                .clip(RoundedCornerShape(24.dp))
                                .then(panelBackgroundModifier),
                            shape = RoundedCornerShape(24.dp),
                            // Fully transparent — Haze draws the blurred + tinted background instead
                            color = Color.Transparent,
                            tonalElevation = 0.dp,
                            shadowElevation = 0.dp,
                            border = androidx.compose.foundation.BorderStroke(
                                width = 1.dp,
                                brush = androidx.compose.ui.graphics.Brush.verticalGradient(
                                    colors = listOf(
                                        Color.White.copy(alpha = 0.18f),
                                        Color.White.copy(alpha = 0.04f)
                                    )
                                )
                            )
                        ) {
                            Column(modifier = Modifier.fillMaxWidth()) {
                                // ── Drag handle ──────────────────────────────────
                                Box(
                                    Modifier
                                        .fillMaxWidth()
                                        .padding(vertical = 12.dp)
                                        .pointerInput(idlePreviewVisible) {
                                            awaitPointerEventScope {
                                                while (true) {
                                                    val event =
                                                        awaitPointerEvent(PointerEventPass.Initial)
                                                    val change =
                                                        event.changes.firstOrNull() ?: continue
                                                    when {
                                                        change.pressed && !change.previousPressed -> {
                                                            dragOffset = 0f
                                                        }
                                                        change.pressed -> {
                                                            val delta =
                                                                change.position.y - change.previousPosition.y
                                                            // Swipe UP = negative delta → negate to grow panel
                                                            dragOffset = if (idlePreviewVisible) {
                                                                (dragOffset + delta).coerceIn(
                                                                    0f,
                                                                    PANEL_DRAG_MAX_EXTRA.toPx()
                                                                )
                                                            } else {
                                                                (dragOffset + (-delta)).coerceIn(
                                                                    0f,
                                                                    PANEL_DRAG_MAX_EXTRA.toPx()
                                                                )
                                                            }
                                                        }
                                                        !change.pressed && change.previousPressed -> {
                                                            val threshold = 40.dp.toPx()
                                                            if (!idlePreviewVisible && dragOffset > threshold) {
                                                                idlePreviewVisible = true
                                                            } else if (idlePreviewVisible && dragOffset > threshold) {
                                                                idlePreviewVisible = false
                                                            }
                                                            dragOffset = 0f
                                                        }
                                                    }
                                                }
                                            }
                                        },
                                    contentAlignment = Alignment.Center
                                ) {
                                    Surface(
                                        modifier = Modifier.size(width = 40.dp, height = 4.dp),
                                        shape = RoundedCornerShape(2.dp),
                                        color = MaterialTheme.colorScheme.outlineVariant
                                    ) {}
                                }

                                // ── Sheet content ─────────────────────────────────
                                Box(modifier = Modifier.fillMaxWidth()) {
                                    IdlePanelContent(
                                        isFrontCamera = state.isFrontCamera,
                                        quality = state.quality,
                                        codec = state.codec,
                                        idlePreviewVisible = idlePreviewVisible,
                                        dragOffset = if (idlePreviewVisible) 0f else -(dragProgress * 120f),
                                        onStart = { vm.startStreaming(context) },
                                        onStop = { idlePreviewVisible = false; vm.stopStreaming() },
                                        onFlip = { vm.flipCamera() },
                                        onCycleQuality = { vm.cycleQuality() },
                                        onCycleCodec = { vm.cycleCodec() }
                                    )
                                } // end measurement Box
                            }
                        }

                            // About trigger — pinned to the panel's own top-end
                            // corner (overlay, doesn't take layout space).
                            AboutButton(
                                onClick = { aboutDialogVisible = true },
                                modifier = Modifier
                                    .align(Alignment.TopEnd)
                                    .padding(top = 8.dp, end = 8.dp)
                            )
                        } // end panel wrapper Box
                    }
                } // end AnimatedVisibility (floating panel)

                BackHandler { activity?.finish() }
            }

            if (aboutDialogVisible) {
                AboutDialog(onDismiss = { aboutDialogVisible = false })
            }
        }
    }
}