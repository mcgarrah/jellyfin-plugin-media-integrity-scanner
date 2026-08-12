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
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ScheduledTasks;

/// <summary>
/// Scheduled task that runs an integrity check, then a <c>VACUUM</c> if it
/// passes, against the plugin's own SQLite database. Only runs if enabled in
/// plugin settings, and skips itself (rather than blocking) if a scan is
/// currently in progress.
/// </summary>
public partial class DatabaseMaintenanceTask : IScheduledTask
{
    private readonly SqliteDatabaseManager _db;
    private readonly IScanEngine _scanner;
    private readonly ILogger<DatabaseMaintenanceTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseMaintenanceTask"/> class.
    /// </summary>
    /// <param name="db">Database manager.</param>
    /// <param name="scanner">Scan engine, used to check whether a scan is active.</param>
    /// <param name="logger">Logger instance.</param>
    public DatabaseMaintenanceTask(SqliteDatabaseManager db, IScanEngine scanner, ILogger<DatabaseMaintenanceTask> logger)
    {
        _db = db;
        _scanner = scanner;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Media Integrity - Database Maintenance";

    /// <inheritdoc />
    public string Key => "MediaIntegrityDatabaseMaintenance";

    /// <inheritdoc />
    public string Description =>
        "Runs an integrity check, then a VACUUM if it passes, against the plugin's own SQLite database. " +
        "Skips itself while a scan is in progress, and only runs if enabled in plugin settings.";

    /// <inheritdoc />
    public string Category => "Media Integrity";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Weekly, Sunday 2 AM -- after Deep Scan's 1 AM slot, before
        // Header Scan's 3 AM slot and Check for Updates' 4 AM slot.
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration?.EnableAutoDatabaseMaintenance != true)
        {
            LogMaintenanceDisabled();
            progress.Report(100);
            return;
        }

        if (_scanner.IsScanning)
        {
            LogSkippedDuringScan();
            progress.Report(100);
            return;
        }

        LogMaintenanceStarting();
        var result = await _db.RunMaintenanceAsync().ConfigureAwait(false);
        if (!result.IntegrityCheckOk)
        {
            LogIntegrityCheckFailed(result.IntegrityCheckMessage ?? "unknown");
        }

        progress.Report(100);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Automatic database maintenance is disabled in plugin configuration. Skipping.")]
    private partial void LogMaintenanceDisabled();

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Skipping database maintenance -- a scan is currently in progress")]
    private partial void LogSkippedDuringScan();

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Starting scheduled database maintenance")]
    private partial void LogMaintenanceStarting();

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Database integrity check failed during scheduled maintenance: {Message}")]
    private partial void LogIntegrityCheckFailed(string message);
}
