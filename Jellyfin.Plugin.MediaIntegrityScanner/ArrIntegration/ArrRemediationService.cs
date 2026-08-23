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
    private readonly IDatabaseManager _db;
    private readonly ILibraryManager _library;
    private readonly ILogger<ArrRemediationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrRemediationService"/> class.
    /// </summary>
    /// <param name="clientFactory">Builds Radarr/Sonarr clients for a configured server.</param>
    /// <param name="matcher">Matches a Jellyfin item to its Radarr/Sonarr counterpart.</param>
    /// <param name="db">Database manager, for persisting the remediation outcome.</param>
    /// <param name="library">Library manager, for resolving an episode's parent series.</param>
    /// <param name="logger">Logger instance.</param>
    public ArrRemediationService(
        IArrClientFactory clientFactory,
        IArrItemMatcher matcher,
        IDatabaseManager db,
        ILibraryManager library,
        ILogger<ArrRemediationService> logger)
    {
        _clientFactory = clientFactory;
        _matcher = matcher;
        _db = db;
        _library = library;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ArrRemediationRecord> RemediateAsync(BaseItem item, long? scanRecordId, CancellationToken cancellationToken)
    {
        var requestedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var config = Plugin.Instance?.Configuration;

        return item switch
        {
            Movie movie => await RemediateMovieAsync(movie, scanRecordId, requestedAt, config, cancellationToken).ConfigureAwait(false),
            Episode episode => await RemediateEpisodeAsync(episode, scanRecordId, requestedAt, config, cancellationToken).ConfigureAwait(false),
            _ => await RecordAsync(item, scanRecordId, "none", null, "unmatched", "unmatched", "skipped", null, null, requestedAt, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<ArrRemediationRecord> RemediateMovieAsync(Movie movie, long? scanRecordId, string requestedAt, PluginConfiguration? config, CancellationToken cancellationToken)
    {
        var serverConfig = config?.RadarrServers?.FirstOrDefault();
        if (serverConfig is null)
        {
            LogNoServerConfigured("radarr", movie.Id);
            return await RecordAsync(movie, scanRecordId, "radarr", null, "unmatched", "unmatched", "skipped", null, null, requestedAt, cancellationToken).ConfigureAwait(false);
        }

        var client = _clientFactory.CreateRadarrClient(serverConfig);
        var match = await _matcher.MatchMovieAsync(movie, client, cancellationToken).ConfigureAwait(false);

        if (!match.Matched || match.ArrItemId is not int movieId || match.ArrFileId is not int movieFileId)
        {
            LogUnmatched("radarr", movie.Id);
            return await RecordAsync(movie, scanRecordId, "radarr", serverConfig.Name, match.MatchMethod, "unmatched", "skipped", match.ArrItemId, match.ArrFileId, requestedAt, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Step 0: pre-flight availability check (section 4).
            var candidates = await client.SearchReleasesAsync(movieId, cancellationToken).ConfigureAwait(false);
            if (candidates.All(c => c.Rejected))
            {
                LogNoReplacementAvailable("radarr", movie.Id);
                return await RecordAsync(movie, scanRecordId, "radarr", serverConfig.Name, match.MatchMethod, "no_replacement_available", "skipped", movieId, movieFileId, requestedAt, cancellationToken).ConfigureAwait(false);
            }

            // Step 1: delete.
            await client.DeleteMovieFileAsync(movieFileId, cancellationToken).ConfigureAwait(false);

            // Steps 2/3: blocklist the recent grab if one exists, otherwise a plain search.
            var actionTaken = await BlocklistOrSearchAsync(
                () => client.GetHistoryForMovieAsync(movieId, cancellationToken),
                h => h.EventType,
                h => h.Date,
                h => h.Id,
                client.MarkHistoryAsFailedAsync,
                () => client.TriggerMovieSearchAsync(movieId, cancellationToken),
                config?.HistoryLookbackDays ?? 30,
                cancellationToken).ConfigureAwait(false);

            LogRemediated("radarr", movie.Id, actionTaken);
            return await RecordAsync(movie, scanRecordId, "radarr", serverConfig.Name, match.MatchMethod, actionTaken, "success", movieId, movieFileId, requestedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (ArrClientException ex)
        {
            LogRemediationFailed(ex, "radarr", movie.Id);
            return await RecordAsync(movie, scanRecordId, "radarr", serverConfig.Name, match.MatchMethod, null, "failed", movieId, movieFileId, requestedAt, cancellationToken, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task<ArrRemediationRecord> RemediateEpisodeAsync(Episode episode, long? scanRecordId, string requestedAt, PluginConfiguration? config, CancellationToken cancellationToken)
    {
        var serverConfig = config?.SonarrServers?.FirstOrDefault();
        if (serverConfig is null)
        {
            LogNoServerConfigured("sonarr", episode.Id);
            return await RecordAsync(episode, scanRecordId, "sonarr", null, "unmatched", "unmatched", "skipped", null, null, requestedAt, cancellationToken).ConfigureAwait(false);
        }

        var client = _clientFactory.CreateSonarrClient(serverConfig);
        var series = _library.GetItemById(episode.SeriesId) as Series;
        var match = await _matcher.MatchEpisodeAsync(episode, series, client, cancellationToken).ConfigureAwait(false);

        if (!match.Matched || match.ArrItemId is not int seriesId || match.ArrFileId is not int episodeFileId || match.ArrEpisodeId is not int episodeId)
        {
            LogUnmatched("sonarr", episode.Id);
            return await RecordAsync(episode, scanRecordId, "sonarr", serverConfig.Name, match.MatchMethod, "unmatched", "skipped", match.ArrItemId, match.ArrFileId, requestedAt, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Step 0: pre-flight availability check (section 4).
            var candidates = await client.SearchReleasesAsync(episodeId, cancellationToken).ConfigureAwait(false);
            if (candidates.All(c => c.Rejected))
            {
                LogNoReplacementAvailable("sonarr", episode.Id);
                return await RecordAsync(episode, scanRecordId, "sonarr", serverConfig.Name, match.MatchMethod, "no_replacement_available", "skipped", seriesId, episodeFileId, requestedAt, cancellationToken).ConfigureAwait(false);
            }

            // Step 1: delete.
            await client.DeleteEpisodeFileAsync(episodeFileId, cancellationToken).ConfigureAwait(false);

            // Steps 2/3: blocklist the recent grab if one exists, otherwise a plain search.
            var actionTaken = await BlocklistOrSearchAsync(
                () => client.GetHistoryForEpisodeAsync(seriesId, episodeId, cancellationToken),
                h => h.EventType,
                h => h.Date,
                h => h.Id,
                client.MarkHistoryAsFailedAsync,
                () => client.TriggerEpisodeSearchAsync(episodeId, cancellationToken),
                config?.HistoryLookbackDays ?? 30,
                cancellationToken).ConfigureAwait(false);

            LogRemediated("sonarr", episode.Id, actionTaken);
            return await RecordAsync(episode, scanRecordId, "sonarr", serverConfig.Name, match.MatchMethod, actionTaken, "success", seriesId, episodeFileId, requestedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (ArrClientException ex)
        {
            LogRemediationFailed(ex, "sonarr", episode.Id);
            return await RecordAsync(episode, scanRecordId, "sonarr", serverConfig.Name, match.MatchMethod, null, "failed", seriesId, episodeFileId, requestedAt, cancellationToken, ex.Message).ConfigureAwait(false);
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
            await markAsFailed(getId(recentGrab), cancellationToken).ConfigureAwait(false);
            return "deleted_and_blocklisted";
        }

        await triggerSearch().ConfigureAwait(false);
        return "deleted_and_searched";
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
        string? errorMessage = null)
    {
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
}
