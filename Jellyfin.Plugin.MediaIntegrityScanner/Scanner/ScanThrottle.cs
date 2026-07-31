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
using System.Globalization;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Pure, dependency-free helpers for scan pacing: quiet-hours window checks
/// and read-rate throttling delay calculations. Kept free of Jellyfin server
/// types so the logic can be unit tested directly.
/// </summary>
public static class ScanThrottle
{
    /// <summary>
    /// Determines whether the given time of day falls within the configured
    /// quiet-hours window. Supports windows that wrap past midnight
    /// (e.g., 22:00-06:00). Fails open (returns true) if either bound cannot
    /// be parsed, so a misconfigured window never blocks scanning entirely.
    /// </summary>
    /// <param name="start">Window start, "HH:mm" format.</param>
    /// <param name="end">Window end, "HH:mm" format.</param>
    /// <param name="timeOfDay">The time of day to check.</param>
    /// <returns>True if scanning should be allowed at this time.</returns>
    public static bool IsWithinQuietHours(string? start, string? end, TimeSpan timeOfDay)
    {
        if (!TimeSpan.TryParse(start, CultureInfo.InvariantCulture, out var startTime) ||
            !TimeSpan.TryParse(end, CultureInfo.InvariantCulture, out var endTime))
        {
            return true;
        }

        if (startTime == endTime)
        {
            // A zero-width window is treated as "always on" rather than "never."
            return true;
        }

        return startTime < endTime
            ? timeOfDay >= startTime && timeOfDay < endTime
            : timeOfDay >= startTime || timeOfDay < endTime;
    }

    /// <summary>
    /// Computes the additional delay needed after a scan so the average
    /// read rate for that file does not exceed the configured cap. Pads
    /// wall-clock time after the fact rather than throttling the ffmpeg/ffprobe
    /// read itself, so it never risks breaking non-seekable-input edge cases
    /// (e.g., MP4/MOV files with the moov atom at the end).
    /// </summary>
    /// <param name="fileSizeBytes">Size of the scanned file in bytes.</param>
    /// <param name="maxReadRateMbPerSec">Configured cap in MB/s. Zero or negative disables throttling.</param>
    /// <param name="actualDurationMs">How long the scan actually took.</param>
    /// <returns>The additional delay to wait, or <see cref="TimeSpan.Zero"/> if none is needed.</returns>
    public static TimeSpan ComputeReadRateDelay(long fileSizeBytes, int maxReadRateMbPerSec, int actualDurationMs)
    {
        if (maxReadRateMbPerSec <= 0 || fileSizeBytes <= 0)
        {
            return TimeSpan.Zero;
        }

        var fileSizeMb = fileSizeBytes / (1024.0 * 1024.0);
        var minDurationMs = fileSizeMb / maxReadRateMbPerSec * 1000.0;
        var extraMs = minDurationMs - actualDurationMs;

        return extraMs > 0 ? TimeSpan.FromMilliseconds(extraMs) : TimeSpan.Zero;
    }
}
