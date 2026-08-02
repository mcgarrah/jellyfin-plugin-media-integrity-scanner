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

using System;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Updates;

/// <summary>
/// Snapshot of the plugin's current version against the latest versions
/// discovered across whichever plugin repositories the Jellyfin admin has
/// registered.
/// </summary>
public class UpdateStatus
{
    /// <summary>
    /// Gets or sets the currently running plugin version.
    /// </summary>
    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the latest version found from the stable manifest, if any.
    /// </summary>
    public string? LatestStableVersion { get; set; }

    /// <summary>
    /// Gets or sets the latest version found from the development manifest, if any.
    /// </summary>
    public string? LatestDevVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a newer version is available
    /// for the currently configured <see cref="UpdateChannel"/>.
    /// </summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// Gets or sets the version that would be installed for the currently
    /// configured channel, when <see cref="UpdateAvailable"/> is <see langword="true"/>.
    /// </summary>
    public string? AvailableVersion { get; set; }

    /// <summary>
    /// Gets or sets which channel this status was computed against.
    /// </summary>
    public UpdateChannel Channel { get; set; }

    /// <summary>
    /// Gets or sets when this status was computed.
    /// </summary>
    public DateTime CheckedAt { get; set; }
}
