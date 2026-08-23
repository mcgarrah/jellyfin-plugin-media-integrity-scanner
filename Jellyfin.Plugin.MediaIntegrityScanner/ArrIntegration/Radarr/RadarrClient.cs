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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;

/// <summary>
/// Real HTTP implementation of <see cref="IRadarrClient"/>. See
/// <see cref="ArrClientBase"/> for the shared transport concerns
/// (timeout, camelCase JSON, contextual error wrapping).
/// </summary>
public class RadarrClient : ArrClientBase, IRadarrClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RadarrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the underlying <see cref="HttpClient"/>.</param>
    /// <param name="baseUrl">The configured Radarr server's base URL.</param>
    /// <param name="apiKey">The configured Radarr server's API key.</param>
    /// <param name="logger">Logger instance.</param>
    public RadarrClient(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey, ILogger<RadarrClient> logger)
        : base(httpClientFactory, baseUrl, apiKey, logger)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RadarrMovie>> GetAllMoviesAsync(CancellationToken cancellationToken)
    {
        var movies = await GetAsync<List<RadarrMovie>>("movie", cancellationToken).ConfigureAwait(false);
        return movies ?? new List<RadarrMovie>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RadarrReleaseCandidate>> SearchReleasesAsync(int movieId, CancellationToken cancellationToken)
    {
        var releases = await GetAsync<List<RadarrReleaseCandidate>>($"release?movieId={movieId}", cancellationToken).ConfigureAwait(false);
        return releases ?? new List<RadarrReleaseCandidate>();
    }

    /// <inheritdoc />
    public async Task DeleteMovieFileAsync(int movieFileId, CancellationToken cancellationToken)
    {
        await DeleteAsync($"moviefile/{movieFileId}", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RadarrHistoryRecord>> GetHistoryForMovieAsync(int movieId, CancellationToken cancellationToken)
    {
        var history = await GetAsync<List<RadarrHistoryRecord>>($"history/movie?movieId={movieId}", cancellationToken).ConfigureAwait(false);
        return history ?? new List<RadarrHistoryRecord>();
    }

    /// <inheritdoc />
    public async Task MarkHistoryAsFailedAsync(int historyId, CancellationToken cancellationToken)
    {
        // No request body -- confirmed live this is a bare POST, the historyId is in the path.
        await PostAsync($"history/failed/{historyId}", new { }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TriggerMovieSearchAsync(int movieId, CancellationToken cancellationToken)
    {
        await PostAsync("command", new { name = "MoviesSearch", movieIds = new[] { movieId } }, cancellationToken).ConfigureAwait(false);
    }
}
