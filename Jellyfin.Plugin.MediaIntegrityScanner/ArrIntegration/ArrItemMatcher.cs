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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <inheritdoc cref="IArrItemMatcher" />
public class ArrItemMatcher : IArrItemMatcher
{
    /// <inheritdoc />
    public async Task<ArrMatchResult> MatchMovieAsync(BaseItem movie, IRadarrClient client, CancellationToken cancellationToken)
    {
        var allMovies = await client.GetAllMoviesAsync(cancellationToken).ConfigureAwait(false);

        // Primary: TMDb ID (section 3.1) -- robust to mount-point differences,
        // since it doesn't depend on file paths matching at all.
        if (movie.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbIdString)
            && int.TryParse(tmdbIdString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            var providerMatch = allMovies.FirstOrDefault(m => m.TmdbId == tmdbId);
            if (providerMatch is { HasFile: true })
            {
                return new ArrMatchResult(true, "provider_id", providerMatch.Id, providerMatch.MovieFileId);
            }
        }

        // Fallback: path suffix (section 3.2) -- for unidentified content with no provider ID.
        var pathMatch = allMovies.FirstOrDefault(m =>
            m is { HasFile: true, MovieFile: not null } && PathSuffixMatches(movie.Path, m.MovieFile.Path));
        if (pathMatch is not null)
        {
            return new ArrMatchResult(true, "path_suffix", pathMatch.Id, pathMatch.MovieFileId);
        }

        return ArrMatchResult.Unmatched;
    }

    /// <inheritdoc />
    public async Task<ArrMatchResult> MatchEpisodeAsync(Episode episode, Series? series, ISonarrClient client, CancellationToken cancellationToken)
    {
        var allSeries = await client.GetAllSeriesAsync(cancellationToken).ConfigureAwait(false);

        SonarrSeries? matchedSeries = null;
        string matchMethod = "unmatched";

        if (series is not null
            && series.ProviderIds.TryGetValue(MetadataProvider.Tvdb.ToString(), out var tvdbIdString)
            && int.TryParse(tvdbIdString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvdbId))
        {
            matchedSeries = allSeries.FirstOrDefault(s => s.TvdbId == tvdbId);
            if (matchedSeries is not null)
            {
                matchMethod = "provider_id";
            }
        }

        // Fallback: path suffix on the series' folder isn't reliable enough on its
        // own (many shows share generic folder-naming patterns) -- for an
        // unidentified series, this plugin reports Unmatched rather than guessing
        // which series an orphaned episode file might belong to.
        if (matchedSeries is null)
        {
            return ArrMatchResult.Unmatched;
        }

        var episodes = await client.GetEpisodesAsync(matchedSeries.Id, cancellationToken).ConfigureAwait(false);
        var matchedEpisode = episodes.FirstOrDefault(e =>
            e.SeasonNumber == episode.ParentIndexNumber && e.EpisodeNumber == episode.IndexNumber);

        if (matchedEpisode is not { HasFile: true })
        {
            return ArrMatchResult.Unmatched;
        }

        return new ArrMatchResult(true, matchMethod, matchedSeries.Id, matchedEpisode.EpisodeFileId, matchedEpisode.Id);
    }

    /// <summary>
    /// Compares two file paths by their trailing segments (parent folder +
    /// filename) rather than the full absolute path -- robust to the two
    /// systems mounting the same underlying storage at different prefixes
    /// (see section 3.2's real example from this cluster's own deployment).
    /// </summary>
    private static bool PathSuffixMatches(string jellyfinPath, string arrPath)
    {
        return string.Equals(GetLastTwoSegments(jellyfinPath), GetLastTwoSegments(arrPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetLastTwoSegments(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Fixed '/' separator regardless of OS -- both sides of the comparison
        // always go through this same function, so what matters is that the
        // join is deterministic, not that it matches the host OS's own
        // directory separator (Path.Combine would vary that unnecessarily).
        return segments.Length >= 2 ? $"{segments[^2]}/{segments[^1]}" : normalized;
    }
}
