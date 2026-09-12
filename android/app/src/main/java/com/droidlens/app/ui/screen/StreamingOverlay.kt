package com.droidlens.app.ui.screen

import androidx.compose.animation.core.*
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.draw.drawWithCache
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.ImageShader
import androidx.compose.ui.graphics.ShaderBrush
import androidx.compose.ui.graphics.TileMode
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.platform.LocalResources
import androidx.core.graphics.createBitmap
import com.droidlens.app.R

enum class PanelPosition { RIGHT, LEFT }

// Live camera stats shown when stats mode is toggled on.
data class CameraStats(
    val fps: Float,
    val iso: Int,
    val exposureFraction: String // e.g. "1/60"
)

// Panel summary parameters — to avoid passing many arguments to each call
@Stable
private class PanelState(
    val isBlackScreen: Boolean,
    val isFlashlightOn: Boolean,
    val isFrontCamera: Boolean,
    val isCameraSwitching: Boolean,
    val isStatsMode: Boolean,
    val cameraStats: CameraStats?,
    val codec: String,
    val quality: String,
    val onFlip: () -> Unit,
    val onRotate: () -> Unit,
    val onBlackScreen: () -> Unit,
    val onFlashlight: () -> Unit,
    val onStop: () -> Unit,
    val onToggleStats: () -> Unit
)

// Derived icon helpers — computed on demand rather than stored in PanelState
private val PanelState.blackIcon: ImageVector
    get() = if (isBlackScreen) Icons.Filled.VisibilityOff else Icons.Filled.Visibility

private val PanelState.flashIcon: ImageVector
    get() = if (isFrontCamera) {
        if (isFlashlightOn) Icons.Filled.WbSunny else Icons.Filled.BrightnessLow
    } else {
        if (isFlashlightOn) Icons.Filled.FlashOn else Icons.Filled.FlashOff
    }

// Describes a single action button rendered in both grid and compact modes.
internal data class ActionButton(
    val icon: ImageVector,
    val label: Int, // string resource id
    val onClick: () -> Unit,
    val tint: @Composable () -> Color = { MaterialTheme.colorScheme.onSurface },
    val isActive: Boolean = false
)

// Fixed total panel width — no longer changes between grid/stats modes.
// Only the internal split between the icon strip and the stats content
// changes, via a crossfade/weight animation instead of resizing the panel.
private val PANEL_WIDTH = 220.dp
// Width of the narrow icon strip shown in stats mode. Must fit a 48dp
// CompactIconButton plus 8dp horizontal padding on each side (64dp min).
private val ICON_STRIP_WIDTH = 72.dp

// ---------------------------------------------------------------------------
// Outer Box fills the screen for alignment only — no background, no clickable,
// so touches fall through to the camera preview underneath.
// ---------------------------------------------------------------------------

@Composable
fun StreamingOverlay(
    isFrontCamera: Boolean,
    isBlackScreen: Boolean,
    isFlashlightOn: Boolean,
    panelPosition: PanelPosition,
    onFlip: () -> Unit,
    onRotate: () -> Unit,
    onBlackScreen: () -> Unit,
    onFlashlight: () -> Unit,
    onStop: () -> Unit,
    modifier: Modifier = Modifier,
    isCameraSwitching: Boolean = false,
    cameraStats: CameraStats? = null,
    codec: String = "",
    quality: String = ""
) {
    var isStatsMode by remember { mutableStateOf(false) }

    val state = PanelState(
        isBlackScreen = isBlackScreen,
        isFlashlightOn = isFlashlightOn,
        isFrontCamera = isFrontCamera,
        isCameraSwitching = isCameraSwitching,
        isStatsMode = isStatsMode,
        cameraStats = cameraStats,
        codec = codec,
        quality = quality,
        onFlip = onFlip,
        onRotate = onRotate,
        onBlackScreen = onBlackScreen,
        onFlashlight = onFlashlight,
        onStop = onStop,
        onToggleStats = { isStatsMode = !isStatsMode }
    )

    Box(modifier = modifier.fillMaxSize()) {
        // Shadow cast onto the preview by the panel, drawn first so the
        // panel itself renders on top of it. Gives the panel a sense of
        // floating above the preview without a shadow ringing its full
        // outline (which reads as a second parallel edge along the
        // panel's straight side flush with the screen border).
        Box(
            modifier = Modifier
                .align(if (panelPosition == PanelPosition.RIGHT) Alignment.CenterEnd else Alignment.CenterStart)
                .fillMaxHeight()
                .width(PANEL_WIDTH + ShadowOnPreviewWidth)
                .panelDropShadowOnPreview(isRight = panelPosition == PanelPosition.RIGHT)
        )
        SidePanel(state, panelPosition)
    }
}

