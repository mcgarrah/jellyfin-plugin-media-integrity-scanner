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

using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Data;

/// <summary>
/// Interface for the scan results database manager.
/// </summary>
public interface IDatabaseManager
{
    /// <summary>
    /// Saves or updates a scan result record.
    /// </summary>
    /// <param name="record">The scan record to persist.</param>
    /// <returns>A task representing the async operation.</returns>
    Task SaveResultAsync(ScanRecord record);

    /// <summary>
    /// Checks if an item already has a passing scan result at or above the given
    /// phase, for the file's current contents (file hasn't changed since that scan).
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="filePath">The current file path.</param>
    /// <param name="minPhase">The minimum <see cref="Scanner.ScanPhase"/> (as an int) required for the existing result to count.</param>
    /// <returns>True if an existing scan result at or above <paramref name="minPhase"/> is still valid.</returns>
    Task<bool> IsCurrentAsync(string itemId, string filePath, int minPhase);

    /// <summary>
    /// Gets a summary of scan statistics.
    /// </summary>
    /// <returns>Scan statistics.</returns>
    Task<ScanStatistics> GetStatisticsAsync();

    /// <summary>
    /// Removes all scan records for a given item.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID to purge.</param>
    /// <returns>A task representing the async operation.</returns>
    Task PurgeItemAsync(string itemId);

    /// <summary>
    /// Initializes the database schema.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    Task InitializeAsync();

    /// <summary>
    /// Creates a consistent point-in-time snapshot of the database (via SQLite's
    /// <c>VACUUM INTO</c>, safe to run against a live WAL-mode database without
    /// stopping the scanner) into a timestamped file under a <c>backups</c>
    /// subdirectory next to the live database.
    /// </summary>
    /// <returns>The file name (not full path) of the backup that was created.</returns>
    Task<string> BackupAsync();

    /// <summary>
    /// Lists available backups, newest first.
    /// </summary>
    /// <returns>Metadata for every backup file found.</returns>
    Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync();

    /// <summary>
    /// Replaces the live database with the contents of a previously-created backup.
    /// The caller is responsible for ensuring no scan is in progress before calling this.
    /// </summary>
    /// <param name="backupFileName">The backup file name, as returned by <see cref="ListBackupsAsync"/> -- not a full path.</param>
    /// <returns>A task representing the async operation.</returns>
    Task RestoreAsync(string backupFileName);
}

/// <summary>
/// Metadata describing one on-disk database backup.
/// </summary>
public class DatabaseBackupInfo
{
    /// <summary>
    /// Gets or sets the backup file's name (not full path).
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the backup file's size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the backup's creation timestamp, ISO 8601 UTC.
    /// </summary>
    public string CreatedUtc { get; set; } = string.Empty;
}

/// <summary>
/// Summary statistics for scan results. Reflects only items that have at least
/// one scan record — the database has no notion of a library's total item
/// count or truly "pending" (never-scanned) items; callers with access to
/// <c>ILibraryManager</c> should derive those from the real library count.
/// Pass/fail/error counts use each item's most authoritative (highest scan
/// phase) result, even if it was scanned in both phases. <see cref="ScannedFiles"/>
/// and <see cref="DeepScannedFiles"/> are phase-specific, since a header scan and
/// a deep scan are independent passes over the library and a file can be
/// "current" for one while still pending the other.
/// </summary>
public class ScanStatistics
{
    /// <summary>
    /// Gets or sets the number of distinct items that have at least a Header-phase
    /// scan result (a FullDecode result also satisfies this, since it implies the
    /// header was read successfully too).
    /// </summary>
    public int ScannedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of distinct items that have a FullDecode-phase
    /// (deep) scan result specifically.
    /// </summary>
    public int DeepScannedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose most recent scan passed.
    /// </summary>
    public int PassedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose most recent scan failed.
    /// </summary>
    public int FailedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose most recent scan ended in an error.
    /// </summary>
    public int ErroredFiles { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last completed scan.
    /// </summary>
    public string? LastScanTimestamp { get; set; }
}
