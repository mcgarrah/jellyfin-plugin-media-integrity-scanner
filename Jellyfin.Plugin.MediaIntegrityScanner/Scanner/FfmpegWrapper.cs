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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Wraps FFmpeg and FFprobe process execution for media integrity scanning.
/// </summary>
public class FfmpegWrapper
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly ILogger<FfmpegWrapper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegWrapper"/> class.
    /// </summary>
    /// <param name="resolver">FFmpeg binary resolver.</param>
    /// <param name="logger">Logger instance.</param>
    public FfmpegWrapper(
        FfmpegResolver resolver,
        ILogger<FfmpegWrapper> logger)
    {
        _ffmpegPath = resolver.ResolveFfmpegPath();
        _ffprobePath = resolver.ResolveFfprobePath();
        _logger = logger;

        _logger.LogInformation("FFmpeg resolved to: {Path}", _ffmpegPath);
        _logger.LogInformation("FFprobe resolved to: {Path}", _ffprobePath);
    }

    /// <summary>
    /// Phase 1: Quick header/metadata validation via ffprobe.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scan result indicating pass/fail.</returns>
    public async Task<ScanResult> ProbeAsync(string filePath, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var (exitCode, _, stderr) = await RunProcessAsync(
            _ffprobePath,
            new[] { "-v", "error", "-show_entries", "format=duration,size",
                    "-show_entries", "stream=codec_type,codec_name",
                    "-of", "json", filePath },
            cancellationToken).ConfigureAwait(false);

        sw.Stop();

        var success = exitCode == 0 && string.IsNullOrWhiteSpace(stderr);
        if (!success)
        {
            _logger.LogDebug(
                "Probe failed for {File}: exit={ExitCode}, stderr={Stderr}",
                filePath, exitCode, stderr);
        }

        return new ScanResult
        {
            Success = success,
            ErrorOutput = string.IsNullOrWhiteSpace(stderr) ? null : stderr,
            DurationMs = (int)sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Phase 2: Full decode — reads every frame, outputs nothing.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scan result indicating pass/fail.</returns>
    public async Task<ScanResult> DecodeAsync(string filePath, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var (exitCode, _, stderr) = await RunProcessAsync(
            _ffmpegPath,
            new[] { "-v", "error", "-i", filePath, "-f", "null", "-" },
            cancellationToken).ConfigureAwait(false);

        sw.Stop();

        var success = exitCode == 0 && string.IsNullOrWhiteSpace(stderr);
        if (!success)
        {
            _logger.LogDebug(
                "Decode failed for {File}: exit={ExitCode}, stderr={Stderr}",
                filePath, exitCode, stderr);
        }

        return new ScanResult
        {
            Success = success,
            ErrorOutput = string.IsNullOrWhiteSpace(stderr) ? null : stderr,
            DurationMs = (int)sw.ElapsedMilliseconds
        };
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string exe, string[] args, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(cancellationToken),
                stdoutTask,
                stderrTask).ConfigureAwait(false);

            return (
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore process exit race conditions during termination
            }

            throw;
        }
    }
}
