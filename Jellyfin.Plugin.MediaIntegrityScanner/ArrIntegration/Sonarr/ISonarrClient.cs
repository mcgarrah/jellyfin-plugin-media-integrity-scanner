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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;

/// <summary>
/// Client for the subset of Sonarr's REST API this plugin needs: matching
/// an episode, checking replacement availability, and running the
/// delete-then-blocklist remediation sequence from
/// <c>ARR-INTEGRATION-PROPOSAL.md</c> section 4. Mirrors
/// <see cref="Radarr.IRadarrClient"/>'s shape, adjusted for
/// series/episode instead of movie.
/// </summary>
public interface ISonarrClient
{
    /// <summary>
    /// Gets every series Sonarr manages. Used for the provider-ID/path
    /// matching in section 3.1.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every configured series.</returns>
    Task<IReadOnlyList<SonarrSeries>> GetAllSeriesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets every episode for a series -- used to find the specific episode
    /// matching a Jellyfin item's season/episode number (section 3.1).
    /// </summary>
    /// <param name="seriesId">Sonarr's internal series ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every episode Sonarr tracks for this series.</returns>
    Task<IReadOnlyList<SonarrEpisode>> GetEpisodesAsync(int seriesId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs Sonarr's real interactive search for an episode and returns
    /// every candidate release, without grabbing anything -- the pre-flight
    /// availability check (section 4 step 0).
    /// </summary>
    /// <param name="episodeId">Sonarr's internal episode ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every candidate release Sonarr's configured indexers returned.</returns>
    Task<IReadOnlyList<SonarrReleaseCandidate>> SearchReleasesAsync(int episodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an episode file from disk and from Sonarr's database (section 4 step 1).
    /// </summary>
    /// <param name="episodeFileId">The episode file's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task DeleteEpisodeFileAsync(int episodeFileId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the history for a specific episode (filtered client-side from
    /// the series-wide history endpoint -- see <see cref="SonarrHistoryRecord"/>),
    /// for finding the most recent "grabbed" event to blocklist (section 4 step 2).
    /// </summary>
    /// <param name="seriesId">Sonarr's internal series ID.</param>
    /// <param name="episodeId">Sonarr's internal episode ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every history event for this specific episode.</returns>
    Task<IReadOnlyList<SonarrHistoryRecord>> GetHistoryForEpisodeAsync(int seriesId, int episodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a history record as failed -- blocklists that release and
    /// triggers a fresh search (section 4 step 2).
    /// </summary>
    /// <param name="historyId">The history record's own ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task MarkHistoryAsFailedAsync(int historyId, CancellationToken cancellationToken);

    /// <summary>
    /// Triggers a plain search for an episode, with nothing blocklisted --
    /// the fallback when no grab history exists (section 4 step 3).
    /// </summary>
    /// <param name="episodeId">Sonarr's internal episode ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task TriggerEpisodeSearchAsync(int episodeId, CancellationToken cancellationToken);
}
