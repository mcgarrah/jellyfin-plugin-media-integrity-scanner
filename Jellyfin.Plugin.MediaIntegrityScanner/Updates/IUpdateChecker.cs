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

using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Updates;

/// <summary>
/// Checks for and installs plugin updates via Jellyfin's own
/// <c>IInstallationManager</c>, classifying discovered versions into a
/// stable or development channel by which registered plugin repository they
/// came from.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Gets the most recently computed update status, or <see langword="null"/>
    /// if <see cref="RefreshAsync"/> has never been called.
    /// </summary>
    UpdateStatus? CachedStatus { get; }

    /// <summary>
    /// Re-queries Jellyfin's installation manager for available versions of
    /// this plugin and updates <see cref="CachedStatus"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The freshly computed status.</returns>
    Task<UpdateStatus> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Installs the latest available version for the given channel by
    /// calling Jellyfin's own plugin installation API -- the same mechanism
    /// Dashboard &gt; Plugins &gt; Catalog uses. Jellyfin stages the update on
    /// disk; whether it applies immediately or requires a server restart is
    /// Jellyfin's own behavior, not something this method controls.
    /// </summary>
    /// <param name="channel">Which channel's latest version to install.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the install has been requested.</returns>
    Task InstallAsync(UpdateChannel channel, CancellationToken cancellationToken);
}
