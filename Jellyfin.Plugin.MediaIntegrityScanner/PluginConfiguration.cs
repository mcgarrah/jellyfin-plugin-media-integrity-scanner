using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaIntegrityScanner;

/// <summary>
/// Plugin configuration for Media Integrity Scanner.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the maximum number of files scanned concurrently.
    /// </summary>
    public int MaxConcurrentScans { get; set; } = 1;

    /// <summary>
    /// Gets or sets the delay in milliseconds between scanning each file.
    /// </summary>
    public int DelayBetweenFilesMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets a value indicating whether scanning pauses during active playback.
    /// </summary>
    public bool PauseDuringPlayback { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Phase 2 deep scanning is enabled.
    /// </summary>
    public bool EnableDeepScan { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum read rate in MB/s for scanning I/O.
    /// </summary>
    public int MaxReadRateMbPerSec { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether scanning is restricted to quiet hours.
    /// </summary>
    public bool UseQuietHoursOnly { get; set; } = false;

    /// <summary>
    /// Gets or sets the start of the quiet hours window (HH:mm format).
    /// </summary>
    public string QuietHoursStart { get; set; } = "02:00";

    /// <summary>
    /// Gets or sets the end of the quiet hours window (HH:mm format).
    /// </summary>
    public string QuietHoursEnd { get; set; } = "06:00";

    /// <summary>
    /// Gets or sets a user-specified override path for the ffmpeg binary.
    /// </summary>
    public string? FfmpegPathOverride { get; set; }

    /// <summary>
    /// Gets or sets a user-specified override path for the ffprobe binary.
    /// </summary>
    public string? FfprobePathOverride { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether newly added items are scanned automatically.
    /// </summary>
    public bool ScanOnItemAdded { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether scan records are purged when items are removed.
    /// </summary>
    public bool PurgeOnItemRemoved { get; set; } = true;
}
