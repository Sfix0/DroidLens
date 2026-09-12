# Third-party licenses — DroidLens PC Client

| Component | License | Source |
|---|---|---|
| Avalonia UI | MIT | https://github.com/AvaloniaUI/Avalonia |
| Sdcb.FFmpeg (bindings) | LGPL-3.0-only | https://github.com/sdcb/Sdcb.FFmpeg |
| FFmpeg.LGPL (native runtime: avcodec/avformat/…) | LGPL-3.0-or-later | https://www.nuget.org/packages/FFmpeg.LGPL / upstream https://ffmpeg.org |
| softcam (DirectShow virtual camera filter) | MIT | https://github.com/tshino/softcam |
| DirectShow Base Classes (Microsoft samples) | MIT | https://github.com/microsoft/Windows-classic-samples |
| Makaretu.Dns.Multicast.New (mDNS / DNS-SD) | MIT | https://github.com/StephenCleary/Makaretu.Dns.Multicast |
| AdvancedSharpAdbClient (USB / ADB transport) | Apache-2.0 | https://github.com/quamotion/madb |
| Material.Icons.Avalonia | MIT | https://github.com/SKProCH/Material.Icons.Avalonia |
| JetBrains.Annotations | MIT | https://github.com/JetBrains/annotations |

## Notes

- **FFmpeg (LGPL).** The app ships LGPL FFmpeg binaries (via the `FFmpeg.LGPL`
  NuGet package) and links them dynamically at runtime — no FFmpeg code is
  compiled into the app itself. Corresponding FFmpeg sources are available
  upstream at https://ffmpeg.org/download.html.
- **ADB / platform-tools** (`adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`) are
  **not** bundled in this repository (Google's Android SDK license forbids
  redistribution). Contributors download them manually into `Tools/` — see the
  client README, "Building from source".
