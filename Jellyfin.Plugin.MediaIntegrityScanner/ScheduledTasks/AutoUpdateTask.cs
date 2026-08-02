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
using Jellyfin.Plugin.MediaIntegrityScanner.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ScheduledTasks;

/// <summary>
/// Weekly scheduled task that, when enabled, automatically installs a newer
/// plugin version for the configured update channel and (only if separately
/// enabled) restarts Jellyfin once no one has an active playback session.
/// Both behaviors are off by default -- conservative installs can leave
/// <see cref="PluginConfiguration.EnableAutoUpdate"/> disabled and only ever
/// update manually via the dashboard's "Update Now" button.
/// </summary>
public partial class AutoUpdateTask : IScheduledTask
{
    private readonly IUpdateChecker _updateChecker;
    private readonly ISessionManager _sessions;
    private readonly ISystemManager _systemManager;
    private readonly ILogger<AutoUpdateTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoUpdateTask"/> class.
    /// </summary>
    /// <param name="updateChecker">Update checker.</param>
    /// <param name="sessions">Session manager, used to detect active playback before an auto-triggered restart.</param>
    /// <param name="systemManager">System manager, used to trigger a restart.</param>
    /// <param name="logger">Logger instance.</param>
    public AutoUpdateTask(
        IUpdateChecker updateChecker,
        ISessionManager sessions,
        ISystemManager systemManager,
        ILogger<AutoUpdateTask> logger)
    {
        _updateChecker = updateChecker;
        _sessions = sessions;
        _systemManager = systemManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Media Integrity - Auto Update";

    /// <inheritdoc />
    public string Key => "MediaIntegrityAutoUpdate";

    /// <inheritdoc />
    public string Description =>
        "If enabled, automatically installs a newer plugin version for the configured update channel, " +
        "and optionally restarts Jellyfin once no one is watching.";

    /// <inheritdoc />
    public string Category => "Media Integrity";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.EnableAutoUpdate)
        {
            LogAutoUpdateDisabled();
            progress.Report(100);
            return;
        }

        var status = await _updateChecker.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!status.UpdateAvailable)
        {
            progress.Report(100);
            return;
        }

        LogInstalling(status.Channel.ToString(), status.AvailableVersion ?? "unknown");
        await _updateChecker.InstallAsync(status.Channel, cancellationToken).ConfigureAwait(false);
        progress.Report(80);

        if (config.AutoRestartAfterUpdate)
        {
            await WaitForNoActivePlaybackAsync(cancellationToken).ConfigureAwait(false);
            LogRestarting();
            _systemManager.Restart();
        }

        progress.Report(100);
    }

    private async Task WaitForNoActivePlaybackAsync(CancellationToken cancellationToken)
    {
        while (_sessions.Sessions.Any(s => s.NowPlayingItem != null) && !cancellationToken.IsCancellationRequested)
        {
            LogWaitingForPlayback();
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 24, Level = LogLevel.Debug, Message = "Automatic updates are disabled; skipping")]
    private partial void LogAutoUpdateDisabled();

    [LoggerMessage(EventId = 25, Level = LogLevel.Information, Message = "Automatically installing {Channel} update: version {Version}")]
    private partial void LogInstalling(string channel, string version);

    [LoggerMessage(EventId = 26, Level = LogLevel.Information, Message = "Waiting for active playback to end before restarting")]
    private partial void LogWaitingForPlayback();

    [LoggerMessage(EventId = 27, Level = LogLevel.Information, Message = "Restarting Jellyfin to apply the automatically installed update")]
    private partial void LogRestarting();
}
