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

using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// Builds <see cref="IRadarrClient"/>/<see cref="ISonarrClient"/> instances
/// for a specific configured server. A factory rather than DI-registering
/// the clients directly, since a server's URL/API key are runtime plugin
/// configuration (<see cref="ArrServerConfig"/>), not known at DI
/// registration time -- and Phase 3 will need to create a different client
/// per configured server, not just one fixed instance.
/// </summary>
public interface IArrClientFactory
{
    /// <summary>Creates a Radarr client for the given server configuration.</summary>
    /// <param name="config">The configured server to connect to.</param>
    /// <returns>A ready-to-use Radarr client.</returns>
    IRadarrClient CreateRadarrClient(ArrServerConfig config);

    /// <summary>Creates a Sonarr client for the given server configuration.</summary>
    /// <param name="config">The configured server to connect to.</param>
    /// <returns>A ready-to-use Sonarr client.</returns>
    ISonarrClient CreateSonarrClient(ArrServerConfig config);
}