// ---------------------------------------------------------------------------
// SidePanel — handles both RIGHT and LEFT positions.
//
// Default state: 220dp panel, 2x2 button grid with labels, stop button below.
// A toggle button switches to stats mode: the grid crossfades into a compact
// icon-only column, and a semi-transparent stats section appears toward the
// center of the screen.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// panelSurfaceTexture — gives the side panel some depth without any blur:
// a directional gradient (imitates light falling from the screen side),
// a 1px inner highlight along the inner edge, and a very faint amber-tinted
// grain (reusing the app's existing neutral noise_grain_256 asset, tinted
// via BlendMode.Overlay rather than shipping a second colored PNG).
//
// All static — drawn once via drawWithCache, no per-frame cost.
// ---------------------------------------------------------------------------

// Panel gradient now derives from the theme's surface color instead of a
// fixed dark purple, so the panel follows light/dark theme like the rest
// of the UI (matches the approach used for the idle sheet's glazeTintColor
// in StreamingScreen.kt). The three stops are the same surface color at
// slightly different tonal weights to preserve the subtle depth effect.
@Composable
private fun panelGradientColors(isDark: Boolean): List<Color> {
    val surface = MaterialTheme.colorScheme.surface
    return if (isDark) {
        listOf(
            surface.copy(alpha = 0.98f),
            surface,
            lerp(surface, Color.Black, 0.35f)
        )
    } else {
        listOf(
            surface,
            surface,
            lerp(surface, Color.Black, 0.04f)
        )
    }
}

@Composable
private fun Modifier.panelSurfaceTexture(
    grainAlpha: Float = 0.028f
): Modifier {
    val resources = LocalResources.current
    val isDarkPanel = androidx.compose.foundation.isSystemInDarkTheme()
    val gradientColors = panelGradientColors(isDarkPanel)
    val grainTint = MaterialTheme.colorScheme.tertiary
    val noiseImage = remember {
        androidx.core.content.res.ResourcesCompat
            .getDrawable(resources, R.drawable.noise_grain, null)
            ?.let { drawable ->
                createBitmap(
                    drawable.intrinsicWidth,
                    drawable.intrinsicHeight,
                    android.graphics.Bitmap.Config.ARGB_8888
                ).also { bmp ->
                    val canvas = android.graphics.Canvas(bmp)
                    drawable.setBounds(0, 0, canvas.width, canvas.height)
                    drawable.draw(canvas)
                }.asImageBitmap()
            }
    }

    return this.drawWithCache {
        // Diagonal gradient — light falling toward the screen-facing edge.
        val gradient = Brush.linearGradient(
            colors = gradientColors,
            start = Offset(0f, 0f),
            end = Offset(size.width, size.height)
        )

        val noiseBrush = noiseImage?.let {
            ShaderBrush(
                ImageShader(it, tileModeX = TileMode.Repeated, tileModeY = TileMode.Repeated)
            )
        }

        onDrawBehind {
            drawRect(brush = gradient)
            if (noiseBrush != null) {
                // Tint the neutral grain amber by drawing a solid amber rect
                // through the same noise shader via Overlay, at a very low alpha.
                drawRect(brush = noiseBrush, alpha = grainAlpha, blendMode = BlendMode.Overlay)
                drawRect(color = grainTint, alpha = grainAlpha * 0.6f, blendMode = BlendMode.Overlay)
            }
        }
    }
}

