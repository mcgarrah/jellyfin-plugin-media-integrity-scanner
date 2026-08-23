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

using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="RadarrClient"/> against a fake
/// <see cref="HttpMessageHandler"/> -- verifies request shape (method, path,
/// headers, body), camelCase response deserialization (Radarr's real wire
/// format, confirmed live -- see <c>ARR-INTEGRATION-PROPOSAL.md</c>), and
/// the contextual error wrapping from <see cref="ArrClientBase"/>.
/// </summary>
public class RadarrClientTests
{
    private static RadarrClient CreateClient(FakeHttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        return new RadarrClient(factory.Object, "http://radarr.local:7878", "test-api-key", NullLogger<RadarrClient>.Instance);
    }

    [Fact]
    public async Task GetAllMoviesAsync_DeserializesCamelCaseFields_IntoPascalCaseProperties()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            "[{\"id\":1,\"title\":\"Sintel\",\"tmdbId\":45745,\"imdbId\":\"tt1727587\",\"hasFile\":true,\"movieFileId\":1,\"movieFile\":{\"id\":1,\"movieId\":1,\"path\":\"/movies/Sintel/Sintel.mkv\",\"relativePath\":\"Sintel.mkv\"}}]");
        var client = CreateClient(handler);

        var movies = await client.GetAllMoviesAsync(CancellationToken.None);

        var movie = Assert.Single(movies);
        Assert.Equal(1, movie.Id);
        Assert.Equal("Sintel", movie.Title);
        Assert.Equal(45745, movie.TmdbId);
        Assert.True(movie.HasFile);
        Assert.NotNull(movie.MovieFile);
        Assert.Equal("/movies/Sintel/Sintel.mkv", movie.MovieFile!.Path);
    }

    [Fact]
    public async Task GetAllMoviesAsync_SendsApiKeyHeader_OnEveryRequest()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("[]");
        var client = CreateClient(handler);

        await client.GetAllMoviesAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.True(request.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("test-api-key", Assert.Single(values!));
    }

    [Fact]
    public async Task GetAllMoviesAsync_RequestsTheCorrectPath_UnderApiV3()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("[]");
        var client = CreateClient(handler);

        await client.GetAllMoviesAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://radarr.local:7878/api/v3/movie", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task SearchReleasesAsync_DeserializesRejectionReasons()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            "[{\"title\":\"Sintel.2010.1080p.WEB\",\"indexer\":\"NZBgeek\",\"rejected\":true,\"rejections\":[\"Existing file meets cutoff\"]}]");
        var client = CreateClient(handler);

        var releases = await client.SearchReleasesAsync(movieId: 1, CancellationToken.None);

        var release = Assert.Single(releases);
        Assert.True(release.Rejected);
        Assert.Equal("Existing file meets cutoff", Assert.Single(release.Rejections));
    }

    [Fact]
    public async Task SearchReleasesAsync_RequestsWithMovieIdQueryParameter()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("[]");
        var client = CreateClient(handler);

        await client.SearchReleasesAsync(movieId: 42, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("release?movieId=42", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task DeleteMovieFileAsync_SendsDeleteToTheCorrectPath()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.DeleteMovieFileAsync(movieFileId: 99, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Contains("moviefile/99", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetHistoryForMovieAsync_DeserializesEventTypeAsAString()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(
            "[{\"id\":40,\"movieId\":1,\"eventType\":\"grabbed\",\"date\":\"2026-08-22T10:20:41Z\",\"sourceTitle\":\"Sintel.2010.1080p.WEB\"}]");
        var client = CreateClient(handler);

        var history = await client.GetHistoryForMovieAsync(movieId: 1, CancellationToken.None);

        var record = Assert.Single(history);
        Assert.Equal(40, record.Id);
        Assert.Equal("grabbed", record.EventType);
    }

    [Fact]
    public async Task MarkHistoryAsFailedAsync_PostsToTheCorrectPath()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.MarkHistoryAsFailedAsync(historyId: 40, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("history/failed/40", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task TriggerMovieSearchAsync_PostsCommandWithMovieIdsArray()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.TriggerMovieSearchAsync(movieId: 7, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("command", request.RequestUri!.ToString());
        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"name\":\"MoviesSearch\"", body);
        Assert.Contains("\"movieIds\":[7]", body);
    }

    [Fact]
    public async Task AnyFailedRequest_ThrowsArrClientException_WithAContextualMessage()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ArrClientException>(() => client.GetAllMoviesAsync(CancellationToken.None));

        Assert.Contains("RadarrClient", ex.Message);
        Assert.Contains("movie", ex.Message);
        Assert.NotNull(ex.InnerException);
    }
}
