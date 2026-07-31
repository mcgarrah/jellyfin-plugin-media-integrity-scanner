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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Bounded, thread-safe scan engine that processes files with configurable throttling.
/// </summary>
public partial class ScanEngine : IScanEngine, IDisposable
{
    private readonly SemaphoreSlim _scanLock;
    private readonly FfmpegWrapper _ffmpeg;
    private readonly IDatabaseManager _db;
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly ILogger<ScanEngine> _logger;
    private CancellationTokenSource? _cts;
    private int _activeScanCount;
    private int _isLibraryScanning;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanEngine"/> class.
    /// </summary>
    /// <param name="ffmpeg">FFmpeg wrapper for executing scans.</param>
    /// <param name="db">Database manager for persisting results.</param>
    /// <param name="sessions">Session manager for playback awareness.</param>
    /// <param name="library">Library manager for querying items.</param>
    /// <param name="logger">Logger instance.</param>
    public ScanEngine(
        FfmpegWrapper ffmpeg,
        IDatabaseManager db,
        ISessionManager sessions,
        ILibraryManager library,
        ILogger<ScanEngine> logger)
    {
        _ffmpeg = ffmpeg;
        _db = db;
        _sessions = sessions;
        _library = library;
        _logger = logger;

        var maxConcurrent = Plugin.Instance?.Configuration?.MaxConcurrentScans ?? 1;
        _scanLock = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <inheritdoc />
    public bool IsScanning => Volatile.Read(ref _activeScanCount) > 0 || Volatile.Read(ref _isLibraryScanning) > 0;

    /// <inheritdoc />
    public async Task ScanItemAsync(BaseItem item, ScanPhase phase, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, (_cts ??= new CancellationTokenSource()).Token);
        var token = linkedCts.Token;

        await _scanLock.WaitAsync(token).ConfigureAwait(false);
        Interlocked.Increment(ref _activeScanCount);
        try
        {
            // Check playback pause
            if (ShouldPauseForPlayback())
            {
                LogPausingForPlayback();
                await WaitForPlaybackEnd(token).ConfigureAwait(false);
            }

            // Apply inter-file delay
            var config = Plugin.Instance?.Configuration;
            var delay = config?.DelayBetweenFilesMs ?? 5000;
            if (delay > 0)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }

            // Execute scan
            LogScanning(item.Path, (int)phase);

            var result = phase switch
            {
                ScanPhase.Header => await _ffmpeg.ProbeAsync(item.Path, token).ConfigureAwait(false),
                ScanPhase.FullDecode => await _ffmpeg.DecodeAsync(item.Path, token).ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown scan phase: {phase}", nameof(phase))
            };

            // Persist result
            var fileInfo = new FileInfo(item.Path);
            await _db.SaveResultAsync(new ScanRecord
            {
                ItemId = item.Id.ToString(),
                FilePath = item.Path,
                FileSize = fileInfo.Exists ? fileInfo.Length : null,
                LastModified = fileInfo.Exists
                    ? fileInfo.LastWriteTimeUtc.ToString("O")
                    : null,
                ScanPhase = (int)phase,
                ScanStatus = result.Success
                    ? (int)ScanStatus.Pass
                    : (int)ScanStatus.Fail,
                ScanTimestamp = DateTime.UtcNow.ToString("O"),
                ErrorOutput = result.ErrorOutput,
                ScanDurationMs = result.DurationMs
            }).ConfigureAwait(false);

            if (result.Success)
            {
                LogScanPassed(item.Path);
            }
            else
            {
                var firstError = GetFirstLine(result.ErrorOutput);
                LogScanFailed(item.Path, firstError);
            }
        }
        catch (OperationCanceledException)
        {
            LogScanCancelled(item.Path);
            throw;
        }
        catch (Exception ex)
        {
            LogScanError(ex, item.Path);

            // Record the error
            await _db.SaveResultAsync(new ScanRecord
            {
                ItemId = item.Id.ToString(),
                FilePath = item.Path,
                ScanPhase = (int)phase,
                ScanStatus = (int)ScanStatus.Error,
                ScanTimestamp = DateTime.UtcNow.ToString("O"),
                ErrorOutput = ex.Message
            }).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeScanCount);
            _scanLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ScanLibraryAsync(string? libraryId, ScanPhase phase, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, (_cts ??= new CancellationTokenSource()).Token);
        var token = linkedCts.Token;

        Interlocked.Exchange(ref _isLibraryScanning, 1);

        try
        {
            var query = new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video, MediaType.Audio },
                IsVirtualItem = false
            };

            if (!string.IsNullOrEmpty(libraryId) && Guid.TryParse(libraryId, out var parentId))
            {
                query.ParentId = parentId;
            }

            var items = _library.GetItemList(query);
            LogLibraryScanStarting(items.Count, phase);

            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();

                // Skip if already scanned and file unchanged
                if (await _db.IsCurrentAsync(item.Id.ToString(), item.Path).ConfigureAwait(false))
                {
                    continue;
                }

                await ScanItemAsync(item, phase, token).ConfigureAwait(false);
            }

            LogLibraryScanComplete();
        }
        finally
        {
            Interlocked.Exchange(ref _isLibraryScanning, 0);
        }
    }

    /// <inheritdoc />
    public void Cancel()
    {
        LogScanCancellationRequested();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _isLibraryScanning, 0);
    }

    private bool ShouldPauseForPlayback()
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.PauseDuringPlayback != true)
        {
            return false;
        }

        return _sessions.Sessions.Any(s => s.NowPlayingItem != null);
    }

    private async Task WaitForPlaybackEnd(CancellationToken cancellationToken)
    {
        while (ShouldPauseForPlayback() && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? GetFirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var idx = text.IndexOf('\n');
        return idx >= 0 ? text.Substring(0, idx).TrimEnd('\r') : text;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _scanLock.Dispose();
            _cts?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Pausing scan — active playback detected")]
    private partial void LogPausingForPlayback();

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Scanning {File} (Phase {Phase})")]
    private partial void LogScanning(string file, int phase);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Scan passed: {File}")]
    private partial void LogScanPassed(string file);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Scan failed: {File} — {Error}")]
    private partial void LogScanFailed(string file, string? error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Scan cancelled for {File}")]
    private partial void LogScanCancelled(string file);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Error scanning {File}")]
    private partial void LogScanError(Exception ex, string file);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Starting library scan: {Count} items, phase={Phase}")]
    private partial void LogLibraryScanStarting(int count, ScanPhase phase);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Library scan complete")]
    private partial void LogLibraryScanComplete();

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Scan cancellation requested")]
    private partial void LogScanCancellationRequested();
}
