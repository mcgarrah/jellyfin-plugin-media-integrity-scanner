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
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// Orchestrates one full remediation attempt for a bad file: matching it to
/// Radarr/Sonarr, the pre-flight availability check, delete-then-blocklist
/// (or the plain-search fallback), and recording the outcome. Implements the
/// flow from <c>ARR-INTEGRATION-PROPOSAL.md</c> section 4.
///
/// Phase 1 only supports manual, one-at-a-time triggering (see
/// <c>MediaIntegrityController.TriggerArrRemediation</c>) -- there is no
/// background worker or automatic forwarding yet (that's Phase 2).
/// </summary>
public interface IArrRemediationService
{
    /// <summary>
    /// Runs the full remediation flow for a single Jellyfin item (a movie or
    /// TV episode -- other item types return an immediate "unmatched"
    /// result, since only those two are Radarr/Sonarr-managed).
    /// </summary>
    /// <param name="item">The Jellyfin item to remediate.</param>
    /// <param name="scanRecordId">The originating scan_results row ID, if this was triggered from a specific failed scan result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed remediation record, already persisted.</returns>
    Task<ArrRemediationRecord> RemediateAsync(BaseItem item, long? scanRecordId, CancellationToken cancellationToken);
}
