package com.droidlens.app.camera

import android.graphics.BitmapFactory
import android.view.ViewGroup
import android.widget.ImageView
import androidx.camera.core.*
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.runtime.*
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.droidlens.app.Codec
import com.droidlens.app.ui.screen.CameraStats
import kotlin.math.roundToInt

private fun rotateJpegInMemory(jpeg: ByteArray, degrees: Int): ByteArray {
    if (degrees == 0) return jpeg
    return try {
        val original = BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size)
        val matrix = android.graphics.Matrix().apply {
            postRotate(degrees.toFloat())
        }
        val rotated = android.graphics.Bitmap.createBitmap(
            original, 0, 0, original.width, original.height, matrix, false
        )
        original.recycle()
        val stream = java.io.ByteArrayOutputStream()
        rotated.compress(android.graphics.Bitmap.CompressFormat.JPEG, 90, stream)
        rotated.recycle()
        stream.toByteArray()
    } catch (_: Exception) {
        jpeg
    }
}

// Explicit resolution target for ImageAnalysis, replacing the old setTargetAspectRatio-only
// approach. Without this, CameraX's default ResolutionStrategy targets 640x480 with
// FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER — the actual resolution it lands on then
// depends entirely on each device's StreamConfigurationMap output sizes and hardware
// level. Targeting the requested size directly (see Resolution enum, chosen from the
// PC client) makes the choice explicit and consistent: devices that support it reliably
// get it, while devices that genuinely can't will fall back to the closest lower size.
//
// AspectRatioStrategy must always be set explicitly and must roughly match targetSize's
// own ratio — CameraX does NOT skip aspect-based sorting when you omit it; it silently
// falls back to an internal 4:3 preference, which made HD/FHD start landing on
// 800x600/1280x960/1920x1440 (exact 4:3 hits) instead of their own exact 16:9 hardware
// matches once no strategy was set at all. Hardcoding RATIO_16_9 for everyone had the
// opposite problem: it fought SD's own targetSize (720x480, 3:2), since CameraX sorts
// candidates by aspect match first and kept re-selecting the exact 16:9 hit (1280x720)
// over the requested 720x480.
//
// CameraX only ships two built-in AspectRatioStrategy constants (4:3 and 16:9) — there's
// no 3:2 constant, and building one from a raw Rational hits a constructor overload
// CameraX resolves differently than expected (compile error: "Int was expected"). So
// this picks whichever built-in constant sorts closer to targetSize's actual shape:
// squarer-than-16:9 ratios (SD's 3:2) get RATIO_4_3, exact-16:9 targets (HD/FHD) get
// RATIO_16_9. Either way ResolutionStrategy's exact targetSize still does the real
// picking — this only decides which direction ties get broken in.
private fun targetResolutionSelector(targetSize: android.util.Size): androidx.camera.core.resolutionselector.ResolutionSelector {
    val ratio = targetSize.width.toFloat() / targetSize.height
    val aspectStrategy =
        if (ratio < 16f / 9f - 0.05f)
            androidx.camera.core.resolutionselector.AspectRatioStrategy.RATIO_4_3_FALLBACK_AUTO_STRATEGY
        else
            androidx.camera.core.resolutionselector.AspectRatioStrategy.RATIO_16_9_FALLBACK_AUTO_STRATEGY

    return androidx.camera.core.resolutionselector.ResolutionSelector.Builder()
        .setResolutionStrategy(
            androidx.camera.core.resolutionselector.ResolutionStrategy(
                targetSize,
                androidx.camera.core.resolutionselector.ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER
            )
        )
        .setAspectRatioStrategy(aspectStrategy)
        .build()
}


