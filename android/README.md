# DroidLens — Android Application

The mobile camera streamer for DroidLens.

## <img src="../assets/icons/photo_camera.svg" width="20" height="20"> Screenshots

<p align="center">
  <img src="../assets/screenshots/android-live-stream.jpg" alt="DroidLens Android app — live streaming with camera controls" width="800" />
  <br />
  <em>Live stream — camera preview with Stats, lens, rotate, black-screen, flashlight controls and STOP</em>
</p>

<table>
  <tr>
    <td align="center" width="35%">
      <img src="../assets/screenshots/android-idle.png" alt="Ready to stream screen with lens, quality and codec selection" width="270" />
      <br />
      <em>Ready to stream — front lens, quality and codec (H.265) selection</em>
    </td>
    <td align="center" width="65%">
      <img src="../assets/screenshots/android-preview-paused.jpg" alt="Live stream stats overlay with FPS, ISO, codec and quality" width="520" />
      <br />
      <em>Stream stats — live FPS, ISO/exposure, codec and quality overlay</em>
    </td>
  </tr>
</table>

## <img src="../assets/icons/handyman.svg" width="20" height="20"> Tech Stack
* **Language:** Kotlin
* **UI Framework:** [Jetpack Compose](https://developer.android.com/jetpack/compose) + Material 3
* **Camera API:** [AndroidX CameraX 1.4](https://developer.android.com/training/camerax)
* **Hardware Encoding:** Android `MediaCodec` (H.264 & H.265/HEVC low-latency encoding)
* **Discovery:** Android NSD (`NsdManager` / DNS-SD / mDNS)
* **Design & Effects:** [Haze](https://github.com/chrisbanes/haze) (Glassmorphic blur with auto-disable in Power Save mode)

## <img src="../assets/icons/smartphone.svg" width="20" height="20"> Requirements
* Android 8.0+ (API level 26 or higher)
* Device with Camera2 support

## <img src="../assets/icons/package.svg" width="20" height="20"> Building from Source
Using Gradle:

* **Debug Build:**
  ```bash
  ./gradlew assembleDebug
  ```
* **Release Build:**
  ```bash
  ./gradlew assembleRelease
  ```

Output APK:
`app/build/outputs/apk/release/app-release.apk`
