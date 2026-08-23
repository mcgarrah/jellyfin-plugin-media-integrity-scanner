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
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// Shared HTTP plumbing for <see cref="Radarr.RadarrClient"/> and
/// <see cref="Sonarr.SonarrClient"/> -- both target apps expose the same
/// Servarr-framework REST shape (API-key header, <c>/api/v3</c> prefix,
/// JSON bodies), so the transport concerns live here once. Modeled on
/// Seerr's own shared-base-class pattern (<c>ServarrBase</c>, see
/// <c>ARR-INTEGRATION-PROPOSAL.md</c> section 2.1/6.2), but with a real
/// request timeout -- Seerr's client (and JellyGlance's own ad hoc HTTP
/// calls) leave this unbounded, which two of that doc's cited GitHub issues
/// trace back to real "a service is unreachable and nothing ever recovers"
/// bugs. Not repeating that here.
/// </summary>
public abstract class ArrClientBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Radarr/Sonarr's REST APIs use camelCase JSON (e.g. <c>"movieId"</c>,
    /// <c>"eventType"</c>) -- confirmed against this user's real live
    /// instances, and the opposite convention from Jellyfin's own API (see
    /// this project's AGENTS.md: Jellyfin is always PascalCase). Every model
    /// class in <see cref="Radarr"/>/<see cref="Sonarr"/> uses plain
    /// PascalCase C# property names; this options instance is what maps
    /// them to/from the real camelCase wire format, for both directions
    /// (deserializing responses and serializing request bodies).
    /// </summary>
    private static readonly JsonSerializerOptions ArrJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClientBase"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for creating the underlying <see cref="HttpClient"/>. Jellyfin's own host already registers this (confirmed against its <c>Startup.cs</c>); no new DI registration is needed for this plugin to use it.</param>
    /// <param name="baseUrl">The configured server's base URL, e.g. <c>http://192.168.86.52:7878</c>.</param>
    /// <param name="apiKey">The configured server's API key.</param>
    /// <param name="logger">Logger instance, for subclasses' own logging.</param>
    protected ArrClientBase(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _http = httpClientFactory.CreateClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/v3/");
        _http.Timeout = RequestTimeout;
        _apiKey = apiKey;
        Logger = logger;
    }

    /// <summary>Gets the logger passed to this instance, for subclass use.</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Sends a GET request and deserializes the JSON response.
    /// </summary>
    /// <typeparam name="T">The response type.</typeparam>
    /// <param name="path">The request path, relative to <c>/api/v3/</c> (e.g. <c>"movie"</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized response.</returns>
    protected async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        return await SendAsync<T>(HttpMethod.Get, path, requestBody: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a POST request with a JSON body and deserializes the JSON response.
    /// </summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="path">The request path, relative to <c>/api/v3/</c>.</param>
    /// <param name="requestBody">The request body, serialized as JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized response.</returns>
    protected async Task<TResponse?> PostAsync<TResponse>(string path, object requestBody, CancellationToken cancellationToken)
    {
        return await SendAsync<TResponse>(HttpMethod.Post, path, requestBody, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a POST request with a JSON body, discarding the response body
    /// (used for command/action endpoints where the response isn't needed).
    /// </summary>
    /// <param name="path">The request path, relative to <c>/api/v3/</c>.</param>
    /// <param name="requestBody">The request body, serialized as JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    protected async Task PostAsync(string path, object requestBody, CancellationToken cancellationToken)
    {
        await SendAsync<object>(HttpMethod.Post, path, requestBody, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a DELETE request.
    /// </summary>
    /// <param name="path">The request path, relative to <c>/api/v3/</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    protected async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        await SendAsync<object>(HttpMethod.Delete, path, requestBody: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? requestBody, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Api-Key", _apiKey);
            if (requestBody is not null)
            {
                request.Content = JsonContent.Create(requestBody, options: ArrJsonOptions);
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (typeof(T) == typeof(object))
            {
                // Caller doesn't need the response body (PostAsync/DeleteAsync's
                // void-returning overloads) -- avoid deserializing a body that
                // may not even be valid JSON for these endpoints.
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>(ArrJsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Contextual message, original preserved as InnerException --
            // Seerr's exact pattern (ARR-INTEGRATION-PROPOSAL.md section 2.1).
            throw new ArrClientException($"[{GetType().Name}] {method} {path} failed: {ex.Message}", ex);
        }
    }
}
