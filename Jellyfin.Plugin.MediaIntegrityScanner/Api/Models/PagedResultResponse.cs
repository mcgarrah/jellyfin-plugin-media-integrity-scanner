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

using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Api.Models;

/// <summary>
/// Paginated response model for scan results.
/// </summary>
public class PagedResultResponse
{
    /// <summary>Gets or sets the list of scan result items.</summary>
    public List<ScanRecord> Items { get; set; } = new();

    /// <summary>Gets or sets the total count of matching records.</summary>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the current page number.</summary>
    public int Page { get; set; }

    /// <summary>Gets or sets the page size.</summary>
    public int PageSize { get; set; }
}
