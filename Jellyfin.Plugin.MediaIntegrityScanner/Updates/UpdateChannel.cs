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

namespace Jellyfin.Plugin.MediaIntegrityScanner.Updates;

/// <summary>
/// Which release channel to check for updates against.
/// </summary>
public enum UpdateChannel
{
    /// <summary>
    /// Only tagged, stable releases (this project's <c>manifest.json</c>).
    /// </summary>
    Stable = 0,

    /// <summary>
    /// Includes pre-release/development builds (this project's
    /// <c>manifest-unstable.json</c>), in addition to stable releases.
    /// </summary>
    Development = 1
}
