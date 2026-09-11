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
/// Response model for a manual ffmpeg/ffprobe path re-resolution.
/// </summary>
public class FfmpegRefreshResult
{
    /// <summary>Gets or sets a value indicating whether either resolved path actually changed.</summary>
    public bool Changed { get; set; }

    /// <summary>Gets or sets the currently resolved ffmpeg path.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the currently resolved ffprobe path.</summary>
    public string FfprobePath { get; set; } = string.Empty;
}
