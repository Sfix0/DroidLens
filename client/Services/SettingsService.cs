using System;
using System.Text.Json;

namespace DroidLens.Client.Services;

public record AppSettings(
    string LastHost    = "192.168.1.100",
    string LastCodec   = "MJPEG",
    string LastQuality = "MEDIUM",
    // Mirrors LastQuality/LastCodec — persists the PC-selected resolution
    // (SD/HD/FHD) across app restarts, so the phone doesn't need to remember
    // it independently on the PC side (Android still persists its own copy).
    string LastResolution = "HD",
    string Language    = "en",
    string Theme       = "Dark",
    // Device keys (currently: device model name — see MainViewModel's
    // DeviceAutoConnectKey) for which auto-reconnect-on-rediscovery is
    // enabled. HashSet<string> round-trips fine through System.Text.Json.
    HashSet<string>? AutoConnectDevices = null,

    // Which transport the user was actually connected over when they enabled
    // auto-connect for a given device key (see SetAutoConnectEnabled) — true
    // means USB, false/absent-from-dict means Wi-Fi. Drives the priority in
    // MainViewModel.TryAutoConnect: whichever transport the user chose at
    // toggle-time wins the race when both are available on rediscovery.
    // Nullable/optional so settings.json files saved before this existed
    // just deserialize with every key defaulting to "prefer Wi-Fi", which
    // was the only behavior before anyway.
    Dictionary<string, bool>? AutoConnectPreferUsb = null,

    // ── App-behavior settings (SettingsWindow) ──────────────────────────────
    // Both are about a *deliberate* teardown while the command channel is
    // still alive (manual Disconnect click, or closing the PC client) — NOT
    // about reacting to ConnectionHealth.Disconnected, since by the time that
    // fires the socket is already gone and there's no one left to send
    // "stop" to. See CommandClient.StopStreamAsync / DisconnectBtn_Click.
    bool StopStreamOnDisconnect = true,
    bool StopStreamOnAppClose   = true,

    // ── Preview rendering (SettingsPanel, Interface section) ────────────────
    // Halves the preview surface's own render rate (skips every other decoded
    // frame before the UI hand-off — see PreviewArea.axaml.cs) purely to cut
    // CPU/GPU cost of the window itself. Never touches the phone's stream
    // (bitrate/fps) or the virtual camera, both of which keep receiving every
    // frame regardless of this setting.
    bool LimitPreviewFps = false,

    // Ambient light fill (letterbox filler) — see PreviewArea.axaml.cs:
    // ShouldUpdateAmbientFill/UpdateAmbientFill. Samples averaged edge colors
    // off the decoded frame and animates two soft color bars behind the
    // letterbox gap on portrait streams, in place of flat black bars.
    // Defaults on since the effect is deliberately cheap (no bitmap, no
    // per-frame GPU blur pass), but exposed as its own switch so it can be
    // ruled out (or just turned off on weaker hardware) independently of
    // LimitPreviewFps.
    bool AmbientFillEnabled = true,

    // ── Screenshot save location (Preview pause/screenshot feature) ─────────
    // null means "use the default" (Pictures\DroidLens — computed at the call
    // site, not stored here, so a future change to the default doesn't require
    // migrating already-saved settings.json files). Only ever non-null once
    // the user explicitly picks a custom folder in SettingsPanel.
    string? ScreenshotPath = null,

    // ── UI Scale (SettingsPanel, Interface section) ─────────────────────────
    // Multiplier for window dimensions and root content scaling (0.75 to 1.25).
    // Defaults to 1.0 (980x640 base size).
    double UiScale = 1.0
);

