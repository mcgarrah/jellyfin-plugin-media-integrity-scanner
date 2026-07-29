namespace Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;

/// <summary>
/// Database entity representing a single scan result record.
/// </summary>
public class ScanRecord
{
    /// <summary>
    /// Gets or sets the auto-increment primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin item GUID.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full file path.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes at scan time.
    /// </summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// Gets or sets the file last-modified timestamp (ISO 8601) at scan time.
    /// Used to detect if the file has changed since last scan.
    /// </summary>
    public string? LastModified { get; set; }

    /// <summary>
    /// Gets or sets the scan phase (1 = header, 2 = full decode).
    /// </summary>
    public int ScanPhase { get; set; }

    /// <summary>
    /// Gets or sets the scan status (0 = pending, 1 = pass, 2 = fail, 3 = error).
    /// </summary>
    public int ScanStatus { get; set; }

    /// <summary>
    /// Gets or sets the scan timestamp (ISO 8601).
    /// </summary>
    public string ScanTimestamp { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ffmpeg/ffprobe stderr output on failure.
    /// </summary>
    public string? ErrorOutput { get; set; }

    /// <summary>
    /// Gets or sets the scan duration in milliseconds.
    /// </summary>
    public int? ScanDurationMs { get; set; }
}
