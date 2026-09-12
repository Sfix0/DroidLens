# DroidLens — Windows installer (Inno Setup 7)

Bilingual (EN/UK) single-window installer. Registers `softcam.dll` on install,
unregisters + wipes `%AppData%\DroidLens` on uninstall.

## Build

```bat
:: 1. Publish the client (from DroidLens.Client/)
dotnet publish -c Release

:: 2. Compile the installer (Inno Setup 7 required)
"C:\Program Files\Inno Setup 7\ISCC.exe" DroidLens_Installer.iss
```

Output: `Output\DroidLens_Setup_v1.0.0.exe`

On another machine, override the publish folder without editing the script:

```bat
ISCC.exe /DPublishDir="C:\path\to\publish" DroidLens_Installer.iss
```

## Languages

`[Languages]` = `en` + `uk` only, mirroring the app itself. No language dialog —
Inno auto-selects by OS locale (`uk-UA` → Ukrainian, everything else → English),
same rule as the app's first-run detection. All UI strings live in
`[CustomMessages]`; `assets\English.isl` is a minimal stub (standard messages
fall back to Inno's built-in English).
