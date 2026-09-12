package com.droidlens.app.ui.theme

import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithCache
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageShader
import androidx.compose.ui.graphics.ShaderBrush
import androidx.compose.ui.graphics.TileMode
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import com.droidlens.app.R

/**
 * Draws a subtle accent-tinted glow in the top-right corner, then a tiled
 * grain-noise texture on top using BlendMode.Overlay at low alpha.
 *
 * Both layers are drawn once per frame the background is composed (no
 * animation loop inside), so the cost is equivalent to any static gradient —
 * negligible next to things like the panel's Haze blur.
 *
 * Usage:
 *   Box(Modifier.fillMaxSize().appBackgroundTexture())
 *
 * @param grainAlpha overlay strength for the noise texture (0.03-0.06 recommended)
 * @param glowColor color used for the soft corner glow (defaults to AccentIndigo)
 * @param glowAlpha peak opacity of the glow at its center
 */
@Composable
fun Modifier.appBackgroundTexture(
    grainAlpha: Float = 0.045f,
    glowColor: Color = AccentIndigo,
    glowAlpha: Float = 0.14f,
    bottomGlowAlpha: Float = 0.22f
): Modifier {
    val context = LocalContext.current
    val noiseImage = remember {
        androidx.core.content.res.ResourcesCompat
            .getDrawable(context.resources, R.drawable.noise_grain, null)
            ?.let { drawable ->
                android.graphics.Bitmap.createBitmap(
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
        val topGlowBrush = Brush.radialGradient(
            colors = listOf(glowColor.copy(alpha = glowAlpha), Color.Transparent),
            center = Offset(size.width * 0.85f, size.height * 0.05f),
            radius = size.width * 0.9f
        )

        // Mirrors the top glow at the bottom-center, right where the floating
        // panel sits — makes it visually obvious the panel is translucent,
        // since this glow (and the grain above it) shows through the blur.
        val bottomGlowBrush = Brush.radialGradient(
            colors = listOf(glowColor.copy(alpha = bottomGlowAlpha), Color.Transparent),
            center = Offset(size.width * 0.5f, size.height * 1.0f),
            radius = size.width * 0.75f
        )

        val noiseBrush = noiseImage?.let {
            ShaderBrush(
                ImageShader(it, tileModeX = TileMode.Repeated, tileModeY = TileMode.Repeated)
            )
        }

        onDrawBehind {
            drawRect(brush = topGlowBrush)
            drawRect(brush = bottomGlowBrush)
            if (noiseBrush != null) {
                drawRect(brush = noiseBrush, alpha = grainAlpha, blendMode = BlendMode.Overlay)
            }
        }
    }
}