@androidx.camera.camera2.interop.ExperimentalCamera2Interop
@Composable
fun CameraPreview(
    modifier: Modifier = Modifier,
    cameraSelector: CameraSelector,
    codec: Codec,
    resolution: com.droidlens.app.Resolution = com.droidlens.app.Resolution.HD,
    jpegQuality: Int = 85,
    isFrontCamera: Boolean = false,
    // Mirrors StreamViewModel.isReverseLandscape — the single source of truth
    // for the Rotate button / sensor auto-detect. Passed in directly instead
    // of re-derived from LocalConfiguration/display.rotation: Configuration
    // does NOT change between LANDSCAPE and REVERSE_LANDSCAPE (both are the
    // same orientation class), so a Compose recomposition-based trigger keyed
    // off configuration never fires for this rotation and MJPEG's
    // ImageAnalysis.targetRotation was silently left stale.
    isReverseLandscape: Boolean = false,
    h264Encoder: VideoEncoder? = null,
    h265Encoder: VideoEncoder? = null,
    isStreaming: Boolean = false,
    renderPreview: Boolean = true,
    onCameraReady: (Camera) -> Unit = {},
    onStatsUpdated: (CameraStats) -> Unit = {},
    onFrameReady: (ByteArray) -> Unit
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val renderPreviewState = rememberUpdatedState(renderPreview)
    // Read on every frame inside the analyzer below so quality changes take
    // effect immediately without restarting the camera/analyzer pipeline.
    val jpegQualityState = rememberUpdatedState(jpegQuality)

    // MJPEG only: live reference to the bound ImageAnalysis use case so its
    // targetRotation can be updated in place (no rebind) whenever orientation
    // changes — Rotate button, sensor auto-detect, or a fresh use case after
    // flipCamera() rebinds the whole pipeline.
    val mjpegImageAnalysisRef = remember { mutableStateOf<ImageAnalysis?>(null) }

    // Maps isReverseLandscape directly to a Surface.ROTATION_* constant.
    // ROTATION_90 = 90° (normal landscape), ROTATION_270 = 270° (reverse
    // landscape) — matches MainActivity's SCREEN_ORIENTATION_LANDSCAPE /
    // REVERSE_LANDSCAPE mapping. Driven by the ViewModel flag instead of
    // display.rotation to avoid any race with the system's own window
    // rotation animation.
    LaunchedEffect(isReverseLandscape, mjpegImageAnalysisRef.value) {
        val rotation = if (isReverseLandscape)
            android.view.Surface.ROTATION_270
        else
            android.view.Surface.ROTATION_90
        mjpegImageAnalysisRef.value?.targetRotation = rotation
    }

    val executor = remember {
        java.util.concurrent.Executors.newSingleThreadExecutor()
    }

    // MJPEG: ImageView that receives decoded bitmaps directly — matches PreviewView behavior
    val imageView = remember {
        ImageView(context).apply {
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
            scaleType = ImageView.ScaleType.CENTER_CROP
        }
    }

    // Clear the last frozen frame when black screen is activated (MJPEG only)
    LaunchedEffect(renderPreview) {
        if (!renderPreview) {
            imageView.post { imageView.setImageDrawable(null) }
        }
    }

    // H264/idle: PreviewView — always visible, codec and quality don't affect idle
    val previewView = remember {
        PreviewView(context).apply {
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
            scaleType = PreviewView.ScaleType.FILL_CENTER
            implementationMode = PreviewView.ImplementationMode.COMPATIBLE
        }
    }

    // Wrapper FrameLayout: mirroring is applied here (native Android View scaleX),
    // NOT on previewView.scaleX and NOT via Compose Modifier.scale. PreviewView in
    // PERFORMANCE mode is backed by SurfaceView, which composites in its own window
    // layer ("hole punch") outside both the normal View transform pipeline and
    // Compose's draw layer. So scaleX set directly on it, or via a Compose
    // Modifier wrapping it, is ignored. A plain native ViewGroup ancestor's
    // scaleX still applies because it affects how the child SurfaceView's
    // window is positioned/transformed by the system compositor, one level up
    // from the SurfaceView itself.
    val previewContainer = remember {
        android.widget.FrameLayout(context).apply {
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
            addView(previewView)
        }
    }


    // Mirror only during H264/H265 streaming with front camera —
    // in idle mode PreviewView mirrors automatically; in MJPEG mode ImageView is used instead.
    val shouldMirror = isFrontCamera && isStreaming && (codec == Codec.H264 || codec == Codec.H265)
    LaunchedEffect(shouldMirror) {
        previewContainer.scaleX = if (shouldMirror) -1f else 1f
    }

    DisposableEffect(Unit) {
        onDispose {
            executor.shutdown()
            h264Encoder?.stop()
            h265Encoder?.stop()
        }
    }

    // In idle mode codec is irrelevant — exclude it from the key to avoid
    // restarting the camera when the user cycles codec while not streaming.
    val effectKey2 = if (isStreaming) codec else null
    // renderPreview only needs to be part of the rebind key for H264: that's the
    // only path where the Preview use case is conditionally bound/unbound below,
    // so toggling black-screen there must trigger a rebind. MJPEG and idle read
    // renderPreview reactively elsewhere (analyzer / renderPreviewState), so
    // including it here for them would cause pointless camera rebinds.
    val effectKey3 = if (isStreaming && (codec == Codec.H264 || codec == Codec.H265)) renderPreview else null
    LaunchedEffect(cameraSelector, effectKey2, effectKey3, isStreaming, resolution) {
        var cancelled = false
        var boundProvider: ProcessCameraProvider? = null
        var currentIso = 0
        var currentExposureFraction = "—"
        var lastFrameTimeNs = 0L
        var lastUiUpdateTimeNs = 0L
        var smoothedFps = 0.0f
        // Reused across frames to avoid ~30 allocations/sec of ~1.5MB (720p) that
        // put pressure on the GC and contributed to jank/heat.
        var nv12Buffer: ByteArray? = null

        val captureCallback = object : android.hardware.camera2.CameraCaptureSession.CaptureCallback() {
            override fun onCaptureCompleted(
                session: android.hardware.camera2.CameraCaptureSession,
                request: android.hardware.camera2.CaptureRequest,
                result: android.hardware.camera2.TotalCaptureResult
            ) {
                val iso = result.get(android.hardware.camera2.CaptureResult.SENSOR_SENSITIVITY) ?: 0
                val exposureTimeNs = result.get(android.hardware.camera2.CaptureResult.SENSOR_EXPOSURE_TIME) ?: 0L
                val fraction = if (exposureTimeNs > 0) {
                    val seconds = exposureTimeNs / 1_000_000_000.0
                    if (seconds >= 1.0) {
                        "%.1fs".format(seconds)
                    } else {
                        val denominator = (1.0 / seconds).roundToInt()
                        "1/$denominator"
                    }
                } else {
                    "—"
                }
                currentIso = iso
                currentExposureFraction = fraction
            }
        }
        val cameraProviderFuture = ProcessCameraProvider.getInstance(context)
        cameraProviderFuture.addListener({
            if (cancelled) return@addListener
            val cameraProvider = cameraProviderFuture.get()
            cameraProvider.unbindAll()
            boundProvider = cameraProvider

            if (!isStreaming) {
                // ── IDLE ───────────────────────────────────────────────────
                // Plain preview only — no ImageAnalysis, no encoder, no JPEG pipeline.
                // Codec and quality have no effect here.
                // Context.display (API 30+) replaces the deprecated defaultDisplay
                // getters below; kept as a fallback since minSdk here is lower than 30.
                val display = if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R) {
                    context.display
                } else {
                    @Suppress("DEPRECATION")
                    (context as? android.app.Activity)?.windowManager?.defaultDisplay
                        ?: (context.getSystemService(android.content.Context.WINDOW_SERVICE) as? android.view.WindowManager)?.defaultDisplay
                }
                val rotation = display?.rotation ?: android.view.Surface.ROTATION_0
                val screenPreview = Preview.Builder()
                    .setTargetRotation(rotation)
                    .build().also {
                        it.surfaceProvider = previewView.surfaceProvider
                    }
                try {
                    val cam = cameraProvider.bindToLifecycle(
                        lifecycleOwner, cameraSelector, screenPreview
                    )
                    onCameraReady(cam)
                } catch (_: Exception) {
                    // ignore: camera unavailable
                }

            } else if ((codec == Codec.H264 || codec == Codec.H265) &&
                (codec == Codec.H264 && h264Encoder != null || codec == Codec.H265 && h265Encoder != null)
            ) {
                // Resolve which hardware encoder is actually feeding this session.
                // Both branches share identical NV12-assembly and preview logic below —
                // only the encoder instance differs, so it's picked once here rather
                // than duplicating the whole ImageAnalysis block per codec.
                val activeEncoder = if (codec == Codec.H264) h264Encoder!! else h265Encoder!!
                // Stop the encoder not in use for this session, in case it was left
                // running from a previous codec selection. The active encoder itself
                // must NOT be stopped here — it hasn't started yet (VideoEncoder lazily
                // starts on the first feedFrame() call below), and stopping it would
                // immediately tear down the session this branch is about to begin.
                (if (codec == Codec.H264) h265Encoder else h264Encoder)?.stop()

                // H264/H265: PreviewView is only bound when renderPreview is true. When
                // the user toggles black screen, the Preview use case is dropped entirely
                // (not just visually covered) so the SurfaceView stops receiving frames
                // from the camera and drops out of the compositor. This is the actual
                // GPU/compose cost, previously paid even while covered by the black Box.
                // ImageAnalysis (the encoder feed) is unaffected either way.
                val screenPreview = if (renderPreviewState.value) {
                    Preview.Builder().build().also {
                        it.surfaceProvider = previewView.surfaceProvider
                    }
                } else null

                var startTimeUs = 0L

                val imageAnalysis = ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
                    .setResolutionSelector(targetResolutionSelector(android.util.Size(resolution.width, resolution.height)))
                    .also { builder ->
                        val extender = androidx.camera.camera2.interop.Camera2Interop.Extender(builder)
                        extender.setCaptureRequestOption(
                            android.hardware.camera2.CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,
                            android.util.Range(15, 30)
                        )
                        extender.setSessionCaptureCallback(captureCallback)
                    }
                    .build()

                imageAnalysis.setAnalyzer(executor) { imageProxy: ImageProxy ->
                    try {
                        // Calculate FPS
                        val nowNs = System.nanoTime()
                        if (lastFrameTimeNs != 0L) {
                            val frameDuration = nowNs - lastFrameTimeNs
                            val instantFps = 1_000_000_000.0f / frameDuration
                            smoothedFps = if (smoothedFps == 0f) instantFps else smoothedFps * 0.9f + instantFps * 0.1f
                        }
                        lastFrameTimeNs = nowNs

                        if (nowNs - lastUiUpdateTimeNs >= 500_000_000L) { // 500ms
                            onStatsUpdated(
                                CameraStats(
                                    fps = smoothedFps,
                                    iso = currentIso,
                                    exposureFraction = currentExposureFraction
                                )
                            )
                            lastUiUpdateTimeNs = nowNs
                        }
                        val yPlane = imageProxy.planes[0]
                        val uPlane = imageProxy.planes[1]
                        val w = imageProxy.width
                        val h = imageProxy.height
                        val yRowStride = yPlane.rowStride
                        val uvRowStride = uPlane.rowStride
                        val uvPixelStride = uPlane.pixelStride
                        // NV12: Y + interleaved UV — buffer reused across frames (see nv12Buffer above)
                        val requiredSize = w * h * 3 / 2
                        val nv12 = nv12Buffer.let {
                            if (it != null && it.size == requiredSize) it else ByteArray(requiredSize).also { newBuf -> nv12Buffer = newBuf }
                        }

                        // Y plane
                        for (row in 0 until h) {
                            yPlane.buffer.position(row * yRowStride)
                            yPlane.buffer.get(nv12, row * w, w)
                        }

                        // UV interleaved — U from planes[1], V from planes[2].
                        // Fast path: when pixelStride == 1, U/V bytes for a row are contiguous,
                        // so we can bulk-read each row into a temp array instead of one .get() per byte
                        // (random-access ByteBuffer reads are considerably slower than sequential bulk gets).
                        // Fallback: pixelStride == 2 (the common Camera2 semi-planar case) still needs
                        // per-pixel interleaving, since U and V are not laid out contiguously per row.
                        val uBuf = uPlane.buffer
                        val vBuf = imageProxy.planes[2].buffer
                        val uvRowStride2 = imageProxy.planes[2].rowStride
                        val uvPixelStride2 = imageProxy.planes[2].pixelStride
                        val uvDst = w * h
                        val halfW = w / 2

                        if (uvPixelStride == 1 && uvPixelStride2 == 1) {
                            // Bulk path: read each row's U and V bytes contiguously.
                            val uRow = ByteArray(halfW)
                            val vRow = ByteArray(halfW)
                            for (row in 0 until h / 2) {
                                uBuf.position(row * uvRowStride)
                                uBuf.get(uRow, 0, halfW)
                                vBuf.position(row * uvRowStride2)
                                vBuf.get(vRow, 0, halfW)
                                val rowBase = uvDst + row * w
                                for (col in 0 until halfW) {
                                    nv12[rowBase + col * 2] = vRow[col]
                                    nv12[rowBase + col * 2 + 1] = uRow[col]
                                }
                            }
                        } else {
                            // Indexed fallback for pixelStride == 2 (or any other stride).
                            for (row in 0 until h / 2) {
                                val uRowOffset = row * uvRowStride
                                val vRowOffset = row * uvRowStride2
                                val rowBase = uvDst + row * w
                                for (col in 0 until halfW) {
                                    nv12[rowBase + col * 2] = vBuf.get(vRowOffset + col * uvPixelStride2)
                                    nv12[rowBase + col * 2 + 1] = uBuf.get(uRowOffset + col * uvPixelStride)
                                }
                            }
                        }

                        val nowUs = System.nanoTime() / 1000
                        if (startTimeUs == 0L) startTimeUs = nowUs
                        activeEncoder.feedFrame(nv12, w, h, nowUs - startTimeUs)
                    } catch (_: Throwable) {
                        // ignore: encoder frame error
                    } finally {
                        imageProxy.close()
                    }
                }

                try {
                    // screenPreview is null when renderPreview is false — only bind
                    // the use cases that actually exist (Preview is optional here,
                    // ImageAnalysis always runs to keep the encoder fed).
                    val useCases = listOfNotNull(screenPreview, imageAnalysis).toTypedArray()
                    val cam = cameraProvider.bindToLifecycle(
                        lifecycleOwner, cameraSelector, *useCases
                    )
                    onCameraReady(cam)
                } catch (_: Throwable) {
                    // ignore: camera unavailable
                }

            } else {
                // ── MJPEG ──────────────────────────────────────────────────
                // No PreviewView — the on-screen image comes from the same JPEG
                // pipeline that feeds the client, so what you see = what client gets
                h264Encoder?.stop()
                h265Encoder?.stop()

                val initialRotation = if (isReverseLandscape)
                    android.view.Surface.ROTATION_270
                else
                    android.view.Surface.ROTATION_90

                val imageAnalysis = ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
                    .setResolutionSelector(targetResolutionSelector(android.util.Size(resolution.width, resolution.height)))
                    // Seed with the current isReverseLandscape state at bind time — the
                    // LaunchedEffect(isReverseLandscape, mjpegImageAnalysisRef.value) above
                    // re-applies this whenever either changes afterward (Rotate button,
                    // sensor auto-detect, or flipCamera() rebuilding this use case fresh).
                    .setTargetRotation(initialRotation)
                    .also { builder ->
                        val extender = androidx.camera.camera2.interop.Camera2Interop.Extender(builder)
                        extender.setCaptureRequestOption(
                            android.hardware.camera2.CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,
                            android.util.Range(15, 30)
                        )
                        extender.setSessionCaptureCallback(captureCallback)
                    }
                    .build()
                mjpegImageAnalysisRef.value = imageAnalysis

                imageAnalysis.setAnalyzer(executor) { imageProxy: ImageProxy ->
                    imageProxy.use {
                        // Calculate FPS
                        val nowNs = System.nanoTime()
                        if (lastFrameTimeNs != 0L) {
                            val frameDuration = nowNs - lastFrameTimeNs
                            val instantFps = 1_000_000_000.0f / frameDuration
                            smoothedFps = if (smoothedFps == 0f) instantFps else smoothedFps * 0.9f + instantFps * 0.1f
                        }
                        lastFrameTimeNs = nowNs

                        if (nowNs - lastUiUpdateTimeNs >= 500_000_000L) { // 500ms
                            onStatsUpdated(
                                CameraStats(
                                    fps = smoothedFps,
                                    iso = currentIso,
                                    exposureFraction = currentExposureFraction
                                )
                            )
                            lastUiUpdateTimeNs = nowNs
                        }
                        val w = imageProxy.width
                        val h = imageProxy.height

                        // Use exact w*h sizes to avoid UV plane padding artifacts
                        val ySize = w * h
                        val uvSize = w * h / 2
                        val nv21 = ByteArray(ySize + uvSize)

                        // Y plane — copy row by row to skip row padding
                        val yPlane = imageProxy.planes[0]
                        val yRowStride = yPlane.rowStride
                        if (yRowStride == w) {
                            yPlane.buffer.get(nv21, 0, ySize)
                        } else {
                            for (row in 0 until h) {
                                yPlane.buffer.position(row * yRowStride)
                                yPlane.buffer.get(nv21, row * w, w)
                            }
                        }

                        // V then U — same order as before, row by row to skip padding
                        val uBuffer = imageProxy.planes[1].buffer
                        val vBuffer = imageProxy.planes[2].buffer
                        val uRowStride = imageProxy.planes[1].rowStride
                        val vRowStride = imageProxy.planes[2].rowStride
                        val uPixelStride = imageProxy.planes[1].pixelStride
                        val vPixelStride = imageProxy.planes[2].pixelStride
                        var uvDst = ySize
                        for (row in 0 until h / 2) {
                            for (col in 0 until w / 2) {
                                nv21[uvDst++] = vBuffer.get(row * vRowStride + col * vPixelStride)
                                nv21[uvDst++] = uBuffer.get(row * uRowStride + col * uPixelStride)
                            }
                        }

                        val yuvImage = android.graphics.YuvImage(
                            nv21, android.graphics.ImageFormat.NV21,
                            imageProxy.width, imageProxy.height, null
                        )
                        val stream = java.io.ByteArrayOutputStream()
                        yuvImage.compressToJpeg(
                            android.graphics.Rect(0, 0, imageProxy.width, imageProxy.height),
                            jpegQualityState.value, stream
                        )
                        val rotation = imageProxy.imageInfo.rotationDegrees
                        val rawJpeg = stream.toByteArray()
                        val jpeg = rotateJpegInMemory(rawJpeg, rotation)

                        // Send to network client — logic unchanged
                        onFrameReady(jpeg)

                        // Decode for on-screen preview — reuse the already rotation-corrected jpeg.
                        if (renderPreviewState.value) {
                            val bitmap = BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size)
                            if (bitmap != null) {
                                imageView.post {
                                    val old = imageView.drawable
                                    imageView.setImageBitmap(bitmap)
                                    if (old is android.graphics.drawable.BitmapDrawable) {
                                        old.bitmap?.recycle()
                                    }
                                }
                            }
                        }
                    }
                }

                try {
                    // No screenPreview — ImageAnalysis only, no PreviewView needed
                    val cam = cameraProvider.bindToLifecycle(
                        lifecycleOwner, cameraSelector, imageAnalysis
                    )
                    onCameraReady(cam)
                } catch (_: Exception) {
                    // ignore: camera unavailable
                }
            }
        }, ContextCompat.getMainExecutor(context))

        try {
            kotlinx.coroutines.awaitCancellation()
        } finally {
            cancelled = true
            boundProvider?.unbindAll()
            // Drop the reference so the LaunchedEffect(configuration) rotation
            // sync above doesn't touch a use case that's no longer bound
            // (e.g. after switching away from MJPEG to H264/H265, or stopping).
            // Safe unconditionally: if this run never bound MJPEG, the ref is
            // already null or was already overwritten by a newer run.
            mjpegImageAnalysisRef.value = null
        }
    }

    // Idle: always PreviewView — codec and quality don't affect preview.
    // Streaming H264: PreviewView stays composed here, but its Preview use case
    // is only bound to the camera above when renderPreview is true — the encoder
    // is fed by ImageAnalysis independently and never depended on Preview being
    // active. The black Box below is now just a one-frame-safe cover so nothing
    // stale flashes on screen during the brief rebind when toggling black screen.
    // Streaming MJPEG: ImageView fed by the JPEG pipeline.
    if (!isStreaming || codec == Codec.H264 || codec == Codec.H265) {
        Box(modifier = modifier) {
            AndroidView(
                factory = { previewContainer },
                modifier = Modifier.fillMaxSize()
            )
            if (isStreaming && !renderPreview) {
                Box(modifier = Modifier.fillMaxSize().background(Color.Black))
            }
        }
    } else {
        AndroidView(factory = { imageView }, modifier = modifier)
    }
}