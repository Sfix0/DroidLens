# DroidLens — Android Application

The mobile camera streamer for DroidLens.

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
