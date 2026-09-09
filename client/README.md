# DroidLens — Windows PC Client

The desktop receiver for DroidLens: it discovers the phone, decodes its camera stream with hardware acceleration, shows a live preview with telemetry, and re-exposes the stream as a system-wide DirectShow virtual camera for OBS, Discord, Zoom, and browsers.

## <img src="../assets/icons/photo_camera.svg" width="20" height="20"> Screenshots

<p align="center">
  <img src="../assets/screenshots/client-live-stream.png" alt="DroidLens PC client — live H.264 stream with telemetry" width="800" />
  <br />
  <em>Live stream — Full HD (1920×1080) over Wi-Fi with RTT, decode and resolution telemetry</em>
</p>

<table>
  <tr>
    <td align="center" width="50%">
      <img src="../assets/screenshots/client-discovery.png" alt="Auto-discovery with USB and Wi-Fi devices" width="400" />
      <br />
      <em>Auto-discovery — USB (127.0.0.1) and Wi-Fi (192.168.31.9) devices</em>
    </td>
    <td align="center" width="50%">
      <img src="../assets/screenshots/client-connected-idle.png" alt="Connected device in idle state, dark theme" width="400" />
      <br />
      <em>Connected & idle — battery, temperature, bitrate and codec selection (H.265 / HD)</em>
    </td>
  </tr>
  <tr>
    <td align="center" width="50%">
      <img src="../assets/screenshots/client-preview-paused.png" alt="Preview rendering paused, virtual camera keeps streaming" width="400" />
      <br />
      <em>Preview paused — local preview off, virtual camera keeps streaming</em>
    </td>
    <td align="center" width="50%">
      <img src="../assets/screenshots/client-light-theme.png" alt="Light theme with connected device" width="400" />
      <br />
      <em>Light theme — same workflow in Fluent light variant</em>
    </td>
  </tr>
  <tr>
    <td align="center" colspan="2">
      <img src="../assets/screenshots/client-settings-light-theme.png" alt="Settings dialog with theme, language and preview options" width="600" />
      <br />
      <em>Settings — theme, language, UI scale, preview FPS limit and ambient light fill</em>
    </td>
  </tr>
</table>

