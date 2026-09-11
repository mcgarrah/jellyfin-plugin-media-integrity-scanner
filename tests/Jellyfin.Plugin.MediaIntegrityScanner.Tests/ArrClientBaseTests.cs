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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Minimal concrete subclass exposing <see cref="ArrClientBase"/>'s protected
/// transport methods directly, so its shared behavior (JSON options,
/// exception wrapping, the void-response short-circuit) can be tested without
/// going through <see cref="ArrIntegration.Radarr.RadarrClient"/>/
/// <see cref="ArrIntegration.Sonarr.SonarrClient"/>'s own endpoint-specific
/// wrappers.
/// </summary>
internal sealed class TestArrClient : ArrClientBase
{
    public TestArrClient(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey)
        : base(httpClientFactory, baseUrl, apiKey, NullLogger<TestArrClient>.Instance)
    {
    }

    public new Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken) => base.GetAsync<T>(path, cancellationToken);

    public new Task<T?> PostAsync<T>(string path, object body, CancellationToken cancellationToken) => base.PostAsync<T>(path, body, cancellationToken);

    public new Task PostAsync(string path, object body, CancellationToken cancellationToken) => base.PostAsync(path, body, cancellationToken);

    public new Task DeleteAsync(string path, CancellationToken cancellationToken) => base.DeleteAsync(path, cancellationToken);
}

internal sealed class TestArrModel
{
    public int SomeValue { get; set; }
}

internal sealed class TestArrRequestBody
{
    public string SomeField { get; set; } = string.Empty;
}

/// <summary>
/// Tests for <see cref="ArrClientBase"/>'s shared transport logic, using
/// <see cref="TestArrClient"/> against a <see cref="FakeHttpMessageHandler"/>.
/// <see cref="RadarrClientTests"/>/<see cref="SonarrClientTests"/> already
/// cover this indirectly for their own endpoints; this class targets the
/// base class's own behavior directly instead of as a side effect of testing
/// something else.
/// </summary>
public class ArrClientBaseTests
{
    private static TestArrClient CreateClient(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        return new TestArrClient(factory.Object, "http://arr.local:7878", "test-api-key");
    }

    [Fact]
    public async Task GetAsync_DeserializesCamelCaseJson_IntoPascalCaseProperty()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("{\"someValue\":42}");
        var client = CreateClient(handler);

        var result = await client.GetAsync<TestArrModel>("some/path", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(42, result!.SomeValue);
    }

    [Fact]
    public async Task PostAsync_SerializesRequestBody_AsCamelCaseJson()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.PostAsync("some/path", new TestArrRequestBody { SomeField = "value" }, CancellationToken.None);

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"someField\":\"value\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SomeField", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_SendsApiKeyHeader()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("{\"someValue\":1}");
        var client = CreateClient(handler);

        await client.GetAsync<TestArrModel>("some/path", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.True(request.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("test-api-key", Assert.Single(values!));
    }

    [Fact]
    public async Task PostAsync_VoidOverload_DoesNotAttemptToDeserializeNonJsonResponseBody()
    {
        // Command/action endpoints (blocklist, search-trigger, etc.) don't
        // necessarily return a JSON body -- the void-returning PostAsync/
        // DeleteAsync overloads must short-circuit on typeof(T) == typeof(object)
        // rather than attempt (and fail) to parse whatever comes back.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not valid json", System.Text.Encoding.UTF8, "text/plain")
        });
        var client = CreateClient(handler);

        await client.PostAsync("some/path", new TestArrRequestBody(), CancellationToken.None);

        // No exception means the short-circuit worked; nothing further to assert.
    }

    [Fact]
    public async Task DeleteAsync_DoesNotAttemptToDeserializeNonJsonResponseBody()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not valid json", System.Text.Encoding.UTF8, "text/plain")
        });
        var client = CreateClient(handler);

        await client.DeleteAsync("some/path", CancellationToken.None);
    }

    [Fact]
    public async Task GetAsync_NonSuccessStatusCode_ThrowsArrClientException_WrappingHttpRequestException()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ArrClientException>(
            () => client.GetAsync<TestArrModel>("some/path", CancellationToken.None));

        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Contains("[TestArrClient]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GET", ex.Message, StringComparison.Ordinal);
        Assert.Contains("some/path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_ConnectionFailure_ThrowsArrClientException_WrappingOriginalException()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ArrClientException>(
            () => client.GetAsync<TestArrModel>("some/path", CancellationToken.None));

        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal("Connection refused", ex.InnerException!.Message);
    }

    [Fact]
    public async Task GetAsync_OperationCanceled_PropagatesUnwrapped_NotAsArrClientException()
    {
        // ArrClientBase's catch is explicitly `when (ex is not OperationCanceledException)`
        // -- a real HttpClient.Timeout expiring throws (a subclass of)
        // OperationCanceledException, and callers up the stack (see
        // ArrRemediationWorker's per-tick circuit breaker) rely on this NOT
        // being wrapped, since it's the signal that the whole request timed
        // out rather than completed with an error response.
        var handler = new FakeHttpMessageHandler(_ => throw new OperationCanceledException("Timed out"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.GetAsync<TestArrModel>("some/path", CancellationToken.None));

        Assert.Equal("Timed out", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_SendsCorrectHttpMethod()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.DeleteAsync("some/path", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
    }
}
