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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Bounded, thread-safe scan engine that processes files with configurable throttling.
/// </summary>
public partial class ScanEngine : IScanEngine, IDisposable
{
    private static readonly TimeSpan BandwidthPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly SemaphoreSlim _scanLock;
    private readonly FfmpegWrapper _ffmpeg;
    private readonly IDatabaseManager _db;
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly ILogger<ScanEngine> _logger;
    private readonly SharedBandwidthLimiter _bandwidthLimiter;
    private CancellationTokenSource? _cts;
    private int _activeScanCount;
    private int _isLibraryScanning;
    private int _currentLibraryScanPhase;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanEngine"/> class.
    /// </summary>
    /// <param name="ffmpeg">FFmpeg wrapper for executing scans.</param>
    /// <param name="db">Database manager for persisting results.</param>
    /// <param name="sessions">Session manager for playback awareness.</param>
    /// <param name="library">Library manager for querying items.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="bandwidthLimiter">
    /// Shared bandwidth budget. Optional -- no DI registration exists for
    /// this type, so the default (a real, wall-clock-driven instance) is
    /// what production always gets; tests can supply one built with a fake
    /// <see cref="TimeProvider"/> for deterministic control.
    /// </param>
    public ScanEngine(
        FfmpegWrapper ffmpeg,
        IDatabaseManager db,
        ISessionManager sessions,
        ILibraryManager library,
        ILogger<ScanEngine> logger,
        SharedBandwidthLimiter? bandwidthLimiter = null)
    {
        _ffmpeg = ffmpeg;
        _db = db;
        _sessions = sessions;
        _library = library;
        _logger = logger;
        _bandwidthLimiter = bandwidthLimiter ?? new SharedBandwidthLimiter();

        var maxConcurrent = Plugin.Instance?.Configuration?.MaxConcurrentScans ?? 1;
        _scanLock = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <inheritdoc />
    public bool IsScanning => Volatile.Read(ref _activeScanCount) > 0 || Volatile.Read(ref _isLibraryScanning) > 0;

    /// <inheritdoc />
    public int? CurrentPhase => Volatile.Read(ref _isLibraryScanning) > 0
        ? Volatile.Read(ref _currentLibraryScanPhase)
        : (int?)null;

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
            // Check quiet-hours window
            if (IsOutsideQuietHours())
            {
                LogWaitingForQuietHours();
                await WaitForQuietHours(token).ConfigureAwait(false);
            }

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

            // Spend this file's size against the shared bandwidth budget
            // before moving on -- draws from the same budget regardless of
            // phase or how many scans are running concurrently, so
            // concurrency divides the configured cap rather than
            // multiplying past it. See SharedBandwidthLimiter's remarks for
            // why this replaced a per-file-independent calculation.
            var fileInfo = new FileInfo(item.Path);
            if (fileInfo.Exists)
            {
                var fileSizeMb = fileInfo.Length / (1024.0 * 1024.0);
                var maxRate = config?.MaxReadRateMbPerSec ?? 0;
                if (!_bandwidthLimiter.TryConsume(fileSizeMb, maxRate, phase))
                {
                    if (phase == ScanPhase.FullDecode)
                    {
                        _bandwidthLimiter.MarkDeepScanWaiting();
                    }

                    try
                    {
                        while (!_bandwidthLimiter.TryConsume(fileSizeMb, maxRate, phase))
                        {
                            await Task.Delay(BandwidthPollInterval, token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (phase == ScanPhase.FullDecode)
                        {
                            _bandwidthLimiter.MarkDeepScanNoLongerWaiting();
                        }
                    }
                }
            }

            // Persist result
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
                ScanDurationMs = result.DurationMs,
                DecodeMode = (int)result.DecodeMode,
                HardwareAccelType = result.HardwareAccelType
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

            // Record the error. For a FullDecode attempt, record which decode
            // mode was in play at the time -- same resolution FfmpegWrapper
            // itself would have used -- so a hardware-decode-related failure
            // isn't indistinguishable from a software one after the fact.
            var attemptedHwAccelFlag = phase == ScanPhase.FullDecode
                ? FfmpegWrapper.ResolveHwAccelFlag(Plugin.Instance?.Configuration?.HardwareAccelerationType ?? MediaBrowser.Model.Entities.HardwareAccelerationType.none)
                : null;
            var attemptedDecodeMode = phase switch
            {
                ScanPhase.FullDecode => attemptedHwAccelFlag is null ? DecodeMode.Software : DecodeMode.Hardware,
                _ => DecodeMode.NotApplicable
            };

            await _db.SaveResultAsync(new ScanRecord
            {
                ItemId = item.Id.ToString(),
                FilePath = item.Path,
                ScanPhase = (int)phase,
                ScanStatus = (int)ScanStatus.Error,
                ScanTimestamp = DateTime.UtcNow.ToString("O"),
                ErrorOutput = ex.Message,
                DecodeMode = (int)attemptedDecodeMode,
                HardwareAccelType = attemptedHwAccelFlag
            }).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeScanCount);
            _scanLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ScanLibraryAsync(
        string? libraryId,
        ScanPhase phase,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null,
        string? nameFilter = null,
        IReadOnlyCollection<int>? seasons = null)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, (_cts ??= new CancellationTokenSource()).Token);
        var token = linkedCts.Token;

        Interlocked.Exchange(ref _currentLibraryScanPhase, (int)phase);
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

            IReadOnlyList<BaseItem> items = _library.GetItemList(query);

            if (!string.IsNullOrWhiteSpace(nameFilter))
            {
                items = items.Where(item => MatchesNameFilter(item, nameFilter)).ToList();
            }

            if (seasons is { Count: > 0 })
            {
                var seasonSet = new HashSet<int>(seasons);
                items = items.Where(item => item is not Episode episode || (episode.ParentIndexNumber.HasValue && seasonSet.Contains(episode.ParentIndexNumber.Value))).ToList();
            }

            var total = items.Count;
            var processed = 0;
            LogLibraryScanStarting(total, phase);

            // Pre-seed the whole not-yet-current backlog as Pending, upfront, before any
            // actual scanning starts -- so the results table (and "Pending Only" filter)
            // show the full in-flight queue immediately, rather than only ever reflecting
            // scans that have already completed. This is a separate pass from the
            // Parallel.ForEachAsync loop below on purpose: that loop is bounded by
            // MaxConcurrentScans and only reaches later items once earlier ones finish, so
            // seeding inline there would only reveal the queue a few items at a time instead
            // of showing the real backlog size right away. The per-item IsCurrentAsync check
            // below still runs again regardless -- cheap, and guards against a race where
            // something else (a manual trigger, the other phase's own scan) completed an
            // item in the gap between this pass and its actual turn.
            var pendingBatch = new List<(string ItemId, string FilePath)>();
            foreach (var item in items)
            {
                if (!await _db.IsCurrentAsync(item.Id.ToString(), item.Path, (int)phase).ConfigureAwait(false))
                {
                    pendingBatch.Add((item.Id.ToString(), item.Path));
                }
            }

            await _db.MarkPendingAsync(pendingBatch, (int)phase).ConfigureAwait(false);

            var maxConcurrent = Math.Max(1, Plugin.Instance?.Configuration?.MaxConcurrentScans ?? 1);
            await Parallel.ForEachAsync(
                items,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrent, CancellationToken = token },
                async (item, ct) =>
                {
                    // Skip if already scanned at this phase (or higher) and file unchanged
                    if (!await _db.IsCurrentAsync(item.Id.ToString(), item.Path, (int)phase).ConfigureAwait(false))
                    {
                        await ScanItemAsync(item, phase, ct).ConfigureAwait(false);
                    }

                    var done = Interlocked.Increment(ref processed);
                    progress?.Report(total == 0 ? 100 : (double)done / total * 100);
                }).ConfigureAwait(false);

