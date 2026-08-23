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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// Drains the <c>pending</c> Radarr/Sonarr remediation queue on a timer.
/// This is the Phase 2 piece that makes forwarding automatic --
/// <see cref="Scanner.ScanEngine"/> never calls Radarr/Sonarr directly; it
/// only enqueues a cheap, local <c>pending</c> row via
/// <see cref="IArrRemediationService.EnqueueIfEligibleAsync"/>. If
/// Radarr/Sonarr is unreachable, a row just stays <c>pending</c> and gets
/// retried on the next poll -- durable against an outage rather than losing
/// the remediation entirely, addressing the exact "no resync on recovery"
/// gap this project's own prior-art review (<c>ARR-INTEGRATION-PROPOSAL.md</c>
/// section 2.2) found in Seerr.
/// </summary>
public partial class ArrRemediationWorker : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IArrRemediationService _remediation;
    private readonly IDatabaseManager _db;
    private readonly ILogger<ArrRemediationWorker> _logger;
    private Timer? _timer;
    private int _isProcessing;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrRemediationWorker"/> class.
    /// </summary>
    /// <param name="remediation">Processes each pending row's actual matching/delete/blocklist flow.</param>
    /// <param name="db">Database manager, for reading the pending queue and the daily-cap count.</param>
    /// <param name="logger">Logger instance.</param>
    public ArrRemediationWorker(IArrRemediationService remediation, IDatabaseManager db, ILogger<ArrRemediationWorker> logger)
    {
        _remediation = remediation;
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(OnTick, null, PollInterval, PollInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void OnTick(object? state) => _ = ProcessQueueAsync();

    /// <summary>
    /// Processes every currently-pending row once. Internal (not private) so
    /// tests can drive a single pass deterministically instead of waiting on
    /// the real one-minute timer.
    /// </summary>
    internal async Task ProcessQueueAsync()
    {
        // A poll tick fires every minute regardless of how long the previous
        // one took; skip rather than overlap if the last pass (e.g. a slow
        // or unreachable Radarr/Sonarr) is still running.
        if (Interlocked.Exchange(ref _isProcessing, 1) == 1)
        {
            return;
        }

        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.EnableArrForwarding != true)
            {
                return;
            }

            var pending = await _db.GetPendingRemediationsAsync().ConfigureAwait(false);
            if (pending.Count == 0)
            {
                return;
            }

            var todayCount = await _db.CountAutoRemediationsSinceAsync(DateTime.UtcNow.Date).ConfigureAwait(false);
            var cap = config.MaxAutoRemediationsPerDay;

            foreach (var record in pending)
            {
                if (todayCount >= cap)
                {
                    record.Status = "skipped";
                    record.ActionTaken = "skipped_daily_cap";
                    record.CompletedAt = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    await _db.UpdateRemediationAsync(record).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var processed = await _remediation.ProcessPendingAsync(record, CancellationToken.None).ConfigureAwait(false);
                    if (processed.Status is "success" or "failed")
                    {
                        todayCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogProcessingFailed(ex, record.Id);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _isProcessing, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _timer?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to process pending Arr remediation {RecordId}")]
    private partial void LogProcessingFailed(Exception ex, long recordId);
}
