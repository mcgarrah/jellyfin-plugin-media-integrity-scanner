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
using System.Linq;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <inheritdoc cref="IArrServerSelector" />
public class ArrServerSelector : IArrServerSelector
{
    /// <inheritdoc />
    public ArrServerConfig? SelectForPath(IReadOnlyList<ArrServerConfig> servers, string itemPath)
    {
        if (servers is null || servers.Count == 0)
        {
            return null;
        }

        if (servers.Count == 1)
        {
            return servers[0];
        }

        var match = servers.FirstOrDefault(s => s.LibraryPathPrefixes.Any(prefix => PathStartsWithPrefix(itemPath, prefix)));
        return match ?? servers[0];
    }

    /// <summary>
    /// True if <paramref name="itemPath"/> is under the directory
    /// <paramref name="prefix"/> denotes -- a real directory-boundary check
    /// (via a trailing separator or an exact match), not a naive
    /// <c>string.StartsWith</c> that would let <c>/data/movies2</c> falsely
    /// match a configured prefix of <c>/data/movies</c>.
    /// </summary>
    private static bool PathStartsWithPrefix(string itemPath, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        var normalizedItem = itemPath.Replace('\\', '/');
        var normalizedPrefix = prefix.Replace('\\', '/').TrimEnd('/');

        return normalizedItem.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
            || normalizedItem.StartsWith(normalizedPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }
}