            LogLibraryScanComplete(processed, total);
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

    private static bool IsOutsideQuietHours()
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.UseQuietHoursOnly != true)
        {
            return false;
        }

        return !ScanThrottle.IsWithinQuietHours(config.QuietHoursStart, config.QuietHoursEnd, DateTime.Now.TimeOfDay);
    }

    private static async Task WaitForQuietHours(CancellationToken cancellationToken)
    {
        while (IsOutsideQuietHours() && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        }
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

    /// <summary>
    /// Checks whether an item matches a scan-scope name filter. Episodes are
    /// matched against their series title, not their own episode title --
    /// admins scoping a scan by name mean "just this show", and an episode's
    /// own <see cref="BaseItem.Name"/> would only ever match one specific
    /// episode by coincidence.
    /// </summary>
    private static bool MatchesNameFilter(BaseItem item, string nameFilter)
    {
        var name = item is Episode episode ? episode.SeriesName : item.Name;
        return !string.IsNullOrEmpty(name) && name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
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

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Pausing scan — outside configured quiet-hours window")]
    private partial void LogWaitingForQuietHours();

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

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Library scan complete: {Processed}/{Total} items processed")]
    private partial void LogLibraryScanComplete(int processed, int total);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Scan cancellation requested")]
    private partial void LogScanCancellationRequested();
}
