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
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ScheduledTasks;

/// <summary>
/// Scheduled task for Phase 1 header/metadata scanning of all media files.
/// </summary>
public partial class HeaderScanTask : IScheduledTask
{
    private readonly IScanEngine _scanner;
    private readonly ILogger<HeaderScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeaderScanTask"/> class.
    /// </summary>
    /// <param name="scanner">Scan engine.</param>
    /// <param name="logger">Logger instance.</param>
    public HeaderScanTask(IScanEngine scanner, ILogger<HeaderScanTask> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Media Integrity - Header Scan";

    /// <inheritdoc />
    public string Key => "MediaIntegrityHeaderScan";

    /// <inheritdoc />
    public string Description =>
        "Quick validation of media file headers and metadata using ffprobe.";

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
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        LogHeaderScanStarting();
        return _scanner.ScanLibraryAsync(null, ScanPhase.Header, cancellationToken, progress);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting scheduled header scan")]
    private partial void LogHeaderScanStarting();
}
