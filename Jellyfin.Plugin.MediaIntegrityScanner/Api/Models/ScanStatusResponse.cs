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

namespace Jellyfin.Plugin.MediaIntegrityScanner.Api.Models;

/// <summary>
/// Response model for scan status.
/// </summary>
public class ScanStatusResponse
{
    /// <summary>Gets or sets a value indicating whether a scan is in progress.</summary>
    public bool IsScanning { get; set; }

    /// <summary>Gets or sets the <see cref="Scanner.ScanPhase"/> (as an int) the current library scan is running, or null when idle or between library scans.</summary>
    public int? CurrentPhase { get; set; }

    /// <summary>Gets or sets the library ID the current library scan is scoped to, or null when idle/unscoped.</summary>
    public string? CurrentLibraryId { get; set; }

    /// <summary>Gets or sets the name filter the current library scan is scoped to, or null when idle/unscoped.</summary>
    public string? CurrentNameFilter { get; set; }

    /// <summary>Gets or sets the season filter the current library scan is scoped to, or null when idle/unscoped.</summary>
    public IReadOnlyCollection<int>? CurrentSeasons { get; set; }

    /// <summary>Gets or sets the total number of tracked files.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Gets or sets the number of files that have been scanned.</summary>
    public int ScannedFiles { get; set; }

    /// <summary>Gets or sets the number of files that passed.</summary>
    public int PassedFiles { get; set; }

    /// <summary>Gets or sets the number of files that failed.</summary>
    public int FailedFiles { get; set; }

    /// <summary>Gets or sets the number of files whose most recent scan ended in an error.</summary>
    public int ErroredFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of files still pending a Header (light/quick) scan.
    /// </summary>
    public int PendingHeaderFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of files still pending a FullDecode (deep) scan.
    /// A file already scanned at the Header phase but not yet deep-scanned
    /// counts here, even though it does not count toward <see cref="PendingHeaderFiles"/>.
    /// </summary>
    public int PendingDeepFiles { get; set; }

    /// <summary>Gets or sets the timestamp of the last scan.</summary>
    public string? LastScanTimestamp { get; set; }

    /// <summary>Gets or sets the library health percentage.</summary>
    public double HealthPercentage { get; set; }
}
