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
/// Request model for triggering a scan.
/// </summary>
public class ScanRequest
{
    /// <summary>Gets or sets an optional item ID to scan a specific file.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets an optional library ID to scope the scan.</summary>
    public string? LibraryId { get; set; }

    /// <summary>Gets or sets a value indicating whether to run a deep (Phase 2) scan.</summary>
    public bool DeepScan { get; set; }

    /// <summary>
    /// Gets or sets an optional case-insensitive name filter. Matched against a
    /// movie's title or an episode's series title -- e.g. "Simpsons" scopes the
    /// scan to every episode of that show, not just an episode literally titled
    /// "Simpsons".
    /// </summary>
    public string? NameFilter { get; set; }

    /// <summary>
    /// Gets or sets an optional set of season numbers to restrict TV episodes
    /// to (e.g. <c>[1, 2, 3]</c>). Ignored for movies and other non-episode items.
    /// </summary>
    public int[]? Seasons { get; set; }
}
