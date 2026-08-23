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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Shared name/season scope filtering, applied identically by both
/// <see cref="ScanEngine"/> (what actually gets scanned) and
/// <c>MediaIntegrityController</c> (what the Results table shows) so the two
/// never silently drift apart -- "what will be scanned" and "what am I
/// looking at" should always mean the same set of items for a given scope.
/// </summary>
public static class ScanScopeFilter
{
    /// <summary>
    /// Applies an optional name filter and/or season filter to a list of
    /// items, in that order. Either or both may be omitted.
    /// </summary>
    /// <param name="items">The candidate items (already library/parent-scoped).</param>
    /// <param name="nameFilter">Optional case-insensitive substring filter -- see <see cref="MatchesNameFilter"/>.</param>
    /// <param name="seasons">Optional set of season numbers to restrict TV episodes to.</param>
    /// <returns>The filtered item list.</returns>
    public static IReadOnlyList<BaseItem> Apply(IReadOnlyList<BaseItem> items, string? nameFilter, IReadOnlyCollection<int>? seasons)
    {
        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            items = items.Where(item => MatchesNameFilter(item, nameFilter)).ToList();
        }

        if (seasons is { Count: > 0 })
        {
            var seasonSet = new HashSet<int>(seasons);
            items = items.Where(item => item is not Episode episode || (episode.ParentIndexNumber.HasValue && seasonSet.Contains(episode.ParentIndexNumber.Value))).ToList();
        }

        return items;
    }

    /// <summary>
    /// Checks whether an item matches a scan-scope name filter. Episodes are
    /// matched against their series title, not their own episode title --
    /// scoping by name means "just this show", and an episode's own
    /// <see cref="BaseItem.Name"/> would only ever match one specific
    /// episode by coincidence.
    /// </summary>
    private static bool MatchesNameFilter(BaseItem item, string nameFilter)
    {
        var name = item is Episode episode ? episode.SeriesName : item.Name;
        return !string.IsNullOrEmpty(name) && name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
    }
}
