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

using System.Net.Http;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <inheritdoc cref="IArrClientFactory" />
public class ArrClientFactory : IArrClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClientFactory"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the underlying <see cref="HttpClient"/>, passed through to each created client.</param>
    /// <param name="loggerFactory">Factory for each created client's typed logger.</param>
    public ArrClientFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public IRadarrClient CreateRadarrClient(ArrServerConfig config)
    {
        return new RadarrClient(_httpClientFactory, config.Url, config.ApiKey, _loggerFactory.CreateLogger<RadarrClient>());
    }

    /// <inheritdoc />
    public ISonarrClient CreateSonarrClient(ArrServerConfig config)
    {
        return new SonarrClient(_httpClientFactory, config.Url, config.ApiKey, _loggerFactory.CreateLogger<SonarrClient>());
    }
}
