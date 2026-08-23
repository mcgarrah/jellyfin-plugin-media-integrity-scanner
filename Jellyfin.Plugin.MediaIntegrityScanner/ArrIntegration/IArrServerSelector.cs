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

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// Picks which configured Radarr/Sonarr server a given Jellyfin item's file
/// should route to, when more than one server of that type is configured
/// (Phase 3, <c>ARR-INTEGRATION-PROPOSAL.md</c> section 11's "multi-server
/// support" -- the real Disney/Family-server split this was built for).
/// </summary>
public interface IArrServerSelector
{
    /// <summary>
    /// Selects the server <paramref name="itemPath"/> should route to.
    /// </summary>
    /// <param name="servers">Every configured server of one type (all Radarr, or all Sonarr).</param>
    /// <param name="itemPath">The Jellyfin item's file path, as Jellyfin sees it.</param>
    /// <returns>
    /// <c>null</c> if <paramref name="servers"/> is empty; the single entry if there's
    /// exactly one (no path-prefix configuration needed for the common single-server
    /// case); otherwise the first server whose <see cref="ArrServerConfig.LibraryPathPrefixes"/>
    /// contains a prefix <paramref name="itemPath"/> starts with, or the first configured
    /// server if none match (rather than leaving the item unroutable on a misconfiguration).
    /// </returns>
    ArrServerConfig? SelectForPath(IReadOnlyList<ArrServerConfig> servers, string itemPath);
}
