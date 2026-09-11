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
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
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
    private readonly IArrServerSelector _serverSelector;
    private readonly ILogger<ArrRemediationWorker> _logger;
    private Timer? _timer;
    private int _isProcessing;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrRemediationWorker"/> class.
    /// </summary>
    /// <param name="remediation">Processes each pending row's actual matching/delete/blocklist flow.</param>
    /// <param name="db">Database manager, for reading the pending queue and the daily-cap count.</param>
    /// <param name="serverSelector">
    /// Same routing logic <see cref="ArrRemediationService"/> uses internally,
    /// used here only to predict -- cheaply, with no I/O -- which configured
    /// server a pending row would route to, so a server that just failed this
    /// tick can be skipped for the rest of the pass (see the circuit-breaker
    /// comment in <see cref="ProcessQueueAsync"/>).
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public ArrRemediationWorker(
        IArrRemediationService remediation,
        IDatabaseManager db,
        IArrServerSelector serverSelector,
        ILogger<ArrRemediationWorker> logger)
    {
        _remediation = remediation;
        _db = db;
        _serverSelector = serverSelector;
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

            // Per-tick circuit breaker: an exception escaping ProcessPendingAsync
            // (below) means the failure happened at the transport level -- a
            // timed-out or unreachable server, not a normal "unmatched"/"no
            // replacement available" outcome, which ProcessPendingAsync already
            // reports as a plain status instead of throwing. Without this, a
            // single down server with N queued items pays N full HttpClient
            // timeouts (15s each, see ArrClientBase) serially in one tick,
            // starving every other item -- including ones routed to an
            // otherwise-healthy server -- queued after it in the same pass.
            var failedServersThisTick = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                var predictedServer = PredictServerName(record, config);
                if (predictedServer is not null && failedServersThisTick.Contains(predictedServer))
                {
                    LogSkippedKnownBadServer(record.Id, predictedServer);
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
                    if (predictedServer is not null)
                    {
                        failedServersThisTick.Add(predictedServer);
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _isProcessing, 0);
        }
    }

    /// <summary>
    /// Predicts which configured server a pending row would route to, using
    /// the same cheap, no-I/O logic <see cref="ArrRemediationService"/> uses
    /// for real -- <see cref="IArrServerSelector.SelectForPath"/> against the
    /// path this row was queued with. Returns <c>null</c> if no server of the
    /// right type is configured at all (nothing to attribute a failure to).
    /// </summary>
    private string? PredictServerName(ArrRemediationRecord record, PluginConfiguration config)
    {
        var servers = string.Equals(record.ArrApp, "radarr", StringComparison.OrdinalIgnoreCase)
            ? config.RadarrServers
            : config.SonarrServers;
        return _serverSelector.SelectForPath(servers, record.FilePath)?.Name;
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

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Skipping pending remediation {RecordId} -- server \"{ServerName}\" already failed earlier this tick")]
    private partial void LogSkippedKnownBadServer(long recordId, string serverName);
}
