package com.droidlens.app

import android.Manifest
import android.os.Bundle
import android.view.OrientationEventListener
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.lifecycle.lifecycleScope
import com.droidlens.app.ui.screen.StreamingScreen
import com.droidlens.app.ui.theme.DroidLensTheme
import com.droidlens.app.ui.screen.SplashScreen
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsControllerCompat
import android.content.pm.ActivityInfo
import android.annotation.SuppressLint
import kotlinx.coroutines.launch
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue

@ExperimentalCamera2Interop
class MainActivity : ComponentActivity() {

    private val viewModel: StreamViewModel by viewModels()

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { }

    // While true, auto-detection from the sensor updates isReverseLandscape.
    // Becomes false right after the user manually presses Rotate once,
    // and physical rotation stops changing anything after that (by design).
    private var autoOrientationEnabled = true

    private val orientationListener by lazy {
        object : OrientationEventListener(this) {
            override fun onOrientationChanged(degrees: Int) {
                if (degrees == ORIENTATION_UNKNOWN) return
                if (!autoOrientationEnabled) return
                if (!viewModel.isLandscape.value) return

                // Only the two landscape zones matter; portrait angles are ignored.
                val reverse = when (degrees) {
                    in 225..314 -> false  // phone rotated 180° from "normal" landscape
                    in 45..134 -> true    // "normal" landscape
                    else -> return
                }
                viewModel.setReverseLandscapeFromSensor(reverse)
            }
        }
    }

    @SuppressLint("SourceLockedOrientationActivity")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT

        lifecycleScope.launch {
            viewModel.isLandscape.collect { landscape ->
                requestedOrientation = if (landscape)
                    ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                else
                    ActivityInfo.SCREEN_ORIENTATION_PORTRAIT

                // New stream — re-enable auto-detection until the user manually presses Rotate.
                autoOrientationEnabled = true
                if (landscape && orientationListener.canDetectOrientation()) {
                    orientationListener.enable()
                } else {
                    orientationListener.disable()
                }
            }
        }

        lifecycleScope.launch {
            var isFirstValue = true
            viewModel.isReverseLandscape.collect { reverse ->
                // Skip the initial value emitted on subscription so it isn't
                // mistaken for a "manual" Rotate press.
                if (isFirstValue) {
                    isFirstValue = false
                } else if (!viewModel.isReverseLandscapeFromSensor) {
                    // The value didn't come from the sensor — so it's a manual user action.
                    autoOrientationEnabled = false
                }
                // Only applies during streaming (isLandscape = true)
                if (viewModel.isLandscape.value) {
                    requestedOrientation = if (reverse)
                        ActivityInfo.SCREEN_ORIENTATION_REVERSE_LANDSCAPE
                    else
                        ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
                }
            }
        }

        permissionLauncher.launch(
            arrayOf(Manifest.permission.CAMERA, Manifest.permission.INTERNET)
        )
        WindowCompat.setDecorFitsSystemWindows(window, false)
        setContent {
            DroidLensTheme {
                // App UI here is dark-first (streaming/camera UI), so keep
                // system bar icons/content light regardless of system theme,
                // and don't let the platform paint a light scrim behind them.
                val insetsController = remember(window) {
                    WindowInsetsControllerCompat(window, window.decorView)
                }
                LaunchedEffect(Unit) {
                    insetsController.isAppearanceLightStatusBars = false
                    insetsController.isAppearanceLightNavigationBars = false
                }

                var showSplash by remember { mutableStateOf(true) }
                if (showSplash) {
                    SplashScreen(onFinished = { showSplash = false })
                } else {
                    StreamingScreen()
                }
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        orientationListener.disable()
    }
}