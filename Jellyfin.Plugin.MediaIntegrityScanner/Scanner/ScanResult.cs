namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Result of a single file integrity scan.
/// </summary>
public class ScanResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the scan passed without errors.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the stderr output from ffmpeg/ffprobe on failure.
    /// </summary>
    public string? ErrorOutput { get; set; }

    /// <summary>
    /// Gets or sets the scan duration in milliseconds.
    /// </summary>
    public int DurationMs { get; set; }
}

/// <summary>
/// Scan phase enumeration.
/// </summary>
public enum ScanPhase
{
    /// <summary>
    /// Phase 1: Quick header and metadata validation via ffprobe.
    /// </summary>
    Header = 1,

    /// <summary>
    /// Phase 2: Full byte-stream decode via ffmpeg.
    /// </summary>
    FullDecode = 2
}

/// <summary>
/// Scan status enumeration.
/// </summary>
public enum ScanStatus
{
    /// <summary>
    /// File is queued but not yet scanned.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// File passed integrity check.
    /// </summary>
    Pass = 1,

    /// <summary>
    /// File failed integrity check.
    /// </summary>
    Fail = 2,

    /// <summary>
    /// Scan encountered an error (e.g., file not accessible).
    /// </summary>
    Error = 3
}
