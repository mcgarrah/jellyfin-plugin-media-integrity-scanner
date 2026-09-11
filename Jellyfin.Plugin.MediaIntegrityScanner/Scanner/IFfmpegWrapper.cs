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

using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Wraps FFmpeg and FFprobe process execution for media integrity scanning.
/// Extracted (CODE-REVIEW-ARCHITECTURE.md M4) so consumers depend on this
/// abstraction instead of the concrete <see cref="FfmpegWrapper"/> -- the
/// concrete class previously had to make <c>ProbeAsync</c>/<c>DecodeAsync</c>
/// <c>virtual</c> purely so Moq could subclass it for tests.
/// </summary>
public interface IFfmpegWrapper
{
    /// <summary>Gets the currently resolved ffmpeg binary path.</summary>
    string FfmpegPath { get; }

    /// <summary>Gets the currently resolved ffprobe binary path.</summary>
    string FfprobePath { get; }

    /// <summary>Gets a value indicating whether both paths come from an admin-configured override rather than auto-detection.</summary>
    bool IsUsingCustomOverride { get; }

    /// <summary>
    /// Re-resolves both the ffmpeg and ffprobe binary paths and swaps them in
    /// if either changed. Safe to call at any time, including mid-scan -- the
    /// paths are read by reference for each new process launch, so an
    /// in-flight scan keeps using whatever path it already started with.
    /// </summary>
    /// <returns>True if either path actually changed.</returns>
    bool RefreshPaths();

    /// <summary>
    /// Phase 1: Quick header/metadata validation via ffprobe.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scan result indicating pass/fail.</returns>
    Task<ScanResult> ProbeAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Phase 2: Full stream decode validation via ffmpeg.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scan result indicating pass/fail.</returns>
    Task<ScanResult> DecodeAsync(string filePath, CancellationToken cancellationToken);
}
