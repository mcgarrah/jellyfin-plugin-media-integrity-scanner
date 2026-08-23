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
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// A fake <see cref="HttpMessageHandler"/> for testing the ArrIntegration
/// clients without a real HTTP server -- records every request it sees and
/// returns a scripted response, following the standard .NET pattern for
/// testing code built on <see cref="HttpClient"/>/<see cref="IHttpClientFactory"/>.
/// </summary>
public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    /// <summary>Gets every request this handler has seen, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>
    /// Gets each request's body content, captured eagerly at request time
    /// and indexed the same as <see cref="Requests"/> -- the caller (see
    /// <see cref="ArrClientBase"/>) disposes each <see cref="HttpRequestMessage"/>
    /// (and its content) right after sending, so reading <c>Content</c> off
    /// a captured request afterward throws <see cref="ObjectDisposedException"/>;
    /// this list is read from the content *before* that disposal happens.
    /// </summary>
    public List<string?> RequestBodies { get; } = new();

    public static FakeHttpMessageHandler ReturningJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new FakeHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
    }

    public static FakeHttpMessageHandler ReturningStatus(HttpStatusCode statusCode)
    {
        return new FakeHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return _respond(request);
    }
}