// ---------------------------------------------------------------------------
// panelDropShadowOnPreview — draws a soft shadow cast BY the panel ONTO the
// camera preview beside it, instead of a shadow ringing the panel's full
// outline. A shadow around the whole shape also darkens the panel's own
// straight outer edge (flush with the screen border), which reads as a
// second parallel line rather than depth — this only touches the preview.
// ---------------------------------------------------------------------------

private val ShadowOnPreviewWidth = 20.dp

@Composable
private fun Modifier.panelDropShadowOnPreview(isRight: Boolean): Modifier {
    return this.drawWithCache {
        val shadowWidthPx = ShadowOnPreviewWidth.toPx()
        // Container is PANEL_WIDTH + ShadowOnPreviewWidth wide, docked to the
        // same edge as the panel. The panel itself occupies the docked side
        // (PANEL_WIDTH); this shadow only needs to render in the remaining
        // strip that extends onto the preview, fading out away from the panel.
        val gradient = if (isRight) {
            // Panel docked right → shadow strip is the left part of this
            // container (from x=0 to x=shadowWidthPx), darkest next to the panel.
            Brush.horizontalGradient(
                colors = listOf(
                    Color.Transparent,
                    Color.Black.copy(alpha = 0.32f)
                ),
                startX = 0f,
                endX = shadowWidthPx
            )
        } else {
            // Panel docked left → shadow strip is the right part.
            Brush.horizontalGradient(
                colors = listOf(
                    Color.Black.copy(alpha = 0.32f),
                    Color.Transparent
                ),
                startX = size.width - shadowWidthPx,
                endX = size.width
            )
        }

        onDrawBehind {
            drawRect(brush = gradient)
        }
    }
}

@Composable
private fun BoxScope.SidePanel(s: PanelState, position: PanelPosition) {
    val isRight = position == PanelPosition.RIGHT

    val shape = if (isRight) {
        RoundedCornerShape(topStart = 24.dp, bottomStart = 24.dp)
    } else {
        RoundedCornerShape(topEnd = 24.dp, bottomEnd = 24.dp)
    }

    Box(
        modifier = Modifier
            .align(if (isRight) Alignment.CenterEnd else Alignment.CenterStart)
            .fillMaxHeight()
            .width(PANEL_WIDTH) // fixed panel size — width never animates
    ) {
        Surface(
            shape = shape,
            color = Color.Transparent,
            tonalElevation = 0.dp,
            shadowElevation = 0.dp,
            modifier = Modifier
                .fillMaxHeight()
                .fillMaxWidth()
                .clip(shape)
                .panelSurfaceTexture()
        ) {
            PanelContentRow(s, isRight)
        }
    }
}

// ---------------------------------------------------------------------------
// SidePanel content — the panel's total width is fixed (PANEL_WIDTH) at all
// times, and so is the internal layout: the icon column always measures at
// its full PANEL_WIDTH, and the stats column always measures at its full
// (PANEL_WIDTH - ICON_STRIP_WIDTH) width. Nothing re-measures on every frame.
//
// The animation itself is purely a graphicsLayer transform (translation +
// alpha), which the compositor can run on its own thread without going
// through Compose's measure/layout passes at all. That's what actually
// fixes the dropped frames — previously animateFloatAsState fed straight
// into .width()/.weight(), forcing a full remeasure of both columns (and
// every button inside them) on every single animation frame.
// ---------------------------------------------------------------------------