/// <summary>
/// Persists user settings to %AppData%\DroidLens\settings.json
/// </summary>
public class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DroidLens", "settings.json"
    );

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public AppSettings Current { get; private set; } = new();

    // True only for the Load() call where settings.json didn't exist yet —
    // i.e. genuinely the first launch. Lets callers (MainViewModel) fall back
    // to OS-detected defaults (theme, language) instead of the hardcoded
    // record defaults above, without that detection ever overriding a
    // value the user already saved on a later run.
    public bool IsFirstRun { get; private set; }

    public void Load(Func<string>? detectLanguage = null, Func<bool>? detectIsDark = null)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                IsFirstRun = true;

                // Seed OS-detected defaults in place of the hardcoded record
                // defaults ("en" / "Dark") — still only in-memory, nothing is
                // written to disk until the caller explicitly Save()s.
                if (detectLanguage is not null || detectIsDark is not null)
                {
                    Current = Current with
                    {
                        Language = detectLanguage?.Invoke() ?? Current.Language,
                        Theme    = detectIsDark is null ? Current.Theme : (detectIsDark() ? "Dark" : "Light"),
                    };
                }

                return;
            }
            string json = File.ReadAllText(FilePath);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
        }
        catch { Current = new(); }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Current = settings;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, _opts));
        }
        catch { /* non-critical */ }
    }

    public void UpdateHost(string host)    => Save(Current with { LastHost    = host });
    public void UpdateCodec(string codec)  => Save(Current with { LastCodec   = codec });
    public void UpdateQuality(string q)    => Save(Current with { LastQuality = q });
    public void UpdateResolution(string r) => Save(Current with { LastResolution = r });
    public void UpdateLanguage(string lang)=> Save(Current with { Language    = lang });
    public void UpdateTheme(string theme)  => Save(Current with { Theme       = theme });

    // ── App-behavior settings ────────────────────────────────────────────────
    public void UpdateStopStreamOnDisconnect(bool enabled) =>
        Save(Current with { StopStreamOnDisconnect = enabled });

    public void UpdateStopStreamOnAppClose(bool enabled) =>
        Save(Current with { StopStreamOnAppClose = enabled });

    public void UpdateLimitPreviewFps(bool enabled) =>
        Save(Current with { LimitPreviewFps = enabled });

    public void UpdateAmbientFillEnabled(bool enabled) =>
        Save(Current with { AmbientFillEnabled = enabled });

    // null clears back to the default (Pictures\DroidLens) — see ScreenshotPath
    // doc comment on AppSettings above.
    public void UpdateScreenshotPath(string? path) =>
        Save(Current with { ScreenshotPath = path });

    public void UpdateUiScale(double scale) =>
        Save(Current with { UiScale = scale });

    // ── Per-device auto-connect ─────────────────────────────────────────────
    // "deviceKey" is currently the device model string (e.g. "Pixel 8"), since
    // that's the only stable-ish identifier the phone currently sends over
    // Wi-Fi (see CommandServer.sendDeviceInfo — no serial/UUID today). USB
    // devices could use the adb serial instead if this is ever wired up there.

    public bool IsAutoConnectEnabled(string deviceKey) =>
        !string.IsNullOrWhiteSpace(deviceKey) &&
        Current.AutoConnectDevices is { } set &&
        set.Contains(deviceKey);

    // True if the user last enabled auto-connect for this device while
    // connected over USB — meaning USB should win the race over Wi-Fi on
    // rediscovery instead of the default Wi-Fi-preferred behavior. Falls
    // back to false (prefer Wi-Fi) for keys with no recorded preference,
    // which covers both genuinely-never-set-over-USB devices and settings
    // saved before this preference existed.
    public bool PrefersUsbAutoConnect(string deviceKey) =>
        !string.IsNullOrWhiteSpace(deviceKey) &&
        Current.AutoConnectPreferUsb is { } prefs &&
        prefs.TryGetValue(deviceKey, out bool prefersUsb) &&
        prefersUsb;

    public void SetAutoConnectEnabled(string deviceKey, bool enabled, bool isUsb = false)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) return;

        var set = new HashSet<string>(Current.AutoConnectDevices ?? new HashSet<string>());
        var prefs = new Dictionary<string, bool>(Current.AutoConnectPreferUsb ?? new Dictionary<string, bool>());

        if (enabled)
        {
            set.Add(deviceKey);
            // Record which transport was active right now — this is the
            // "how the user turned it on" signal TryAutoConnect uses later.
            prefs[deviceKey] = isUsb;
        }
        else
        {
            set.Remove(deviceKey);
            prefs.Remove(deviceKey);
        }

        Save(Current with { AutoConnectDevices = set, AutoConnectPreferUsb = prefs });
    }
}