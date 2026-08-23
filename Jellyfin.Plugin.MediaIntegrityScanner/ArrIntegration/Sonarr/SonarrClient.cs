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

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;

/// <summary>
/// Real HTTP implementation of <see cref="ISonarrClient"/>. See
/// <see cref="ArrClientBase"/> for the shared transport concerns
/// (timeout, camelCase JSON, contextual error wrapping).
/// </summary>
public class SonarrClient : ArrClientBase, ISonarrClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SonarrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the underlying <see cref="HttpClient"/>.</param>
    /// <param name="baseUrl">The configured Sonarr server's base URL.</param>
    /// <param name="apiKey">The configured Sonarr server's API key.</param>
    /// <param name="logger">Logger instance.</param>
    public SonarrClient(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey, ILogger<SonarrClient> logger)
        : base(httpClientFactory, baseUrl, apiKey, logger)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SonarrSeries>> GetAllSeriesAsync(CancellationToken cancellationToken)
    {
        var series = await GetAsync<List<SonarrSeries>>("series", cancellationToken).ConfigureAwait(false);
        return series ?? new List<SonarrSeries>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SonarrEpisode>> GetEpisodesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var episodes = await GetAsync<List<SonarrEpisode>>($"episode?seriesId={seriesId}", cancellationToken).ConfigureAwait(false);
        return episodes ?? new List<SonarrEpisode>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SonarrReleaseCandidate>> SearchReleasesAsync(int episodeId, CancellationToken cancellationToken)
    {
        var releases = await GetAsync<List<SonarrReleaseCandidate>>($"release?episodeId={episodeId}", cancellationToken).ConfigureAwait(false);
        return releases ?? new List<SonarrReleaseCandidate>();
    }

    /// <inheritdoc />
    public async Task DeleteEpisodeFileAsync(int episodeFileId, CancellationToken cancellationToken)
    {
        await DeleteAsync($"episodefile/{episodeFileId}", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SonarrHistoryRecord>> GetHistoryForEpisodeAsync(int seriesId, int episodeId, CancellationToken cancellationToken)
    {
        // history/series is series-scoped, not episode-scoped (confirmed live --
        // see SonarrHistoryRecord's doc comment) -- filter down to the one episode.
        var seriesHistory = await GetAsync<List<SonarrHistoryRecord>>($"history/series?seriesId={seriesId}", cancellationToken).ConfigureAwait(false);
        return (seriesHistory ?? new List<SonarrHistoryRecord>())
            .Where(h => h.EpisodeId == episodeId)
            .ToList();
    }

    /// <inheritdoc />
    public async Task MarkHistoryAsFailedAsync(int historyId, CancellationToken cancellationToken)
    {
        await PostAsync($"history/failed/{historyId}", new { }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TriggerEpisodeSearchAsync(int episodeId, CancellationToken cancellationToken)
    {
        await PostAsync("command", new { name = "EpisodeSearch", episodeIds = new[] { episodeId } }, cancellationToken).ConfigureAwait(false);
    }
}
