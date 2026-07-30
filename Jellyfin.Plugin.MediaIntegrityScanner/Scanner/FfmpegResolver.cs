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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Resolves the FFmpeg and FFprobe binary paths across platforms.
/// </summary>
public class FfmpegResolver
{
    private readonly IServerConfigurationManager _config;
    private readonly ILogger<FfmpegResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegResolver"/> class.
    /// </summary>
    /// <param name="config">Server configuration manager.</param>
    /// <param name="logger">Logger instance.</param>
    public FfmpegResolver(
        IServerConfigurationManager config,
        ILogger<FfmpegResolver> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the path to the ffmpeg binary.
    /// </summary>
    /// <returns>The full path to ffmpeg.</returns>
    /// <exception cref="InvalidOperationException">Thrown when ffmpeg cannot be found.</exception>
    public string ResolveFfmpegPath()
    {
        return ResolveBinary("ffmpeg", Plugin.Instance?.Configuration?.FfmpegPathOverride);
    }

    /// <summary>
    /// Resolves the path to the ffprobe binary.
    /// </summary>
    /// <returns>The full path to ffprobe.</returns>
    /// <exception cref="InvalidOperationException">Thrown when ffprobe cannot be found.</exception>
    public string ResolveFfprobePath()
    {
        return ResolveBinary("ffprobe", Plugin.Instance?.Configuration?.FfprobePathOverride);
    }

    private string ResolveBinary(string binaryName, string? userOverride)
    {
        // 1. User override from plugin config
        if (!string.IsNullOrEmpty(userOverride))
        {
            if (File.Exists(userOverride))
            {
                return userOverride;
            }

            _logger.LogWarning(
                "Configured {Binary} path not found: {Path}",
                binaryName,
                userOverride);
        }

        // 2. Jellyfin's own configured ffmpeg path
        var serverFfmpeg = _config.GetEncodingOptions().EncoderAppPath;
        if (!string.IsNullOrEmpty(serverFfmpeg))
        {
            // For ffprobe, derive from ffmpeg path
            var resolved = binaryName == "ffprobe"
                ? DeriveProbeFromFfmpeg(serverFfmpeg)
                : serverFfmpeg;

            if (File.Exists(resolved))
            {
                return resolved;
            }
        }

        // 3. Platform-specific known locations
        var candidates = GetPlatformCandidates(binaryName);
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 4. PATH lookup
        var pathResult = FindInPath(binaryName);
        if (pathResult != null)
        {
            return pathResult;
        }

        throw new InvalidOperationException(
            $"{binaryName} not found. Install ffmpeg or configure the path " +
            "in Media Integrity Scanner settings.");
    }

    private static string DeriveProbeFromFfmpeg(string ffmpegPath)
    {
        var dir = Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
        var ext = Path.GetExtension(ffmpegPath);
        return Path.Combine(dir, "ffprobe" + ext);
    }

    private static IEnumerable<string> GetPlatformCandidates(string binaryName)
    {
        if (OperatingSystem.IsLinux())
        {
            yield return $"/usr/lib/jellyfin-ffmpeg/{binaryName}";
            yield return $"/usr/bin/{binaryName}";
            yield return $"/usr/local/bin/{binaryName}";
        }
        else if (OperatingSystem.IsWindows())
        {
            var ext = ".exe";
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Jellyfin", "Server", binaryName + ext);
            yield return $@"C:\ffmpeg\bin\{binaryName}{ext}";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return $"/opt/homebrew/bin/{binaryName}";
            yield return $"/usr/local/bin/{binaryName}";
        }
    }

    private static string? FindInPath(string executable)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        return pathVar.Split(separator)
            .Select(dir => Path.Combine(dir, executable + extension))
            .FirstOrDefault(File.Exists);
    }
}
