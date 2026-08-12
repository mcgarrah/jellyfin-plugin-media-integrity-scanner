// Jellyfin Media Integrity Scanner - validates media file integrity using FFmpeg
// Copyright (C) 2026  Michael McGarrah <mcgarrah@gmail.com>
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 2 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along
// with this program; if not, see <https://www.gnu.org/licenses/>.

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

    /// <summary>
    /// Gets or sets how the file was decoded (0 = not applicable/Header phase,
    /// 1 = software, 2 = hardware). See <see cref="Scanner.DecodeMode"/>.
    /// </summary>
    public int DecodeMode { get; set; }

    /// <summary>
    /// Gets or sets the specific hardware backend used (e.g. "cuda", "vaapi"),
    /// when <see cref="DecodeMode"/> is Hardware. Null otherwise.
    /// </summary>
    public string? HardwareAccelType { get; set; }
}
