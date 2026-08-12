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
using System.Linq;
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
/// Scheduled task that diffs the plugin's own scan-history database against
/// the real library contents and purges anything no longer present. The
/// periodic counterpart to <see cref="EventHandlers.LibraryMonitor"/>'s
/// event-driven purge -- catches whatever that missed (plugin offline at
/// removal time, purge-on-remove was off, an unverified Jellyfin removal path
/// that doesn't fire <c>ItemRemoved</c>). Only runs if enabled in plugin
/// settings, and skips itself (rather than blocking) if a scan is currently
/// in progress.
/// </summary>
public partial class MediaIntegrityReconciliationTask : IScheduledTask
{
    private readonly IDatabaseManager _db;
    private readonly ILibraryManager _library;
    private readonly IScanEngine _scanner;
    private readonly ILogger<MediaIntegrityReconciliationTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaIntegrityReconciliationTask"/> class.
    /// </summary>
    /// <param name="db">Database manager.</param>
    /// <param name="library">Library manager, for the current set of valid item IDs.</param>
    /// <param name="scanner">Scan engine, used to check whether a scan is active.</param>
    /// <param name="logger">Logger instance.</param>
    public MediaIntegrityReconciliationTask(
        IDatabaseManager db,
        ILibraryManager library,
        IScanEngine scanner,
        ILogger<MediaIntegrityReconciliationTask> logger)
    {
        _db = db;
        _library = library;
        _scanner = scanner;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Media Integrity - Reconcile Scan History";

    /// <inheritdoc />
    public string Key => "MediaIntegrityReconciliation";

    /// <inheritdoc />
    public string Description =>
        "Purges scan-history records for items no longer in the library, catching anything " +
        "the event-driven purge missed. Skips itself while a scan is in progress, and only " +
        "runs if enabled in plugin settings.";

    /// <inheritdoc />
    public string Category => "Media Integrity";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Weekly, Sunday 2:30 AM -- between Database Maintenance's 2 AM slot
        // and Header Scan's 3 AM slot.
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromMinutes(150).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration?.EnableAutoReconciliation != true)
        {
            LogReconciliationDisabled();
            progress.Report(100);
            return;
        }

        if (_scanner.IsScanning)
        {
            LogSkippedDuringScan();
            progress.Report(100);
            return;
        }

        LogReconciliationStarting();

        var currentItemIds = _library
            .GetItemIds(new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video, MediaType.Audio },
                IsVirtualItem = false
            })
            .Select(id => id.ToString())
            .ToArray();

        var purged = await _db.ReconcileAsync(currentItemIds).ConfigureAwait(false);
        LogReconciliationCompleted(purged);

        progress.Report(100);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Automatic reconciliation is disabled in plugin configuration. Skipping.")]
    private partial void LogReconciliationDisabled();

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Skipping reconciliation -- a scan is currently in progress")]
    private partial void LogSkippedDuringScan();

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Starting scheduled reconciliation")]
    private partial void LogReconciliationStarting();

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Reconciliation complete: {Purged} orphaned scan-history rows purged")]
    private partial void LogReconciliationCompleted(int purged);
}
