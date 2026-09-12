namespace DroidLens.Client.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum VideoCodec { MJPEG, H264, H265 }
public enum StreamQuality { LOW, MEDIUM, HIGH }
// Selectable only from the PC client — mirrors the Android-side enum
// (SD/HD/FHD map to 480p/720p/1080p, all 16:9). Sent as "SD"/"HD"/"FHD".
public enum StreamResolution { SD, HD, FHD }

// ── JSON message models (received from Android) ───────────────────────────────

public record DeviceInfo(
    string Model,
    string Android
);

public record BatteryInfo(
    int Level,
    bool Charging,
    double Temp
);

public record NetworkStatus(
    int SignalLevel,
    string Ip
);

public record StreamStatus(
    bool Active,
    int Rotation,
    string Codec,
    string Quality,
    string Resolution,
    bool FrontCamera,
    bool Flashlight,
    bool BlackScreen
);

// ── Main app state (reactive, used by ViewModel) ──────────────────────────────

public class DeviceState
{
    public bool IsConnected { get; set; } = false;
    public string Host { get; set; } = "192.168.1.100";

    public DeviceInfo? Device { get; set; }
    public BatteryInfo? Battery { get; set; }
    public StreamStatus? Stream { get; set; }
    public NetworkStatus? Network { get; set; }

    public VideoCodec CurrentCodec { get; set; } = VideoCodec.MJPEG;
    public StreamResolution CurrentResolution { get; set; } = StreamResolution.HD;
    public int Rotation { get; set; } = 0;
    public bool IsStreaming { get; set; } = false;
}