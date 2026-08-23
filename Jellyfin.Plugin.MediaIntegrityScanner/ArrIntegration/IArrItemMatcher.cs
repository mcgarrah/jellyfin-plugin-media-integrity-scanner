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

using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// Matches a Jellyfin item to its Radarr/Sonarr counterpart, implementing
/// the strategy from <c>ARR-INTEGRATION-PROPOSAL.md</c> section 3: provider
/// IDs (Tmdb/Tvdb) first, a path-suffix fallback second, "unmatched" rather
/// than a guess if neither works.
/// </summary>
public interface IArrItemMatcher
{
    /// <summary>
    /// Matches a Jellyfin movie item to a Radarr movie.
    /// </summary>
    /// <param name="movie">The Jellyfin movie item (its <see cref="BaseItem.Path"/> and <see cref="BaseItem.ProviderIds"/> are used).</param>
    /// <param name="client">The Radarr client to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The match result.</returns>
    Task<ArrMatchResult> MatchMovieAsync(BaseItem movie, IRadarrClient client, CancellationToken cancellationToken);

    /// <summary>
    /// Matches a Jellyfin episode item to a Sonarr episode, via its parent
    /// series' provider IDs plus season/episode number (section 3.1). The
    /// caller resolves <paramref name="series"/> itself (e.g. via
    /// <c>ILibraryManager.GetItemById(episode.SeriesId)</c>) rather than
    /// this method reading <see cref="Episode.Series"/> directly -- that
    /// property calls a static, server-context-only
    /// <c>LibraryManager.GetItemById</c> internally, which makes it unsafe
    /// to exercise from a plain unit test; taking the already-resolved
    /// series as a parameter keeps this method cleanly testable.
    /// </summary>
    /// <param name="episode">The Jellyfin episode item.</param>
    /// <param name="series">The episode's parent series, already resolved by the caller. Null if it couldn't be resolved (treated the same as "no provider ID match").</param>
    /// <param name="client">The Sonarr client to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The match result.</returns>
    Task<ArrMatchResult> MatchEpisodeAsync(Episode episode, Series? series, ISonarrClient client, CancellationToken cancellationToken);
}
