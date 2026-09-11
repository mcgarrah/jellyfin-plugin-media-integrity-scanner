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
/// Response model for the bug-report diagnostic snapshot. Deliberately holds
/// only environment/version info and aggregate counts -- never file paths,
/// library names, or error text -- so it's safe to prefill into a public
/// GitHub issue.
/// </summary>
public class DiagnosticsResponse
{
    /// <summary>Gets or sets the currently running plugin version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured update channel (Stable/Dev).</summary>
    public string UpdateChannel { get; set; } = string.Empty;

    /// <summary>Gets or sets the running Jellyfin server version.</summary>
    public string JellyfinServerVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the server OS description (e.g. "Linux ... " or "Microsoft Windows ...").</summary>
    public string OperatingSystem { get; set; } = string.Empty;

    /// <summary>Gets or sets the .NET runtime description.</summary>
    public string DotNetVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether an admin-configured ffmpeg/ffprobe path override is in use.</summary>
    public bool UsingCustomFfmpegOverride { get; set; }

    /// <summary>Gets or sets the resolved ffmpeg path, or a placeholder if a custom override is configured.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the resolved ffprobe path, or a placeholder if a custom override is configured.</summary>
    public string FfprobePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured hardware acceleration type.</summary>
    public string HardwareAccelerationType { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured maximum concurrent scans.</summary>
    public int MaxConcurrentScans { get; set; }

    /// <summary>Gets or sets the total number of scanned files.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Gets or sets the number of files that passed.</summary>
    public int PassedFiles { get; set; }

    /// <summary>Gets or sets the number of files that failed.</summary>
    public int FailedFiles { get; set; }

    /// <summary>Gets or sets the number of files whose most recent scan ended in an error.</summary>
    public int ErroredFiles { get; set; }

    /// <summary>Gets or sets the library health percentage.</summary>
    public double HealthPercentage { get; set; }
}