@Composable
private fun PanelContentRow(s: PanelState, isRight: Boolean) {
    // animateFloatAsState returns a State<Float>. Keeping it as `statsFractionState`
    // and reading .value only inside graphicsLayer{} lambdas (a draw-phase
    // callback) means the per-frame value changes are consumed without
    // triggering recomposition of PanelContentRow/ButtonColumn on every
    // frame — only the draw phase re-runs, which is far cheaper and avoids
    // reallocating the actions list / tint lambdas 60 times a second.
    val statsFractionState = animateFloatAsState(
        targetValue = if (s.isStatsMode) 1f else 0f,
        // A large translation (icon strip sliding ~150dp) reads as a jolt
        // at the same spring used for small press-scale bounces, so this
        // uses a gentler damping/stiffness pair — quick, but without the
        // hard snap.
        animationSpec = spring(dampingRatio = Spring.DampingRatioLowBouncy, stiffness = Spring.StiffnessMediumLow),
        label = "stats_fraction"
    )

    // Used only to pick which discrete layout (grid vs compact) to show.
    // derivedStateOf collapses the continuous per-frame float into a single
    // boolean flip, so ButtonColumn only recomposes twice per transition
    // (at the 0.5 crossover) instead of every frame.
    val showStatsLayout by remember { derivedStateOf { statsFractionState.value > 0.5f } }

    val statsColumnWidth = PANEL_WIDTH - ICON_STRIP_WIDTH

    Box(
        modifier = Modifier
            .fillMaxHeight()
            .width(PANEL_WIDTH)
            .windowInsetsPadding(WindowInsets.systemBars)
    ) {
        // Stats column: fixed width and position, only its own content
        // (and a slide-in offset) animates via graphicsLayer — no remeasure,
        // no recomposition (statsFractionState.value is read at draw time).
        Box(
            modifier = Modifier
                .align(if (isRight) Alignment.CenterStart else Alignment.CenterEnd)
                .fillMaxHeight()
                .width(statsColumnWidth)
                .graphicsLayer {
                    val fraction = statsFractionState.value
                    translationX = (if (isRight) -1f else 1f) * (1f - fraction) * 24.dp.toPx()
                    alpha = fraction
                }
        ) {
            StatsSection(s.cameraStats, s.codec, s.quality, isRight = isRight)
        }

        // Icon column: fixed width and position — it never moves or
        // resizes at the Box level. Only its internal content crossfades
        // between the grid and compact layouts, and its own graphicsLayer
        // slides it between "full grid" and "narrow strip" appearance.
        ButtonColumn(
            s, statsFractionState, showStatsLayout, isRight,
            modifier = Modifier.align(if (isRight) Alignment.CenterEnd else Alignment.CenterStart)
        )
    }
}

