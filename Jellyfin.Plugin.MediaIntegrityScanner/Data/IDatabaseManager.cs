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
    /// Checks if an item's scan result is current (file hasn't changed since last scan).
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="filePath">The current file path.</param>
    /// <returns>True if the existing scan result is still valid.</returns>
    Task<bool> IsCurrentAsync(string itemId, string filePath);

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
}

/// <summary>
/// Summary statistics for scan results.
/// </summary>
public class ScanStatistics
{
    /// <summary>
    /// Gets or sets the total number of media files in the library.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of files that have been scanned.
    /// </summary>
    public int ScannedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of files that passed the scan.
    /// </summary>
    public int PassedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of files that failed the scan.
    /// </summary>
    public int FailedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of files pending scan.
    /// </summary>
    public int PendingFiles { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last completed scan.
    /// </summary>
    public string? LastScanTimestamp { get; set; }
}
