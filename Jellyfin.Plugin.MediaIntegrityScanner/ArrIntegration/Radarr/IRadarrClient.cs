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

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;

/// <summary>
/// Client for the subset of Radarr's REST API this plugin needs: matching a
/// movie, checking replacement availability, and running the
/// delete-then-blocklist remediation sequence from
/// <c>ARR-INTEGRATION-PROPOSAL.md</c> section 4.
/// </summary>
public interface IRadarrClient
{
    /// <summary>
    /// Gets every movie Radarr manages. Used for the provider-ID/path
    /// matching in section 3.1 -- Radarr has no server-side "find by tmdbId"
    /// filter on this endpoint, so matching happens client-side.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every configured movie.</returns>
    Task<IReadOnlyList<RadarrMovie>> GetAllMoviesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs Radarr's real interactive search for a movie and returns every
    /// candidate release, without grabbing anything -- the pre-flight
    /// availability check (section 4 step 0).
    /// </summary>
    /// <param name="movieId">Radarr's internal movie ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every candidate release Radarr's configured indexers returned.</returns>
    Task<IReadOnlyList<RadarrReleaseCandidate>> SearchReleasesAsync(int movieId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a movie file from disk and from Radarr's database (section 4 step 1).
    /// </summary>
    /// <param name="movieFileId">The movie file's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task DeleteMovieFileAsync(int movieFileId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the history for a movie, for finding the most recent "grabbed"
    /// event to blocklist (section 4 step 2).
    /// </summary>
    /// <param name="movieId">Radarr's internal movie ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every history event for this movie.</returns>
    Task<IReadOnlyList<RadarrHistoryRecord>> GetHistoryForMovieAsync(int movieId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a history record as failed -- blocklists that release and
    /// triggers a fresh search (section 4 step 2).
    /// </summary>
    /// <param name="historyId">The history record's own ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task MarkHistoryAsFailedAsync(int historyId, CancellationToken cancellationToken);

    /// <summary>
    /// Triggers a plain search for a movie, with nothing blocklisted --
    /// the fallback when no grab history exists (section 4 step 3).
    /// </summary>
    /// <param name="movieId">Radarr's internal movie ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task TriggerMovieSearchAsync(int movieId, CancellationToken cancellationToken);
}
