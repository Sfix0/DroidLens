# DroidLens

> Turn your Android phone into a high-performance wireless or USB webcam for Windows, with a DirectShow virtual camera output compatible with OBS, Discord, Zoom, and web browsers.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-0078D6?logo=windows)](client/)
[![Platform: Android](https://img.shields.io/badge/Platform-Android%208.0%2B-3DDC84?logo=android)](android/)
[![UI: Avalonia](https://img.shields.io/badge/PC%20UI-Avalonia%2011-8B5CF6)](client/)
[![UI: Jetpack Compose](https://img.shields.io/badge/Android%20UI-Jetpack%20Compose-4285F4)](android/)

---

## 📁 Repository Structure

This monorepo contains both sides of the DroidLens ecosystem:

```
DroidLens/
├── client/     # Windows PC Client (C# 13 / .NET 10 / Avalonia UI / DirectShow Virtual Camera)
└── android/    # Android Application (Kotlin / Jetpack Compose / CameraX / MediaCodec)
```

* [**`client/`**](./client) — The Windows desktop receiver, hardware decoder (D3D11VA via FFmpeg), DirectShow virtual camera driver, and telemetry monitor.
* [**`android/`**](./android) — The Android camera streamer with hardware-accelerated H.264/H.265 encoders, mDNS discovery, and OLED-friendly black screen mode.

---

## ✨ Key Highlights

* **Two Transports, One Workflow:** Automatic discovery over local Wi-Fi via mDNS (`_DroidLens._tcp.`) or plug-and-play USB connection through ADB port forwarding.
* **H.265 (HEVC), H.264 & MJPEG:** Real-time hardware encoding on Android (`MediaCodec`) and hardware decoding on Windows (FFmpeg `D3D11VA` with CPU fallback).
* **DirectShow Virtual Camera:** Registered system-wide via `softcam.dll` — appears as a standard webcam in OBS Studio, Discord, Google Meet, Zoom, etc.
* **Low-Latency Bounded Pipeline:** Drop-to-keyframe queue strategy keeps latency sub-second without accumulating delay.
* **Live Telemetry & Diagnostics:** RTT ping/pong heartbeat, decode latency, FPS, bitrate, battery percentage, and battery temperature.
* **Consistent Glass Design:** Fluent glassmorphic theme with Dark & Light variants matching across both Android and Windows clients.
* **OLED Black Screen Mode:** Keeps camera active while turning the phone screen completely black to eliminate heat and battery drain during long streams.

---

## 🌐 Network Protocol & Ports

| Port | Transport | Direction | Purpose |
|:---:|:---:|:---:|:---|
| **`7878`** | HTTP Multipart | Phone → PC | MJPEG video fallback stream |
| **`7879`** | TCP / JSON | Bidirectional | Commands, state, ping/pong heartbeat, telemetry |
| **`7880`** | TCP Raw | Phone → PC | Length-prefixed H.264 Annex-B NAL stream |
| **`7881`** | TCP Raw | Phone → PC | Length-prefixed H.265 (HEVC) Annex-B NAL stream |

---

## 🚀 Getting Started

### 1. Download Releases
Pre-built Windows binaries and Android APKs are available on the [**Releases**](https://github.com/Sfix0/DroidLens/releases) page.

### 2. Build from Source
* **Windows Client:** See the [Client README](./client/README.md) for build requirements and instructions (`dotnet publish`).
* **Android App:** See the [Android README](./android/README.md) for Gradle build instructions (`./gradlew assembleRelease`).

---

## 📜 License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.