@Composable
private fun ButtonColumn(
    s: PanelState,
    statsFractionState: State<Float>,
    showStatsLayout: Boolean,
    isRight: Boolean,
    modifier: Modifier = Modifier
) {
    val blackTint = @Composable { if (s.isBlackScreen) MaterialTheme.colorScheme.tertiary else MaterialTheme.colorScheme.onSurface }
    val flashTint = @Composable { if (s.isFlashlightOn) MaterialTheme.colorScheme.tertiary else MaterialTheme.colorScheme.onSurface }

    val actions = listOf(
        ActionButton(Icons.Filled.ScreenRotation, R.string.panel_rotate,     s.onRotate),
        ActionButton(s.blackIcon,                 R.string.panel_screen,     s.onBlackScreen, blackTint, s.isBlackScreen),
        ActionButton(s.flashIcon,                 R.string.panel_flashlight, s.onFlashlight,  flashTint, s.isFlashlightOn),
    )

    // The icon strip itself stays pinned to the docked edge (isRight ?
    // Alignment.End : Alignment.Start) at a constant ICON_STRIP_WIDTH once
    // in stats mode, sliding in from the full grid width — driven by the
    // same graphicsLayer transform, not a remeasure. Reading
    // statsFractionState.value here (inside graphicsLayer{}) rather than as
    // a plain Float parameter means this composable body itself does not
    // re-run every animation frame — only the draw phase does.
    Column(
        modifier = modifier
            .width(PANEL_WIDTH)
            .fillMaxHeight()
            .graphicsLayer {
                val collapsedOffset = PANEL_WIDTH.toPx() - ICON_STRIP_WIDTH.toPx()
                translationX = (if (isRight) 1f else -1f) * collapsedOffset * statsFractionState.value
            }
    ) {
        Column(
            modifier = Modifier
                .weight(1f)
                .padding(horizontal = 8.dp, vertical = 8.dp),
            horizontalAlignment = if (isRight) Alignment.End else Alignment.Start
        ) {
            // TEMP TEST: Crossfade(showStatsLayout, tween(130)) replaced with a
            // direct if — removes this Crossfade's own independent 130ms tween,
            // to check whether it's overlapping with the outer statsFraction
            // animation near the end of the transition is what causes the
            // observed frame drops right at the end of expand/collapse.
            run {
                if (!showStatsLayout) {
                    // Grid mode: 2x2 button grid with labels, stats toggle above.
                    Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.fillMaxWidth()) {
                        NarrowToggleButton(
                            icon = Icons.Filled.Analytics,
                            label = stringResource(R.string.panel_stats),
                            onClick = s.onToggleStats,
                            isActive = s.isStatsMode
                        )
                        Spacer(Modifier.height(10.dp))

                        // Flip uses the animated 3D card; remaining actions use BigPanelButton
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            FlipCameraButton(s.isFrontCamera, s.isCameraSwitching, s.onFlip, Modifier.weight(1f))
                            BigPanelButton(actions[0], Modifier.weight(1f))
                        }
                        Spacer(Modifier.height(10.dp))
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            BigPanelButton(actions[1], Modifier.weight(1f))
                            BigPanelButton(actions[2], Modifier.weight(1f))
                        }
                    }
                } else {
                    // Stats mode: icons shrink and line up in a single narrow
                    // column strip, pinned to the same edge as the panel dock,
                    // so it stays flush against the outer edge in both modes.
                    Column(
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                        modifier = Modifier.width(ICON_STRIP_WIDTH - 16.dp)
                    ) {
                        CompactIconButton(
                            icon = Icons.Filled.Analytics,
                            onClick = s.onToggleStats,
                            tint = MaterialTheme.colorScheme.tertiary,
                            isActive = true
                        )
                        // Flip uses the animated card even in compact mode
                        FlipCameraButton(s.isFrontCamera, s.isCameraSwitching, s.onFlip, compact = true)
                        // Remaining actions (rotate, black screen, flashlight) as plain icon buttons
                        actions.forEach { action ->
                            CompactIconButton(
                                icon = action.icon,
                                onClick = action.onClick,
                                tint = action.tint(),
                                isActive = action.isActive
                            )
                        }
                    }
                }
            }
        }

        // Always-visible Stop button — never hidden by mode switching.
        // Same horizontal padding as the icons above it in both modes, so
        // it lines up with them instead of hugging the panel's outer edge.
        Box(
            modifier = Modifier
                .padding(horizontal = 8.dp, vertical = 8.dp)
                .width(if (showStatsLayout) ICON_STRIP_WIDTH - 16.dp else PANEL_WIDTH - 16.dp),
            contentAlignment = Alignment.Center
        ) {
            // TEMP TEST: same Crossfade removal as above, for consistency.
            StopButton(s.onStop, compact = showStatsLayout)
        }
    }
}

// ---------------------------------------------------------------------------
// StatsSection — always measures at a fixed width (PANEL_WIDTH -
// ICON_STRIP_WIDTH); visibility/motion is handled entirely by the caller's
// graphicsLayer (translation + alpha), so this composable itself never
// triggers a remeasure when toggling modes.
// ---------------------------------------------------------------------------

@Composable
private fun StatsSection(stats: CameraStats?, codec: String, quality: String, isRight: Boolean) {
    Row(modifier = Modifier.fillMaxHeight()) {
        if (!isRight) StatsDivider()
        Box(
            modifier = Modifier
                .fillMaxHeight()
                .weight(1f)
                .padding(horizontal = 12.dp),
            contentAlignment = Alignment.Center
        ) {
            if (stats != null) StatContent(stats, codec, quality)
        }
        if (isRight) StatsDivider()
    }
}