## <img src="../assets/icons/handyman.svg" width="20" height="20"> Tech Stack
* **Runtime:** .NET 10 (`net10.0-windows`, `win-x64`, self-contained)
* **UI Framework:** [Avalonia UI 11](https://avaloniaui.net/) with Fluent styling and Material Icons
* **Video Decoding:** [Sdcb.FFmpeg](https://github.com/sdcb/Sdcb.FFmpeg) 7.0 + `FFmpeg.LGPL` (D3D11VA hardware decoding for H.264 and HEVC, transparent CPU fallback)
* **Virtual Camera:** DirectShow filter powered by [softcam](https://github.com/tshino/softcam) (MIT)
* **Service Discovery:** real mDNS / DNS-SD (`_droidlens._tcp`) via [Makaretu.Dns.Multicast](https://github.com/StephenCleary/Makaretu.Dns.Multicast)
* **USB Transport:** [AdvancedSharpAdbClient](https://github.com/quamotion/madb) driving the bundled `adb.exe` — no system-wide ADB install needed

## <img src="../assets/icons/radar.svg" width="20" height="20"> Discovery & Transports

* **Wi-Fi:** passive mDNS listener for the phone's `_droidlens._tcp` announcement, plus a background re-query every 4 s so a missed announcement doesn't leave a device undiscovered.
* **USB:** polls `adb devices` every 2 s and forwards all four app ports through the active device, so the rest of the app simply talks to it over `127.0.0.1`.
* Both transports feed the same device list, and per-device auto-reconnect remembers which transport you preferred (USB or Wi-Fi).

## <img src="../assets/icons/network.svg" width="20" height="20"> How It Works

Three switchable video pipelines — **MJPEG**, **H.264**, **H.265 (HEVC)** — owned by a router that starts whichever matches the requested codec. H.264/H.265 arrive as length-prefixed Annex-B NAL units and decode through FFmpeg (D3D11VA on Windows when available, CPU otherwise) into a small bounded queue (depth 3), so end-to-end latency stays low even when decode briefly falls behind. Decoded BGRA frames go to the on-screen preview and, in parallel, to the virtual camera.

| Port | Transport | Direction | Purpose |
|:---:|:---:|:---:|:---|
| **`7878`** | HTTP Multipart | Phone → PC | MJPEG video stream |
| **`7879`** | TCP / JSON | Bidirectional | Commands, state, ping/pong heartbeat |
| **`7880`** | TCP Raw | Phone → PC | Length-prefixed H.264 Annex-B NAL stream |
| **`7881`** | TCP Raw | Phone → PC | Length-prefixed H.265 (HEVC) Annex-B NAL stream |

PC → phone commands: `start`, `stop`, `set_codec`, `set_quality`, `flip_camera`, `rotate`, `flashlight`, `black_screen`. The phone pushes back `device_info`, `battery`, `stream_status`, and network state over the same channel.

## <img src="../assets/icons/monitor_heart.svg" width="20" height="20"> Health Monitoring

A `ping`/`pong` heartbeat runs at 1 Hz over the command channel, with three-state reporting:

* **`Live`** — heartbeats flowing, telemetry (RTT, decode latency, throughput, resolution) averaged over 1 s windows so the UI doesn't jitter.
* **`Stale`** — no pong for 4 s; the UI flags the connection but keeps going.
* **`Disconnected`** — no pong for 10 s (30 s grace before the very first pong); a watchdog force-closes the silent socket.

## <img src="../assets/icons/sync.svg" width="20" height="20"> Auto-Reconnect

Devices can be remembered for auto-connect on rediscovery, keyed by the model name the phone reports (e.g. `POCO F6`). If both transports are available, the one you were on when enabling auto-connect wins. Manual `Disconnect` is respected — the client won't immediately re-connect behind your back.

## <img src="../assets/icons/computer.svg" width="20" height="20"> System Requirements
* Windows 10 / 11 x64
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (for building from source)

## <img src="../assets/icons/package.svg" width="20" height="20"> Building a Release Binary
```bash
dotnet publish -c Release -r win-x64 --self-contained true
```
The output binary will be located in:
`bin/Release/net10.0-windows/win-x64/publish/DroidLens.exe`

The publish is intentionally **not** a single-file bundle: `softcam.dll` (a COM filter the registry points at) and `adb.exe` (a real process launched by the ADB client) must live as physical files next to the exe.

## <img src="../assets/icons/videocam.svg" width="20" height="20"> Virtual Camera Registration
Before first use, register the bundled `softcam.dll` as administrator:
```cmd
regsvr32 "%CD%\softcam.dll"
```
Elevation is requested on demand (one UAC prompt) — the app itself runs unprivileged. The client detects whether the filter is registered and whether an existing registration still points at the current `softcam.dll` path. After registration, `DirectShow Softcam` will appear in OBS Studio, Discord, Zoom, Google Meet, and web browsers.

## <img src="../assets/icons/settings.svg" width="20" height="20"> Settings
Persisted to `%AppData%\DroidLens\settings.json`:

* Last host, codec, quality, and resolution
* Language and theme
* Auto-connect devices and per-device transport preference (USB / Wi-Fi)
* Stop stream on manual disconnect / on app close (both default on)
* Limit preview FPS — halves the preview surface render rate to save CPU/GPU; the phone stream and virtual camera are unaffected
* Ambient light fill — fills letterbox bars with a soft color glow sampled from the video instead of flat black
* UI scale (0.75–1.25) and screenshot save location (defaults to `Pictures\DroidLens`)

## <img src="../assets/icons/language.svg" width="20" height="20"> Localization
English and Ukrainian ship out of the box. Translations are plain `.lang` files (`key = value`, `#` comments) in `Assets/Locales/` — drop a new `{code}.lang` next to them and it appears in Settings → Language with no rebuild; missing keys fall back to English.

## <img src="../assets/icons/palette.svg" width="20" height="20"> Theme
Dark (default) and light variants driven by a real design-token system in `App.axaml` rather than ad-hoc colors, matching the Android app's glassmorphic look.
