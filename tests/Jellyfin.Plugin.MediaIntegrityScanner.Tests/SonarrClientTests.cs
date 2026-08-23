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

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="SonarrClient"/>, mirroring <c>RadarrClientTests</c>
/// plus a dedicated test for the series-to-episode history filtering that
/// has no Radarr equivalent (Radarr's history endpoint is already
/// movie-scoped; Sonarr's is series-scoped, see
/// <see cref="SonarrHistoryRecord"/>'s doc comment).
/// </summary>
public class SonarrClientTests
{
    private static SonarrClient CreateClient(FakeHttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        return new SonarrClient(factory.Object, "http://sonarr.local:8989", "test-api-key", NullLogger<SonarrClient>.Instance);
    }

    [Fact]
    public async Task GetAllSeriesAsync_DeserializesCamelCaseFields()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            "[{\"id\":134,\"title\":\"The Simpsons\",\"tvdbId\":71663,\"imdbId\":\"tt0096697\"}]");
        var client = CreateClient(handler);

        var series = await client.GetAllSeriesAsync(CancellationToken.None);

        var s = Assert.Single(series);
        Assert.Equal(134, s.Id);
        Assert.Equal("The Simpsons", s.Title);
        Assert.Equal(71663, s.TvdbId);
    }

    [Fact]
    public async Task GetEpisodesAsync_RequestsWithSeriesIdQueryParameter_AndDeserializesSeasonEpisodeNumbers()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            "[{\"id\":7958,\"seriesId\":134,\"seasonNumber\":6,\"episodeNumber\":1,\"hasFile\":true,\"episodeFileId\":7958}]");
        var client = CreateClient(handler);

        var episodes = await client.GetEpisodesAsync(seriesId: 134, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("episode?seriesId=134", request.RequestUri!.ToString());

        var episode = Assert.Single(episodes);
        Assert.Equal(6, episode.SeasonNumber);
        Assert.Equal(1, episode.EpisodeNumber);
        Assert.Equal(7958, episode.EpisodeFileId);
    }

    [Fact]
    public async Task DeleteEpisodeFileAsync_SendsDeleteToTheCorrectPath()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.DeleteEpisodeFileAsync(episodeFileId: 7958, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Contains("episodefile/7958", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetHistoryForEpisodeAsync_QueriesTheSeriesScopedEndpoint_FilteredToOneEpisode()
    {
        // Real Sonarr shape: /history/series returns every episode's history
        // for the whole series -- the client must filter client-side.
        var handler = FakeHttpMessageHandler.ReturningJson(@"[
            {""id"":1,""episodeId"":100,""eventType"":""grabbed"",""date"":""2026-08-01T00:00:00Z"",""sourceTitle"":""other episode""},
            {""id"":2,""episodeId"":200,""eventType"":""grabbed"",""date"":""2026-08-02T00:00:00Z"",""sourceTitle"":""target episode""}
        ]");
        var client = CreateClient(handler);

        var history = await client.GetHistoryForEpisodeAsync(seriesId: 134, episodeId: 200, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("history/series?seriesId=134", request.RequestUri!.ToString());

        var record = Assert.Single(history);
        Assert.Equal(200, record.EpisodeId);
        Assert.Equal("target episode", record.SourceTitle);
    }

    [Fact]
    public async Task MarkHistoryAsFailedAsync_PostsToTheCorrectPath()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.MarkHistoryAsFailedAsync(historyId: 2, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("history/failed/2", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task TriggerEpisodeSearchAsync_PostsCommandWithEpisodeIdsArray()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.TriggerEpisodeSearchAsync(episodeId: 7958, CancellationToken.None);

        Assert.Single(handler.Requests);
        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"name\":\"EpisodeSearch\"", body);
        Assert.Contains("\"episodeIds\":[7958]", body);
    }
}
