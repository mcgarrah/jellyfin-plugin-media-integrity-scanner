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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <inheritdoc cref="IArrRemediationService" />
public partial class ArrRemediationService : IArrRemediationService
{
    private readonly IArrClientFactory _clientFactory;
    private readonly IArrItemMatcher _matcher;
    private readonly IArrServerSelector _serverSelector;
    private readonly IDatabaseManager _db;
    private readonly ILibraryManager _library;
    private readonly ILogger<ArrRemediationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrRemediationService"/> class.
    /// </summary>
    /// <param name="clientFactory">Builds Radarr/Sonarr clients for a configured server.</param>
    /// <param name="matcher">Matches a Jellyfin item to its Radarr/Sonarr counterpart.</param>
    /// <param name="serverSelector">Picks which configured server (Phase 3 multi-server support) an item routes to.</param>
    /// <param name="db">Database manager, for persisting the remediation outcome.</param>
    /// <param name="library">Library manager, for resolving an episode's parent series.</param>
    /// <param name="logger">Logger instance.</param>
    public ArrRemediationService(
        IArrClientFactory clientFactory,
        IArrItemMatcher matcher,
        IArrServerSelector serverSelector,
        IDatabaseManager db,
        ILibraryManager library,
        ILogger<ArrRemediationService> logger)
    {
        _clientFactory = clientFactory;
        _matcher = matcher;
        _serverSelector = serverSelector;
        _db = db;
        _library = library;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ArrRemediationRecord> RemediateAsync(BaseItem item, long? scanRecordId, CancellationToken cancellationToken)
    {
        var requestedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var config = Plugin.Instance?.Configuration;

        return await RemediateCoreAsync(item, scanRecordId, requestedAt, config, dryRun: false, existing: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ArrRemediationRecord?> EnqueueIfEligibleAsync(BaseItem item, int scanStatus, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableArrForwarding != true)
        {
            return null;
        }

        if (item is not Movie && item is not Episode)
        {
            return null;
        }

        // scan_status: 2 = Fail, 3 = Error (see Scanner.ScanStatus). Error is
        // a weaker signal (the scan itself broke) than Fail (the scan
        // completed and affirmatively found corruption), so it's opt-in via
        // ArrForwardOnStatus rather than always eligible.
        if (scanStatus == 3 && config.ArrForwardOnStatus != ArrForwardTrigger.FailAndError)
        {
            return null;
        }

        var itemId = item.Id.ToString();

        if (await _db.HasPendingRemediationAsync(itemId).ConfigureAwait(false))
        {
            return null;
        }

        var lastCompleted = await _db.GetLastCompletedRemediationForItemAsync(itemId).ConfigureAwait(false);
        if (lastCompleted is not null
            && DateTime.TryParse(lastCompleted.CompletedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var completedAt)
            && DateTime.UtcNow - completedAt < TimeSpan.FromHours(config.RemediationCooldownHours))
        {
            return null;
        }

        var cycleNumber = 1 + await _db.CountSuccessfulRemediationsForItemAsync(itemId).ConfigureAwait(false);
        var requestedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var record = new ArrRemediationRecord
        {
            ItemId = itemId,
            FilePath = item.Path,
            ArrApp = item is Movie ? "radarr" : "sonarr",
            MatchMethod = "pending",
            Status = "pending",
            RequestedAt = requestedAt,
            CycleNumber = cycleNumber
        };

        record.Id = await _db.RecordRemediationAsync(record).ConfigureAwait(false);
        LogEnqueued(record.ArrApp, item.Id, cycleNumber);
        return record;
    }

    /// <inheritdoc />
    public async Task<ArrRemediationRecord> ProcessPendingAsync(ArrRemediationRecord pending, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var maxCycles = config?.MaxRemediationCycles ?? 3;

        if (pending.CycleNumber > maxCycles)
        {
            LogCycleLimitReached(pending.ItemId, pending.CycleNumber, maxCycles);
            return await MarkAsync(pending, "blocked", "skipped_cycle_limit", null, cancellationToken).ConfigureAwait(false);
        }

        if (!Guid.TryParse(pending.ItemId, out var guid) || _library.GetItemById(guid) is not BaseItem item)
        {
            return await MarkAsync(pending, "skipped", "item_removed", null, cancellationToken).ConfigureAwait(false);
        }

        var dryRun = config?.ArrForwardingDryRun ?? true;
        return await RemediateCoreAsync(item, pending.ScanRecordId, pending.RequestedAt, config, dryRun, pending, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ArrRemediationRecord?> ResetCycleAsync(string itemId, CancellationToken cancellationToken)
    {
        var latest = await _db.GetLatestRemediationForItemAsync(itemId).ConfigureAwait(false);
        if (latest is null || latest.Status != "blocked")
        {
            return null;
        }

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var record = new ArrRemediationRecord
        {
            ItemId = itemId,
            FilePath = latest.FilePath,
            ArrApp = latest.ArrApp,
            ArrServerName = latest.ArrServerName,
            MatchMethod = "cycle_reset",
            ActionTaken = "cycle_reset",
            Status = "skipped",
            RequestedAt = now,
            CompletedAt = now,
            CycleNumber = 1
        };

        record.Id = await _db.RecordRemediationAsync(record).ConfigureAwait(false);
        LogCycleReset(itemId);
        return record;
    }

    private async Task<ArrRemediationRecord> RemediateCoreAsync(BaseItem item, long? scanRecordId, string requestedAt, PluginConfiguration? config, bool dryRun, ArrRemediationRecord? existing, CancellationToken cancellationToken)
    {
        return item switch
        {
            Movie movie => await RemediateMovieAsync(movie, scanRecordId, requestedAt, config, dryRun, existing, cancellationToken).ConfigureAwait(false),
            Episode episode => await RemediateEpisodeAsync(episode, scanRecordId, requestedAt, config, dryRun, existing, cancellationToken).ConfigureAwait(false),
            _ => await RecordAsync(item, scanRecordId, "none", null, "unmatched", "unmatched", "skipped", null, null, requestedAt, cancellationToken, existing: existing).ConfigureAwait(false)
        };
    }

    private async Task<ArrRemediationRecord> RemediateMovieAsync(Movie movie, long? scanRecordId, string requestedAt, PluginConfiguration? config, bool dryRun, ArrRemediationRecord? existing, CancellationToken cancellationToken)
    {
        var serverConfig = _serverSelector.SelectForPath(config?.RadarrServers ?? new List<ArrServerConfig>(), movie.Path);
        if (serverConfig is null)
        {
            LogNoServerConfigured("radarr", movie.Id);
            return await RecordAsync(movie, scanRecordId, "radarr", null, "unmatched", "unmatched", "skipped", null, null, requestedAt, cancellationToken, existing: existing).ConfigureAwait(false);
        }

        var client = _clientFactory.CreateRadarrClient(serverConfig);
        var match = await _matcher.MatchMovieAsync(movie, client, cancellationToken).ConfigureAwait(false);

        if (!match.Matched || match.ArrItemId is not int movieId || match.ArrFileId is not int movieFileId)
        {
            LogUnmatched("radarr", movie.Id);
            return await RecordAsync(movie, scanRecordId, "radarr", serverConfig.Name, match.MatchMethod, "unmatched", "skipped", match.ArrItemId, match.ArrFileId, requestedAt, cancellationToken, existing: existing).ConfigureAwait(false);
        }

        try
        {
            // Step 0: pre-flight availability check (section 4).
            var candidates = await client.SearchReleasesAsync(movieId, cancellationToken).ConfigureAwait(false);
            if (candidates.All(c => c.Rejected))
            {
                LogNoReplacementAvailable("radarr", movie.Id);
                return await RecordAsync(movie, scanRecordId, "radarr", serverConfig.Name, match.MatchMethod, "no_replacement_available", "skipped", movieId, movieFileId, requestedAt, cancellationToken, existing: existing).ConfigureAwait(false);
            }

            // Step 1: delete (skipped in dry-run -- see BlocklistOrSearchAsync for the matching skip on steps 2/3).
            if (!dryRun)
            {
                await client.DeleteMovieFileAsync(movieFileId, cancellationToken).ConfigureAwait(false);
            }

            // Steps 2/3: blocklist the recent grab if one exists, otherwise a plain search.
            var actionTaken = await BlocklistOrSearchAsync(
                () => client.GetHistoryForMovieAsync(movieId, cancellationToken),
                h => h.EventType,
                h => h.Date,
                h => h.Id,
                client.MarkHistoryAsFailedAsync,
                () => client.TriggerMovieSearchAsync(movieId, cancellationToken),
                config?.HistoryLookbackDays ?? 30,
                dryRun,
                cancellationToken).ConfigureAwait(false);

            var status = dryRun ? "skipped" : "success";
            LogRemediated("radarr", movie.Id, actionTaken);
            return await RecordAsync(movie, scanRecordId, "radarr", serverConfig.Name, match.MatchMethod, actionTaken, status, movieId, movieFileId, requestedAt, cancellationToken, existing: existing).ConfigureAwait(false);
        }
        catch (ArrClientException ex)
        {
            LogRemediationFailed(ex, "radarr", movie.Id);
            return await RecordAsync(movie, scanRecordId, "radarr", serverConfig.Name, match.MatchMethod, null, "failed", movieId, movieFileId, requestedAt, cancellationToken, ex.Message, existing).ConfigureAwait(false);
        }
    }

    private async Task<ArrRemediationRecord> RemediateEpisodeAsync(Episode episode, long? scanRecordId, string requestedAt, PluginConfiguration? config, bool dryRun, ArrRemediationRecord? existing, CancellationToken cancellationToken)
    {
        var serverConfig = _serverSelector.SelectForPath(config?.SonarrServers ?? new List<ArrServerConfig>(), episode.Path);
        if (serverConfig is null)
        {
            LogNoServerConfigured("sonarr", episode.Id);
            return await RecordAsync(episode, scanRecordId, "sonarr", null, "unmatched", "unmatched", "skipped", null, null, requestedAt, cancellationToken, existing: existing).ConfigureAwait(false);
        }

        var client = _clientFactory.CreateSonarrClient(serverConfig);
        var series = _library.GetItemById(episode.SeriesId) as Series;
        var match = await _matcher.MatchEpisodeAsync(episode, series, client, cancellationToken).ConfigureAwait(false);

        if (!match.Matched || match.ArrItemId is not int seriesId || match.ArrFileId is not int episodeFileId || match.ArrEpisodeId is not int episodeId)
        {
            LogUnmatched("sonarr", episode.Id);
            return await RecordAsync(episode, scanRecordId, "sonarr", serverConfig.Name, match.MatchMethod, "unmatched", "skipped", match.ArrItemId, match.ArrFileId, requestedAt, cancellationToken, existing: existing).ConfigureAwait(false);
        }

        try
        {
            // Step 0: pre-flight availability check (section 4).
            var candidates = await client.SearchReleasesAsync(episodeId, cancellationToken).ConfigureAwait(false);
            if (candidates.All(c => c.Rejected))
            {
                LogNoReplacementAvailable("sonarr", episode.Id);
                return await RecordAsync(episode, scanRecordId, "sonarr", serverConfig.Name, match.MatchMethod, "no_replacement_available", "skipped", seriesId, episodeFileId, requestedAt, cancellationToken, existing: existing).ConfigureAwait(false);
            }

            // Step 1: delete (skipped in dry-run).
            if (!dryRun)
            {
                await client.DeleteEpisodeFileAsync(episodeFileId, cancellationToken).ConfigureAwait(false);
            }

            // Steps 2/3: blocklist the recent grab if one exists, otherwise a plain search.
            var actionTaken = await BlocklistOrSearchAsync(
                () => client.GetHistoryForEpisodeAsync(seriesId, episodeId, cancellationToken),
                h => h.EventType,
                h => h.Date,
                h => h.Id,
                client.MarkHistoryAsFailedAsync,
                () => client.TriggerEpisodeSearchAsync(episodeId, cancellationToken),
                config?.HistoryLookbackDays ?? 30,
                dryRun,
                cancellationToken).ConfigureAwait(false);

            var status = dryRun ? "skipped" : "success";
            LogRemediated("sonarr", episode.Id, actionTaken);
            return await RecordAsync(episode, scanRecordId, "sonarr", serverConfig.Name, match.MatchMethod, actionTaken, status, seriesId, episodeFileId, requestedAt, cancellationToken, existing: existing).ConfigureAwait(false);
        }
        catch (ArrClientException ex)
        {
            LogRemediationFailed(ex, "sonarr", episode.Id);
            return await RecordAsync(episode, scanRecordId, "sonarr", serverConfig.Name, match.MatchMethod, null, "failed", seriesId, episodeFileId, requestedAt, cancellationToken, ex.Message, existing).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Shared logic for section 4 steps 2/3, generic over Radarr's and
    /// Sonarr's otherwise-identical history-record shape: find the most
    /// recent "grabbed" event within <paramref name="lookbackDays"/> and
    /// blocklist it via <paramref name="markAsFailed"/>
    /// ("deleted_and_blocklisted"), or fall back to a plain search via
    /// <paramref name="triggerSearch"/> if no recent grab exists
    /// ("deleted_and_searched").
    /// </summary>
    private static async Task<string> BlocklistOrSearchAsync<THistory>(
        Func<Task<System.Collections.Generic.IReadOnlyList<THistory>>> getHistory,
        Func<THistory, string> getEventType,
        Func<THistory, string> getDate,
        Func<THistory, int> getId,
        Func<int, CancellationToken, Task> markAsFailed,
        Func<Task> triggerSearch,
        int lookbackDays,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var history = await getHistory().ConfigureAwait(false);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-lookbackDays);

        var recentGrab = history
            .Where(h => string.Equals(getEventType(h), "grabbed", StringComparison.OrdinalIgnoreCase))
            .Where(h => DateTimeOffset.TryParse(getDate(h), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && date >= cutoff)
            .OrderByDescending(h => getDate(h), StringComparer.Ordinal)
            .FirstOrDefault();

        if (recentGrab is not null)
        {
            if (!dryRun)
            {
                await markAsFailed(getId(recentGrab), cancellationToken).ConfigureAwait(false);
            }

            return dryRun ? "would_delete_and_blocklist" : "deleted_and_blocklisted";
        }

        if (!dryRun)
        {
            await triggerSearch().ConfigureAwait(false);
        }

        return dryRun ? "would_delete_and_search" : "deleted_and_searched";
    }

    private async Task<ArrRemediationRecord> RecordAsync(
        BaseItem item,
        long? scanRecordId,
        string arrApp,
        string? arrServerName,
        string matchMethod,
        string? actionTaken,
        string status,
        int? arrItemId,
        int? arrFileId,
        string requestedAt,
        CancellationToken cancellationToken,
        string? errorMessage = null,
        ArrRemediationRecord? existing = null)
    {
        if (existing is not null)
        {
            existing.ArrServerName = arrServerName;
            existing.MatchMethod = matchMethod;
            existing.ArrItemId = arrItemId;
            existing.ArrFileId = arrFileId;
            return await MarkAsync(existing, status, actionTaken, errorMessage, cancellationToken).ConfigureAwait(false);
        }

        var itemId = item.Id.ToString();
        var cycleNumber = 1 + await _db.CountSuccessfulRemediationsForItemAsync(itemId).ConfigureAwait(false);

        var record = new ArrRemediationRecord
        {
            ItemId = itemId,
            ScanRecordId = scanRecordId,
            FilePath = item.Path,
            ArrApp = arrApp,
            ArrServerName = arrServerName,
            MatchMethod = matchMethod,
            ArrItemId = arrItemId,
            ArrFileId = arrFileId,
            ActionTaken = actionTaken,
            Status = status,
            ErrorMessage = errorMessage,
            RequestedAt = requestedAt,
            CompletedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            CycleNumber = cycleNumber
        };

        record.Id = await _db.RecordRemediationAsync(record).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Transitions an existing (typically <c>pending</c>) remediation row to
    /// a terminal state in place, via <see cref="IDatabaseManager.UpdateRemediationAsync"/>
    /// -- the Phase 2 counterpart to <see cref="RecordAsync"/>'s insert path.
    /// </summary>
    private async Task<ArrRemediationRecord> MarkAsync(ArrRemediationRecord record, string status, string? actionTaken, string? errorMessage, CancellationToken cancellationToken)
    {
        record.Status = status;
        record.ActionTaken = actionTaken;
        record.ErrorMessage = errorMessage;
        record.CompletedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await _db.UpdateRemediationAsync(record).ConfigureAwait(false);
        return record;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "No {ArrApp} server configured -- cannot remediate item {ItemId}")]
    private partial void LogNoServerConfigured(string arrApp, Guid itemId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Could not match item {ItemId} to a {ArrApp} title")]
    private partial void LogUnmatched(string arrApp, Guid itemId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "No viable replacement found for item {ItemId} in {ArrApp} -- not deleting the existing file")]
    private partial void LogNoReplacementAvailable(string arrApp, Guid itemId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Remediated item {ItemId} via {ArrApp}: {ActionTaken}")]
    private partial void LogRemediated(string arrApp, Guid itemId, string actionTaken);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Remediation failed for item {ItemId} via {ArrApp}")]
    private partial void LogRemediationFailed(Exception ex, string arrApp, Guid itemId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Enqueued {ArrApp} remediation for item {ItemId} (cycle {CycleNumber})")]
    private partial void LogEnqueued(string arrApp, Guid itemId, int cycleNumber);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Item {ItemId} blocked: cycle {CycleNumber} exceeds limit {MaxCycles} -- needs manual review")]
    private partial void LogCycleLimitReached(string itemId, int cycleNumber, int maxCycles);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Cycle count reset for item {ItemId}")]
    private partial void LogCycleReset(string itemId);
}
