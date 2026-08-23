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
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="ArrItemMatcher"/>'s provider-ID/path-suffix
/// matching strategy (<c>ARR-INTEGRATION-PROPOSAL.md</c> section 3).
/// </summary>
public class ArrItemMatcherTests
{
    private readonly ArrItemMatcher _matcher = new();

    // --- Movies ---

    [Fact]
    public async Task MatchMovieAsync_MatchesByTmdbId_WhenPresentAndHasFile()
    {
        var movie = new Movie
        {
            Path = "/jellyfin/movies/Sintel (2010)/Sintel.mkv",
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "45745" }
        };

        var client = new Mock<IRadarrClient>();
        client.Setup(c => c.GetAllMoviesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrMovie>
        {
            new() { Id = 1, TmdbId = 45745, HasFile = true, MovieFileId = 99, MovieFile = new RadarrMovieFile { Id = 99, Path = "/radarr/movies/Sintel/Sintel.mkv" } }
        });

        var result = await _matcher.MatchMovieAsync(movie, client.Object, CancellationToken.None);

        Assert.True(result.Matched);
        Assert.Equal("provider_id", result.MatchMethod);
        Assert.Equal(1, result.ArrItemId);
        Assert.Equal(99, result.ArrFileId);
    }

    [Fact]
    public async Task MatchMovieAsync_FallsBackToPathSuffix_WhenNoProviderIdMatch()
    {
        var movie = new Movie
        {
            Path = "/mnt/jellyfin-mount/movies/Dune Part Two (2024)/Dune.Part.Two.2024.mkv",
            ProviderIds = new Dictionary<string, string>()
        };

        var client = new Mock<IRadarrClient>();
        client.Setup(c => c.GetAllMoviesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrMovie>
        {
            new()
            {
                Id = 2,
                TmdbId = 693134,
                HasFile = true,
                MovieFileId = 55,
                // Different mount prefix, same trailing folder/filename -- exactly
                // the real cross-container path-prefix mismatch section 3.2 covers.
                MovieFile = new RadarrMovieFile { Id = 55, Path = "/mnt/pve/cephfs/movies/Dune Part Two (2024)/Dune.Part.Two.2024.mkv" }
            }
        });

        var result = await _matcher.MatchMovieAsync(movie, client.Object, CancellationToken.None);

        Assert.True(result.Matched);
        Assert.Equal("path_suffix", result.MatchMethod);
        Assert.Equal(2, result.ArrItemId);
        Assert.Equal(55, result.ArrFileId);
    }

    [Fact]
    public async Task MatchMovieAsync_ReturnsUnmatched_WhenNeitherProviderIdNorPathMatch()
    {
        var movie = new Movie
        {
            Path = "/movies/Home Videos/Family Vacation 2019.mp4",
            ProviderIds = new Dictionary<string, string>()
        };

        var client = new Mock<IRadarrClient>();
        client.Setup(c => c.GetAllMoviesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrMovie>
        {
            new() { Id = 1, TmdbId = 1, HasFile = true, MovieFileId = 1, MovieFile = new RadarrMovieFile { Id = 1, Path = "/movies/Something Else/file.mkv" } }
        });

        var result = await _matcher.MatchMovieAsync(movie, client.Object, CancellationToken.None);

        Assert.False(result.Matched);
        Assert.Equal("unmatched", result.MatchMethod);
        Assert.Null(result.ArrItemId);
    }

    [Fact]
    public async Task MatchMovieAsync_DoesNotMatch_WhenRadarrTitleHasNoFileYet()
    {
        var movie = new Movie
        {
            Path = "/movies/Something/file.mkv",
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" }
        };

        var client = new Mock<IRadarrClient>();
        client.Setup(c => c.GetAllMoviesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrMovie>
        {
            new() { Id = 1, TmdbId = 1, HasFile = false, MovieFileId = 0, MovieFile = null }
        });

        var result = await _matcher.MatchMovieAsync(movie, client.Object, CancellationToken.None);

        Assert.False(result.Matched);
    }

    // --- Episodes ---

    private static Episode MakeEpisode(int season, int ep) => new() { ParentIndexNumber = season, IndexNumber = ep };

    [Fact]
    public async Task MatchEpisodeAsync_MatchesByTvdbIdAndSeasonEpisodeNumber()
    {
        var episode = MakeEpisode(6, 1);
        var series = new Series { ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "71663" } };

        var client = new Mock<ISonarrClient>();
        client.Setup(c => c.GetAllSeriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrSeries>
        {
            new() { Id = 134, TvdbId = 71663 }
        });
        client.Setup(c => c.GetEpisodesAsync(134, It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrEpisode>
        {
            new() { Id = 7958, SeriesId = 134, SeasonNumber = 6, EpisodeNumber = 1, HasFile = true, EpisodeFileId = 7958 }
        });

        var result = await _matcher.MatchEpisodeAsync(episode, series, client.Object, CancellationToken.None);

        Assert.True(result.Matched);
        Assert.Equal("provider_id", result.MatchMethod);
        Assert.Equal(134, result.ArrItemId);
        Assert.Equal(7958, result.ArrFileId);
        Assert.Equal(7958, result.ArrEpisodeId);
    }

    [Fact]
    public async Task MatchEpisodeAsync_ReturnsUnmatched_WhenSeriesIsNull()
    {
        var episode = MakeEpisode(1, 1);
        var client = new Mock<ISonarrClient>();
        client.Setup(c => c.GetAllSeriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrSeries>());

        var result = await _matcher.MatchEpisodeAsync(episode, series: null, client.Object, CancellationToken.None);

        Assert.False(result.Matched);
    }

    [Fact]
    public async Task MatchEpisodeAsync_ReturnsUnmatched_WhenNoSeriesHasMatchingTvdbId()
    {
        var episode = MakeEpisode(1, 1);
        var series = new Series { ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "999999" } };

        var client = new Mock<ISonarrClient>();
        client.Setup(c => c.GetAllSeriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrSeries>
        {
            new() { Id = 1, TvdbId = 1 }
        });

        var result = await _matcher.MatchEpisodeAsync(episode, series, client.Object, CancellationToken.None);

        Assert.False(result.Matched);
    }

    [Fact]
    public async Task MatchEpisodeAsync_ReturnsUnmatched_WhenSeasonEpisodeNumberDoesNotMatchAnyEpisode()
    {
        var episode = MakeEpisode(99, 99);
        var series = new Series { ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "71663" } };

        var client = new Mock<ISonarrClient>();
        client.Setup(c => c.GetAllSeriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrSeries>
        {
            new() { Id = 134, TvdbId = 71663 }
        });
        client.Setup(c => c.GetEpisodesAsync(134, It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrEpisode>
        {
            new() { Id = 1, SeriesId = 134, SeasonNumber = 1, EpisodeNumber = 1, HasFile = true, EpisodeFileId = 1 }
        });

        var result = await _matcher.MatchEpisodeAsync(episode, series, client.Object, CancellationToken.None);

        Assert.False(result.Matched);
    }

    [Fact]
    public async Task MatchEpisodeAsync_ReturnsUnmatched_WhenMatchedEpisodeHasNoFile()
    {
        var episode = MakeEpisode(1, 1);
        var series = new Series { ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "71663" } };

        var client = new Mock<ISonarrClient>();
        client.Setup(c => c.GetAllSeriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrSeries>
        {
            new() { Id = 134, TvdbId = 71663 }
        });
        client.Setup(c => c.GetEpisodesAsync(134, It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrEpisode>
        {
            new() { Id = 1, SeriesId = 134, SeasonNumber = 1, EpisodeNumber = 1, HasFile = false, EpisodeFileId = 0 }
        });

        var result = await _matcher.MatchEpisodeAsync(episode, series, client.Object, CancellationToken.None);

        Assert.False(result.Matched);
    }
}
