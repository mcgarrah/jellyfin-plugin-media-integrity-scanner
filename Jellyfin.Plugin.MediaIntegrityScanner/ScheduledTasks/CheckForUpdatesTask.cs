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
using Jellyfin.Plugin.MediaIntegrityScanner.Updates;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ScheduledTasks;

/// <summary>
/// Scheduled task that refreshes the cached plugin update status, so the
/// dashboard and REST API don't make a live network-dependent check on every
/// page load.
/// </summary>
public partial class CheckForUpdatesTask : IScheduledTask
{
    private readonly IUpdateChecker _updateChecker;
    private readonly ILogger<CheckForUpdatesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CheckForUpdatesTask"/> class.
    /// </summary>
    /// <param name="updateChecker">Update checker.</param>
    /// <param name="logger">Logger instance.</param>
    public CheckForUpdatesTask(IUpdateChecker updateChecker, ILogger<CheckForUpdatesTask> logger)
    {
        _updateChecker = updateChecker;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Media Integrity - Check for Updates";

    /// <inheritdoc />
    public string Key => "MediaIntegrityCheckForUpdates";

    /// <inheritdoc />
    public string Description =>
        "Checks for newer plugin versions from registered stable/development plugin repositories.";

    /// <inheritdoc />
    public string Category => "Media Integrity";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        LogCheckStarting();
        var status = await _updateChecker.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (status.UpdateAvailable)
        {
            LogUpdateAvailable(status.Channel.ToString(), status.AvailableVersion ?? "unknown");
        }

        progress.Report(100);
    }

    [LoggerMessage(EventId = 22, Level = LogLevel.Information, Message = "Checking for plugin updates")]
    private partial void LogCheckStarting();

    [LoggerMessage(EventId = 23, Level = LogLevel.Information, Message = "Plugin update available on {Channel} channel: {Version}")]
    private partial void LogUpdateAvailable(string channel, string version);
}
