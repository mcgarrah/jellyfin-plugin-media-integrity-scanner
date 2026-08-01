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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ScheduledTasks;

/// <summary>
/// Scheduled task for Phase 2 full byte-stream decode scanning.
/// Only runs if deep scanning is enabled in plugin configuration.
/// </summary>
public partial class DeepScanTask : IScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly IScanEngine _scanner;
    private readonly IDatabaseManager _db;
    private readonly ILogger<DeepScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepScanTask"/> class.
    /// </summary>
    /// <param name="library">Library manager.</param>
    /// <param name="scanner">Scan engine.</param>
    /// <param name="db">Database manager.</param>
    /// <param name="logger">Logger instance.</param>
    public DeepScanTask(
        ILibraryManager library,
        IScanEngine scanner,
        IDatabaseManager db,
        ILogger<DeepScanTask> logger)
    {
        _library = library;
        _scanner = scanner;
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Media Integrity - Deep Scan";

    /// <inheritdoc />
    public string Key => "MediaIntegrityDeepScan";

    /// <inheritdoc />
    public string Description =>
        "Full byte-stream decode of media files using ffmpeg. " +
        "Detects mid-file corruption that header checks miss. " +
        "Only runs if deep scanning is enabled in plugin settings.";

    /// <inheritdoc />
    public string Category => "Media Integrity";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Deep scan runs weekly by default (Sunday at 1 AM)
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(1).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDeepScan != true)
        {
            LogDeepScanDisabled();
            progress.Report(100);
            return;
        }

        LogDeepScanStarting();

        var items = _library.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video, MediaType.Audio },
            IsVirtualItem = false
        });

        var total = items.Count;
        var processed = 0;

        LogDeepScanItemCount(total);

        var maxConcurrent = Math.Max(1, config?.MaxConcurrentScans ?? 1);
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrent, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                // Skip if already deep-scanned and file unchanged
                if (!await _db.IsCurrentAsync(item.Id.ToString(), item.Path, (int)ScanPhase.FullDecode).ConfigureAwait(false))
                {
                    await _scanner.ScanItemAsync(item, ScanPhase.FullDecode, ct).ConfigureAwait(false);
                }

                var done = Interlocked.Increment(ref processed);
                progress.Report((double)done / total * 100);
            }).ConfigureAwait(false);

        LogDeepScanComplete(processed, total);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Deep scan is disabled in plugin configuration. Skipping.")]
    private partial void LogDeepScanDisabled();

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Starting scheduled deep scan")]
    private partial void LogDeepScanStarting();

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Deep scan: {Count} items to process")]
    private partial void LogDeepScanItemCount(int count);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Deep scan complete: {Processed}/{Total} items processed")]
    private partial void LogDeepScanComplete(int processed, int total);
}
