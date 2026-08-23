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
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Interface for the media integrity scan engine.
/// </summary>
public interface IScanEngine
{
    /// <summary>
    /// Gets a value indicating whether a scan is currently in progress.
    /// </summary>
    bool IsScanning { get; }

    /// <summary>
    /// Gets the <see cref="ScanPhase"/> (as an int) the current library scan is running,
    /// or <c>null</c> when idle. Only ever set for a whole-library scan (<see cref="ScanLibraryAsync"/>)
    /// -- single-item scans via <see cref="ScanItemAsync"/> don't drive this, since "Scanning..."
    /// in the dashboard's status card is about the background library scan, not one-off item checks.
    /// </summary>
    int? CurrentPhase { get; }

    /// <summary>
    /// Gets the library ID the current library scan is scoped to, or <c>null</c>
    /// when idle or when the current scan has no library scope. Lets the
    /// dashboard show what's actually being scanned, not just that a scan is
    /// running -- mirrors <see cref="CurrentPhase"/>'s gating.
    /// </summary>
    string? CurrentLibraryId { get; }

    /// <summary>
    /// Gets the name filter the current library scan is scoped to, or <c>null</c>
    /// when idle or unscoped. See <see cref="CurrentLibraryId"/>.
    /// </summary>
    string? CurrentNameFilter { get; }

    /// <summary>
    /// Gets the season filter the current library scan is scoped to, or
    /// <c>null</c> when idle or unscoped. See <see cref="CurrentLibraryId"/>.
    /// </summary>
    IReadOnlyCollection<int>? CurrentSeasons { get; }

    /// <summary>
    /// Scans a single item for media integrity.
    /// </summary>
    /// <param name="item">The Jellyfin library item to scan.</param>
    /// <param name="phase">The scan phase to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task ScanItemAsync(BaseItem item, ScanPhase phase, CancellationToken cancellationToken);

    /// <summary>
    /// Scans all items in a library, skipping any item that already has a
    /// passing scan result at or above the requested phase.
    /// </summary>
    /// <param name="libraryId">Optional library ID to scope the scan.</param>
    /// <param name="phase">The scan phase to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="progress">Optional progress reporter, updated after each item (scanned or skipped).</param>
    /// <param name="nameFilter">
    /// Optional case-insensitive substring filter. Matched against a movie's own
    /// title, or an episode's series title (not the episode's own title) -- so
    /// typing a show name scopes to every episode of that show, matching how
    /// admins actually think about "just this show", not "just this episode".
    /// </param>
    /// <param name="seasons">
    /// Optional set of season numbers to restrict episodes to (e.g. just seasons
    /// 1-3 of a show). Only applies to TV episodes; movies and other item types
    /// are unaffected by this filter regardless of what's passed here.
    /// </param>
    /// <returns>A task representing the async operation.</returns>
    Task ScanLibraryAsync(
        string? libraryId,
        ScanPhase phase,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null,
        string? nameFilter = null,
        IReadOnlyCollection<int>? seasons = null);

    /// <summary>
    /// Cancels the currently running scan.
    /// </summary>
    void Cancel();
}
